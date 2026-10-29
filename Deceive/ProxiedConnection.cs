using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Threading;
using System.Diagnostics;
using System.IO;
using System.Net.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace Deceive;

internal class ProxiedConnection
{
    private MainController MainController { get; set; }

    private SslStream Incoming { get; set; } = null!;
    private SslStream Outgoing { get; set; } = null!;
    private bool Connected { get; set; } = true;
    private string LastPresence { get; set; } = null!; // we resend this if the state changes
    private bool InsertedFakePlayer { get; set; } = false;
    private bool SentFakePlayerPresence { get; set; } = false;
    private string? ValorantVersion { get; set; } = null;

    // Writes to each stream can come from several tasks (proxy loops, tray actions, settings reloads).
    private readonly SemaphoreSlim _incomingWriteLock = new(1, 1);
    private readonly SemaphoreSlim _outgoingWriteLock = new(1, 1);

    // Friends we have sent our real presence to directly (the "visibleTo" list).
    private HashSet<string> _directedTo = new(StringComparer.OrdinalIgnoreCase);
    private readonly FriendDirectory.Parser _rosterParser = new();

    internal event EventHandler? ConnectionErrored;

    internal ProxiedConnection(MainController main, SslStream incoming, SslStream outgoing)
    {
        MainController = main;
        Incoming = incoming;
        Outgoing = outgoing;
    }

    public void Start()
    {
        Task.Run(IncomingLoopAsync);
        Task.Run(OutgoingLoopAsync);
    }

    private async Task IncomingLoopAsync()
    {
        try
        {
            int byteCount;
            var bytes = new byte[8192 * 2];

            do
            {
                byteCount = await Incoming.ReadAsync(bytes, 0, bytes.Length);

                var content = Encoding.UTF8.GetString(bytes, 0, byteCount);

                // Notice when you enter agent select or a match (read only; changes nothing).
                if (content.Contains("<presence"))
                    ValorantPresence.ObserveOwn(content);

                // If this is possibly a presence stanza, rewrite it.
                if (content.Contains("<presence") && MainController.Enabled)
                {
                    Trace.WriteLine("<!--RC TO SERVER ORIGINAL-->" + content);
                    await PossiblyRewriteAndResendPresenceAsync(content, MainController.Status);
                }
                else if (content.Contains("41c322a1-b328-495b-a004-5ccd3e45eae8@eu1.pvp.net"))
                {
                    await MainController.HandleChatMessage(content);

                    //Don't send anything involving our fake user to chat servers
                    Trace.WriteLine("<!--RC TO SERVER REMOVED-->" + content);
                }
                else
                {
                    await WriteOutgoingAsync(bytes, 0, byteCount);
                    // don't log anything that contains a JWT
                    if (!content.Contains("token>")) Trace.WriteLine("<!--RC TO SERVER-->" + content);
                }

                if (InsertedFakePlayer && !SentFakePlayerPresence)
                    await SendFakePlayerPresenceAsync();
            } while (byteCount != 0 && Connected);
        }
        catch (Exception e)
        {
            Trace.WriteLine("Incoming errored.");
            Trace.WriteLine(e);
        }
        finally
        {
            Trace.WriteLine("Incoming closed.");
            OnConnectionErrored();
        }
    }

