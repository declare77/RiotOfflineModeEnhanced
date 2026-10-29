using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace Deceive;

/// <summary>
/// Learns your friends list (Riot ID -> chat JID) and their online state from what the chat
/// server sends, so the "visibleTo" setting and the app window can work with Riot IDs.
/// </summary>
internal static class FriendDirectory
{
    private static readonly ConcurrentDictionary<string, Friend> ByJid = new(StringComparer.OrdinalIgnoreCase);

    // jid -> (resource -> show value)
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> Presence = new(StringComparer.OrdinalIgnoreCase);

    // jid -> what they're doing in VALORANT (from the presence's hidden details), and since when.
    // stateSince is null until we've actually seen the state change (we don't know how long it was going before).
    private static readonly ConcurrentDictionary<string, (ValorantPresence presence, DateTime? stateSince)> Valorant = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A friend's VALORANT details (map, mode, score, party) if they're playing it.</summary>
    internal static ValorantPresence? GetValorant(string jid) =>
        Valorant.TryGetValue(jid, out var v) && GetState(jid) != OnlineState.Offline ? v.presence : null;

    /// <summary>When the friend's VALORANT state (menus, agent select, match) last changed, as far as we saw.</summary>
    internal static DateTime? ValorantSince(string jid) =>
        Valorant.TryGetValue(jid, out var v) ? v.stateSince : null;

    /// <summary>Raised (on a background thread) whenever the friends list or someone's online state changes.</summary>
    internal static event EventHandler? Changed;

