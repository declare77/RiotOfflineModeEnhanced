using System;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Deceive;

/// <summary>
/// The VALORANT details hidden inside a Riot chat presence (the base64 &lt;p&gt; under &lt;valorant&gt;).
/// Riot has changed the layout over time (flat fields before 2024, nested objects since), so
/// fields are looked up by name anywhere in the object.
/// </summary>
internal sealed class ValorantPresence
{
    public string SessionLoopState = ""; // MENUS, PREGAME, INGAME
    public string MatchMap = "";
    public string QueueId = "";
    public string ProvisioningFlow = "";
    public string CustomGameName = "";
    public int? ScoreAlly;
    public int? ScoreEnemy;
    public string PartyId = "";
    public int PartySize;
    public int MaxPartySize;
    public bool IsPartyOwner;
    public bool PartyOpen;
    public int CompetitiveTier;
    public int AccountLevel;
    public bool IsIdle;

    private static readonly Regex ValorantP = new(@"<valorant>.*?<p>(?<p>[^<]+)</p>", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Reads the VALORANT details out of a presence stanza, or null if it has none.</summary>
    public static ValorantPresence? FromStanza(string stanza)
    {
        var match = ValorantP.Match(stanza);
        return match.Success ? FromBase64(match.Groups["p"].Value) : null;
    }

    public static ValorantPresence? FromBase64(string base64)
    {
        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(base64.Trim()));
            return FromJson(Json.Parse(text));
        }
        catch (Exception e)
        {
            Trace.WriteLine("Couldn't read VALORANT presence: " + e.Message);
            return null;
        }
    }

    public static ValorantPresence? FromJson(JsonNode? json)
    {
        if (json is not JsonObject) return null;
        var p = new ValorantPresence
        {
            SessionLoopState = Find(json, "sessionLoopState").AsStr(),
            MatchMap = Find(json, "matchMap").AsStr(),
            QueueId = Find(json, "queueId").AsStr(),
            ProvisioningFlow = Find(json, "provisioningFlow").AsStr() is { Length: > 0 } flow ? flow : Find(json, "partyOwnerProvisioningFlow").AsStr(),
            CustomGameName = Find(json, "customGameName").AsStr(),
            ScoreAlly = Find(json, "partyOwnerMatchScoreAllyTeam").AsInt(),
            ScoreEnemy = Find(json, "partyOwnerMatchScoreEnemyTeam").AsInt(),
            PartyId = Find(json, "partyId").AsStr(),
            PartySize = Find(json, "partySize").AsInt() ?? 0,
            MaxPartySize = Find(json, "maxPartySize").AsInt() ?? 0,
            IsPartyOwner = Find(json, "isPartyOwner").AsBool(),
            PartyOpen = string.Equals(Find(json, "partyAccessibility").AsStr(), "OPEN", StringComparison.OrdinalIgnoreCase),
            CompetitiveTier = Find(json, "competitiveTier").AsInt() ?? 0,
            AccountLevel = Find(json, "accountLevel").AsInt() ?? 0,
            IsIdle = Find(json, "isIdle").AsBool()
        };

        // Older presences only had the party owner's map.
        if (p.MatchMap.Length == 0)
            p.MatchMap = Find(json, "partyOwnerMatchMap").AsStr();
        if (p.SessionLoopState.Length == 0)
            p.SessionLoopState = Find(json, "partyOwnerSessionLoopState").AsStr();
        return p;
    }

    /// <summary>The first property with exactly this name, searching nested objects too.</summary>
    private static JsonNode? Find(JsonNode? node, string key, int depth = 0)
    {
        if (node is not JsonObject obj || depth > 4) return null;
        foreach (var pair in obj)
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase) && pair.Value is not null)
                return pair.Value;
        foreach (var pair in obj)
            if (pair.Value is JsonObject && Find(pair.Value, key, depth + 1) is { } found)
                return found;
        return null;
    }

    public bool InMenus => SessionLoopState.Equals("MENUS", StringComparison.OrdinalIgnoreCase);
    public bool InAgentSelect => SessionLoopState.Equals("PREGAME", StringComparison.OrdinalIgnoreCase);
    public bool InMatch => SessionLoopState.Equals("INGAME", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ your own presence

    private static string _ownState = "";

    /// <summary>Your own VALORANT state, from the presence the Riot Client sends (no requests needed).</summary>
    internal static ValorantPresence? Own { get; private set; }

    /// <summary>Raised (on a background thread) when your session state changes, e.g. MENUS -> PREGAME.</summary>
    internal static event EventHandler<ValorantPresence>? OwnStateChanged;

    /// <summary>Called by the chat relay with every presence the Riot Client sends.</summary>
    internal static void ObserveOwn(string stanza)
    {
        if (stanza.IndexOf("<valorant>", StringComparison.Ordinal) < 0) return;
        var p = FromStanza(stanza);
        if (p is null) return;
        Own = p;
        var state = p.SessionLoopState.ToUpperInvariant();
        if (state == _ownState) return;
        _ownState = state;
        try { OwnStateChanged?.Invoke(null, p); }
        catch (Exception e) { Trace.WriteLine("Presence handler failed: " + e); }
    }
}

internal static class JsonNodeValues
{
    public static string AsStr(this JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    public static int? AsInt(this JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return (int)l;
        if (v.TryGetValue<double>(out var d)) return (int)Math.Round(d);
        if (v.TryGetValue<string>(out var s) && int.TryParse(s, out var p)) return p;
        return null;
    }

    public static bool AsBool(this JsonNode? node)
    {
        if (node is not JsonValue v) return false;
        if (v.TryGetValue<bool>(out var b)) return b;
        return v.TryGetValue<string>(out var s) && bool.TryParse(s, out var p) && p;
    }
}