    private async Task OutgoingLoopAsync()
    {
        try
        {
            int byteCount;
            var bytes = new byte[8192 * 2];

            do
            {
                byteCount = await Outgoing.ReadAsync(bytes, 0, bytes.Length);
                var content = Encoding.UTF8.GetString(bytes, 0, byteCount);

                // Insert fake player into roster
                const string roster = "<query xmlns='jabber:iq:riotgames:roster'>";
                if (!InsertedFakePlayer && content.Contains(roster))
                {
                    InsertedFakePlayer = true;
                    Trace.WriteLine("<!--SERVER TO RC ORIGINAL-->" + content);
                    await LearnRosterAsync(content);
                    var settings = AppSettings.Current;
                    var fakeName = "&#9;" + SecurityElement.Escape(settings.FriendName);
                    var fakeGroup = SecurityElement.Escape(settings.FriendGroup);
                    content = content.Insert(content.IndexOf(roster, StringComparison.Ordinal) + roster.Length,
                        $"<item jid='41c322a1-b328-495b-a004-5ccd3e45eae8@eu1.pvp.net' name='{fakeName}' subscription='both' puuid='41c322a1-b328-495b-a004-5ccd3e45eae8'>" +
                        $"<group priority='9999'>{fakeGroup}</group>" +
                        "<state>online</state>" +
                        $"<id name='{fakeName}' tagline='...'/>" +
                        $"<lol name='{fakeName}'/>" +
                        $"<platforms><riot name='{fakeName}' tagline='...'/></platforms>" +
                        "</item>");
                    var contentBytes = Encoding.UTF8.GetBytes(content);
                    await WriteIncomingAsync(contentBytes, 0, contentBytes.Length);
                    Trace.WriteLine("<!--DECEIVE TO RC-->" + content);
                }
                else
                {
                    await WriteIncomingAsync(bytes, 0, byteCount);
                    Trace.WriteLine("<!--SERVER TO RC-->" + content);
                    await LearnRosterAsync(content);
                }
            } while (byteCount != 0 && Connected);
        }
        catch (Exception e)
        {
            Trace.WriteLine("Outgoing errored.");
            Trace.WriteLine(e);
        }
        finally
        {
            Trace.WriteLine("Outgoing closed.");
            OnConnectionErrored();
        }
    }

    /// <summary>
    /// Records friends from roster data. If a friend on the visibleTo list shows up for the
    /// first time, re-send presence so they get our real status.
    /// </summary>
    private async Task LearnRosterAsync(string content)
    {
        try
        {
            var added = _rosterParser.Feed(content);
            if (added.Count == 0 || string.IsNullOrEmpty(LastPresence) || !MainController.Enabled || MainController.Status == "chat")
                return;

            var wanted = FriendDirectory.Resolve(AppSettings.Current.VisibleTo, out _, out _);
            if (added.Any(jid => wanted.Contains(jid, StringComparer.OrdinalIgnoreCase) && !_directedTo.Contains(jid)))
                await PossiblyRewriteAndResendPresenceAsync(LastPresence, MainController.Status);
        }
        catch (Exception e)
        {
            Trace.WriteLine("Error reading roster: " + e);
        }
    }