    private static readonly Regex ItemRegex = new(
        @"<item\s[^>]*?\bjid=(['""])(?<jid>[^'""]+)\1[^>]*?(?:/>|>(?<body>.*?)</item>)",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex IdRegex = new(
        @"<id\s[^>]*?\bname=(['""])(?<name>.*?)\1[^>]*?\btagline=(['""])(?<tag>.*?)\2",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex SubscriptionRemoveRegex = new(
        @"\bsubscription=(['""])remove\1", RegexOptions.Compiled);

    private static readonly Regex PresenceRegex = new(
        @"<presence\b(?<attrs>[^>]*?)(?:/>|>(?<body>.*?)</presence>)",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex FromRegex = new(@"\bfrom=(['""])(?<v>[^'""]+)\1", RegexOptions.Compiled);
    private static readonly Regex TypeRegex = new(@"\btype=(['""])(?<v>[^'""]+)\1", RegexOptions.Compiled);
    private static readonly Regex ShowRegex = new(@"<show>(?<v>[^<]*)</show>", RegexOptions.Compiled);

    internal enum OnlineState
    {
        Offline = 0,
        Mobile = 1,
        Away = 2,
        InGame = 3,
        Online = 4
    }

    internal sealed class Friend
    {
        public Friend(string jid, string name, string tag) { Jid = jid; Name = name; Tag = tag; }
        public string Jid { get; }
        public string Name { get; }
        public string Tag { get; }
        public string RiotId => Name + "#" + Tag;
        public bool SameAs(Friend? other) => other is not null && other.Jid == Jid && other.Name == Name && other.Tag == Tag;
    }

    /// <summary>
    /// Scans one connection's server->client stream. Keeps the unparsed tail of each read, since
    /// roster items and presences can be split across reads.
    /// </summary>
    internal class Parser
    {
        private readonly TagScanner _items = new("<item", ItemRegex);
        private readonly TagScanner _presences = new("<presence", PresenceRegex);

        /// <summary>Feeds server->client data. Returns the JIDs of friends that were newly learned.</summary>
        internal List<string> Feed(string content)
        {
            var added = new List<string>();
            var changed = false;

            foreach (var m in _items.Feed(content))
            {
                var jid = Bare(m.Groups["jid"].Value);

                if (SubscriptionRemoveRegex.IsMatch(m.Value))
                {
                    changed |= ByJid.TryRemove(jid, out _);
                    continue;
                }

                var idMatch = IdRegex.Match(m.Value);
                if (!idMatch.Success)
                    continue;

                var friend = new Friend(jid, Decode(idMatch.Groups["name"].Value), Decode(idMatch.Groups["tag"].Value));
                if (string.IsNullOrWhiteSpace(friend.Name))
                    continue;

                var isNew = !ByJid.TryGetValue(jid, out var existing) || !friend.SameAs(existing);
                ByJid[jid] = friend;
                if (isNew)
                {
                    added.Add(jid);
                    changed = true;
                }
            }

            foreach (var m in _presences.Feed(content))
                changed |= RecordPresence(m.Groups["attrs"].Value, m.Groups["body"].Value);

            if (changed)
                Changed?.Invoke(null, EventArgs.Empty);

            return added;
        }
    }

    private sealed class TagScanner
    {
        private readonly string _open;
        private readonly Regex _regex;
        private string _tail = string.Empty;

        public TagScanner(string open, Regex regex)
        {
            _open = open;
            _regex = regex;
        }

        public List<Match> Feed(string content)
        {
            var result = new List<Match>();
            var buffer = _tail + content;

            if (buffer.IndexOf(_open, StringComparison.Ordinal) < 0)
            {
                _tail = buffer.Length > 16 ? buffer.Substring(buffer.Length - 16) : buffer;
                return result;
            }

            var lastEnd = 0;
            foreach (Match m in _regex.Matches(buffer))
            {
                result.Add(m);
                lastEnd = m.Index + m.Length;
            }

            var rest = buffer.Substring(lastEnd);
            var open = rest.LastIndexOf(_open, StringComparison.Ordinal);
            if (open >= 0)
                rest = rest.Substring(open);
            else if (rest.Length > 16)
                rest = rest.Substring(rest.Length - 16);
            // Never let a broken stream grow this forever.
            _tail = rest.Length > 512 * 1024 ? string.Empty : rest;
            return result;
        }
    }

    private static bool RecordPresence(string attrs, string body)
    {
        var from = FromRegex.Match(attrs);
        if (!from.Success)
            return false;

        var full = Decode(from.Groups["v"].Value);
        var jid = Bare(full);
        var slash = full.IndexOf('/');
        var resource = slash < 0 ? string.Empty : full.Substring(slash + 1);

        // Ignore group chat rooms and anything that isn't a player.
        if (jid.IndexOf("@", StringComparison.Ordinal) < 0 || jid.Contains("@conference.") || jid.Contains("@champ-select.") ||
            jid.Contains("@lobby.") || jid.Contains("@ares-"))
            return false;

        var type = TypeRegex.Match(attrs);
        var resources = Presence.GetOrAdd(jid, _ => new ConcurrentDictionary<string, string>());

        if (type.Success)
        {
            if (type.Groups["v"].Value != "unavailable")
                return false;
            var removed = resources.TryRemove(resource, out _);
            if (resources.IsEmpty) Valorant.TryRemove(jid, out _);
            return removed;
        }

        // VALORANT details ride along in <games><valorant><p>base64</p>.
        var valorantChanged = false;
        if (body.IndexOf("<valorant>", StringComparison.Ordinal) >= 0 && ValorantPresence.FromStanza(body) is { } vp)
        {
            DateTime? since = Valorant.TryGetValue(jid, out var old0)
                ? old0.presence.SessionLoopState == vp.SessionLoopState ? old0.stateSince : DateTime.UtcNow
                : null;
            valorantChanged = !Valorant.TryGetValue(jid, out var prev) || !SameValorant(prev.presence, vp);
            Valorant[jid] = (vp, since);
        }

        var show = ShowRegex.Match(body);
        var value = show.Success ? show.Groups["v"].Value.Trim().ToLowerInvariant() : "chat";
        if (resources.TryGetValue(resource, out var old) && old == value)
            return valorantChanged;
        resources[resource] = value;
        return true;
    }

    private static bool SameValorant(ValorantPresence a, ValorantPresence b) =>
        a.SessionLoopState == b.SessionLoopState && a.MatchMap == b.MatchMap && a.QueueId == b.QueueId &&
        a.ScoreAlly == b.ScoreAlly && a.ScoreEnemy == b.ScoreEnemy && a.PartyId == b.PartyId && a.PartySize == b.PartySize &&
        a.CompetitiveTier == b.CompetitiveTier && a.IsIdle == b.IsIdle;

    internal static OnlineState GetState(string jid)
    {
        if (!Presence.TryGetValue(jid, out var resources))
            return OnlineState.Offline;

        var best = OnlineState.Offline;
        foreach (var show in resources.Values)
        {
            var state = show switch
            {
                "chat" => OnlineState.Online,
                "dnd" => OnlineState.InGame,
                "away" or "xa" => OnlineState.Away,
                "mobile" => OnlineState.Mobile,
                _ => OnlineState.Offline
            };
            if (state > best) best = state;
        }

        return best;
    }

    internal static int Count => ByJid.Count;

    internal static List<Friend> All() => ByJid.Values.ToList();

    /// <summary>Resolves the "visibleTo" entries into chat JIDs.</summary>
    internal static List<string> Resolve(IEnumerable<string> entries, out List<string> matchedNames, out List<string> notFound)
    {
        var jids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        matchedNames = new List<string>();
        notFound = new List<string>();
        var friends = ByJid.Values.ToList();

        foreach (var raw in entries)
        {
            var hits = Match(raw, friends, out var rawJid);
            if (rawJid is not null)
            {
                if (jids.Add(rawJid)) matchedNames.Add(raw.Trim());
                continue;
            }

            if (hits.Count == 0)
            {
                notFound.Add(raw.Trim());
                continue;
            }

            foreach (var f in hits)
            {
                if (jids.Add(f.Jid))
                    matchedNames.Add(f.RiotId);
            }
        }

        return jids.ToList();
    }

    /// <summary>The friends a single "visibleTo" entry refers to.</summary>
    internal static List<Friend> Match(string raw, List<Friend> friends, out string? unknownJid)
    {
        unknownJid = null;
        var entry = Normalize(raw);

        if (entry.Contains("@")) // already a JID
        {
            var byJid = friends.Where(f => f.Jid.Equals(entry, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byJid.Count == 0) unknownJid = entry;
            return byJid;
        }

        if (entry.Contains("#"))
            return friends.Where(f => Normalize(f.RiotId) == entry).ToList();

        return friends.Where(f => Normalize(f.Name) == entry).ToList();
    }

    private static string Normalize(string s)
    {
        s = s.Trim().ToLowerInvariant();
        var hash = s.IndexOf('#');
        return hash < 0 ? s : s.Substring(0, hash).Trim() + "#" + s.Substring(hash + 1).Trim();
    }

    private static string Bare(string jid)
    {
        var slash = jid.IndexOf('/');
        return slash < 0 ? jid : jid.Substring(0, slash);
    }

    private static string Decode(string s) => WebUtility.HtmlDecode(s.Replace("&apos;", "'"));
}