    private async Task PossiblyRewriteAndResendPresenceAsync(string content, string targetStatus)
    {
        try
        {
            LastPresence = content;
            var wrappedContent = "<xml>" + content + "</xml>";
            var xml = XDocument.Load(new StringReader(wrappedContent));

            if (xml.Root is null)
                return;
            if (xml.Root.HasElements is false)
                return;

            // Keep untouched copies of the broadcast presence(s) for friends on the visibleTo list.
            var realPresences = xml.Root.Elements("presence")
                .Where(p => p.Attribute("to") is null && p.Attribute("type") is null)
                .Select(p => new XElement(p))
                .ToList();

            foreach (var presence in xml.Root.Elements())
            {
                if (presence.Name != "presence")
                    continue;
                if (presence.Attribute("to") is not null)
                {
                    if (MainController.ConnectToMuc)
                        continue;
                    presence.Remove();
                }

                if (targetStatus != "chat" ||
                    presence.Element("games")?.Element("league_of_legends")?.Element("st")?.Value != "dnd")
                {
                    presence.Element("show")?.ReplaceNodes(targetStatus);
                    presence.Element("games")?.Element("league_of_legends")?.Element("st")?.ReplaceNodes(targetStatus);
                }

                if (targetStatus == "chat")
                    continue;
                presence.Element("status")?.Remove();

                if (targetStatus == "mobile")
                {
                    presence.Element("games")?.Element("league_of_legends")?.Element("p")?.Remove();
                    presence.Element("games")?.Element("league_of_legends")?.Element("m")?.Remove();
                }
                else
                {
                    presence.Element("games")?.Element("league_of_legends")?.Remove();
                }

                // Remove Legends of Runeterra presence
                presence.Element("games")?.Element("bacon")?.Remove();

                // Remove 2XKO presence
                presence.Element("games")?.Element("lion")?.Remove();

                // Remove Riot Client presence
                presence.Element("games")?.Element("keystone")?.Remove();
                presence.Element("games")?.Element("riot_client")?.Remove();

                // Remove Teamfight Tactics presence
                presence.Element("games")?.Element("teamfighttactics")?.Remove();

                // Extracts current VALORANT from the user's own presence, so that we can show a fake
                // player with the proper version and avoid "Version Mismatch" from being shown.
                //
                // This isn't technically necessary, but people keep coming in and asking whether
                // the scary red text means Deceive doesn't work, so might as well do this and
                // get a slightly better user experience.
                if (ValorantVersion is null)
                {
                    var valorantBase64 = presence.Element("games")?.Element("valorant")?.Element("p")?.Value;
                    if (!string.IsNullOrWhiteSpace(valorantBase64))
                    {
                        var valorantPresence = Encoding.UTF8.GetString(Convert.FromBase64String(valorantBase64));
                        var valorantJson = JsonSerializer.Deserialize<JsonNode>(valorantPresence);
                        ValorantVersion = valorantJson?["partyPresenceData"]?["partyClientVersion"]?.GetValue<string>();
                        Trace.WriteLine("Found VALORANT version: " + ValorantVersion);
                        // only resend
                        if (InsertedFakePlayer && ValorantVersion is not null)
                            await SendFakePlayerPresenceAsync();
                    }
                }

                // Remove VALORANT presence
                presence.Element("games")?.Element("valorant")?.Remove();
            }

            var sb = new StringBuilder();
            var xws = new XmlWriterSettings
            {
                OmitXmlDeclaration = true, Encoding = Encoding.UTF8, ConformanceLevel = ConformanceLevel.Fragment,
                Async = true
            };
            using (var xw = XmlWriter.Create(sb, xws))
            {
                foreach (var xElement in xml.Root.Elements())
                    xElement.WriteTo(xw);
            }

            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            await WriteOutgoingAsync(bytes, 0, bytes.Length);
            Trace.WriteLine("<!--DECEIVE TO SERVER-->" + sb);

            await SendDirectedPresencesAsync(xml, realPresences, targetStatus, xws);
        }
        catch (Exception e)
        {
            Trace.WriteLine(e);
            Trace.WriteLine("Error rewriting presence.");
        }
    }

    /// <summary>
    /// While masked (offline/mobile), sends our real presence directly to friends on the visibleTo
    /// list. This goes out after the masked broadcast, so for those friends it is the latest
    /// presence and wins. Friends removed from the list get the masked presence again.
    /// </summary>
    private async Task SendDirectedPresencesAsync(XDocument maskedXml, List<XElement> realPresences, string targetStatus, XmlWriterSettings xws)
    {
        if (realPresences.Count == 0)
            return;

        var wanted = targetStatus == "chat"
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(FriendDirectory.Resolve(AppSettings.Current.VisibleTo, out _, out _), StringComparer.OrdinalIgnoreCase);

        var removed = _directedTo.Where(j => !wanted.Contains(j)).ToList();
        if (wanted.Count == 0 && removed.Count == 0)
        {
            _directedTo = wanted;
            return;
        }

        var maskedPresences = maskedXml.Root!.Elements("presence")
            .Where(p => p.Attribute("to") is null && p.Attribute("type") is null)
            .ToList();

        var sb = new StringBuilder();
        using (var xw = XmlWriter.Create(sb, xws))
        {
            var n = 0;
            void Write(XElement source, string jid)
            {
                var copy = new XElement(source);
                copy.SetAttributeValue("to", jid);
                var id = copy.Attribute("id")?.Value;
                copy.SetAttributeValue("id", (string.IsNullOrEmpty(id) ? "expr" : id) + "-v" + n++);
                copy.WriteTo(xw);
            }

            // Only friends who left the list need the masked status re-sent (when going online,
            // the normal broadcast already reaches everyone).
            if (targetStatus != "chat")
                foreach (var jid in removed)
                foreach (var p in maskedPresences)
                    Write(p, jid);

            foreach (var jid in wanted)
            foreach (var p in realPresences)
                Write(p, jid);
        }

        _directedTo = wanted;

        if (sb.Length == 0)
            return;

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        await WriteOutgoingAsync(bytes, 0, bytes.Length);
        Trace.WriteLine("<!--DECEIVE TO SERVER (visibleTo)-->" + sb);
    }

    private async Task SendFakePlayerPresenceAsync()
    {
        SentFakePlayerPresence = true;
        // VALORANT requires a recent version to not display "Version Mismatch"
        var valorantPresence = Convert.ToBase64String(
            Encoding.UTF8.GetBytes("""
                                              {
                                               "isValid": true,
                                               "isIdle": false,
                                               "queueId": "competitive",
                                               "provisioningFlow": "Invalid",
                                               "partyId": "00000000-0000-0000-0000-000000000000",
                                               "partySize": 1,
                                               "maxPartySize": 5,
                                               "partyOwnerMatchScoreAllyTeam": 0,
                                               "partyOwnerMatchScoreEnemyTeam": 0,
                                               "premierPresenceData":
                                               {
                                                   "rosterId": "",
                                                   "rosterName": "",
                                                   "rosterTag": "",
                                                   "rosterType": "",
                                                   "division": 0,
                                                   "score": 0,
                                                   "plating": 0,
                                                   "showAura": false,
                                                   "showTag": false,
                                                   "showPlating": false
                                               },
                                               "matchPresenceData":
                                               {
                                                   "sessionLoopState": "MENUS",
                                                   "provisioningFlow": "Invalid",
                                                   "matchMap": "",
                                                   "queueId": "competitive"
                                               },
                                               "partyPresenceData":
                                               {
                                                   "partyId": "00000000-0000-0000-0000-000000000000",
                                                   "isPartyOwner": true,
                                                   "partyState": "DEFAULT",
                                                   "partyAccessibility": "CLOSED",
                                                   "partyLFM": false,
                                                   "partyClientVersion": "{VERSION}",
                                                   "partyVersion": 1768830115681,
                                                   "partySize": 1,
                                                   "queueEntryTime": "0001.01.01-00.00.00",
                                                   "isPartyCrossPlayEnabled": false,
                                                   "isPlayerCrossPlayEnabled": false,
                                                   "partyPrecisePlatformTypes": 1,
                                                   "customGameName": "",
                                                   "customGameTeam": "",
                                                   "maxPartySize": 5,
                                                   "tournamentId": "",
                                                   "rosterId": "",
                                                   "partyOwnerSessionLoopState": "MENUS",
                                                   "partyOwnerMatchMap": "",
                                                   "partyOwnerProvisioningFlow": "Invalid",
                                                   "partyOwnerMatchScoreAllyTeam": 0,
                                                   "partyOwnerMatchScoreEnemyTeam": 0
                                               },
                                               "playerPresenceData":
                                               {
                                                   "playerCardId": "{CARD}",
                                                   "playerTitleId": "{TITLE}",
                                                   "accountLevel": {LEVEL},
                                                   "competitiveTier": 0,
                                                   "leaderboardPosition": 0
                                               }
                                              }
                                   """.Replace("{VERSION}", ValorantVersion ?? "unknown")
                                     .Replace("{CARD}", AppSettings.Current.PlayerCardId)
                                     .Replace("{TITLE}", AppSettings.Current.PlayerTitleId)
                                     .Replace("{LEVEL}", AppSettings.Current.AccountLevel.ToString()))
        );

        var randomStanzaId = Guid.NewGuid();
        var unixTimeMilliseconds = DateTimeOffset.Now.ToUnixTimeMilliseconds();

        var presenceMessage =
            $"<presence from='41c322a1-b328-495b-a004-5ccd3e45eae8@eu1.pvp.net/RC-Deceive' id='b-{randomStanzaId}'>" +
            "<games>" +
            $"<keystone><st>chat</st><s.t>{unixTimeMilliseconds}</s.t><s.p>keystone</s.p><pty/></keystone>" +
            $"<league_of_legends><st>chat</st><s.t>{unixTimeMilliseconds}</s.t><s.p>league_of_legends</s.p><s.c>live</s.c><p>{{&quot;pty&quot;:true}}</p></league_of_legends>" + // No Region s.r keeps it in the main "League" category rather than "Other Servers" in every region with "Group Games & Servers" active
            $"<valorant><st>chat</st><s.t>{unixTimeMilliseconds}</s.t><s.p>valorant</s.p><s.r>PC</s.r><p>{valorantPresence}</p><pty/></valorant>" +
            $"<bacon><st>chat</st><s.t>{unixTimeMilliseconds}</s.t><s.l>bacon_availability_online</s.l><s.p>bacon</s.p></bacon>" +
            "</games>" +
            "<show>chat</show>" +
            "<platform>riot</platform>" +
            "<status/>" +
            "</presence>";

        var bytes = Encoding.UTF8.GetBytes(presenceMessage);
        await WriteIncomingAsync(bytes, 0, bytes.Length);
        Trace.WriteLine("<!--DECEIVE TO RC-->" + presenceMessage);
    }

    public async Task SendMessageFromFakePlayerAsync(string message)
    {
        if (!InsertedFakePlayer || !Connected)
            return;

        var stamp = DateTime.UtcNow.AddSeconds(1).ToString("yyyy-MM-dd HH:mm:ss.fff");

        var chatMessage =
            $"<message from='41c322a1-b328-495b-a004-5ccd3e45eae8@eu1.pvp.net/RC-Deceive' stamp='{stamp}' id='fake-{stamp}' type='chat'><body>{message}</body></message>";

        var bytes = Encoding.UTF8.GetBytes(chatMessage);
        await WriteIncomingAsync(bytes, 0, bytes.Length);
        Trace.WriteLine("<!--DECEIVE TO RC-->" + chatMessage);
    }

    public async Task UpdateStatusAsync(string newStatus)
    {
        if (string.IsNullOrEmpty(LastPresence) || !Connected)
            return;

        await PossiblyRewriteAndResendPresenceAsync(LastPresence, newStatus);
    }

    private async Task WriteOutgoingAsync(byte[] buffer, int offset, int count)
    {
        await _outgoingWriteLock.WaitAsync();
        try { await Outgoing.WriteAsync(buffer, offset, count); }
        finally { _outgoingWriteLock.Release(); }
    }

    private async Task WriteIncomingAsync(byte[] buffer, int offset, int count)
    {
        await _incomingWriteLock.WaitAsync();
        try { await Incoming.WriteAsync(buffer, offset, count); }
        finally { _incomingWriteLock.Release(); }
    }

    /// <summary>Re-sends presence and the fake friend's card after the settings file changed.</summary>
    public async Task ApplySettingsAsync()
    {
        if (!Connected)
            return;
        if (!string.IsNullOrEmpty(LastPresence) && MainController.Enabled)
            await PossiblyRewriteAndResendPresenceAsync(LastPresence, MainController.Status);
        if (InsertedFakePlayer)
            await SendFakePlayerPresenceAsync();
    }

    private void OnConnectionErrored()
    {
        Connected = false;
        ConnectionErrored?.Invoke(this, EventArgs.Empty);
    }
}