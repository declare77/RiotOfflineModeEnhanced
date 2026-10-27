using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Deceive;

/// <summary>
/// A finished match, trimmed to what the app shows (a few KB instead of Riot's full replay data).
/// Saved in %APPDATA%\Deceive\matches so each match is only ever fetched from Riot once.
/// </summary>
internal sealed class MatchSummary
{
    internal sealed class Player
    {
        public string Puuid = "";
        public string Name = "";
        public string Tag = "";
        public string Team = "";
        public string Agent = "";
        public string PartyId = "";
        public int Tier;
        public int Level;
        public int Kills, Deaths, Assists, Score, Rounds;
        public int Head, Body, Leg, Damage;

        public double Acs => Rounds > 0 ? (double)Score / Rounds : 0;
        public double Kd => Deaths > 0 ? (double)Kills / Deaths : Kills;
        public double HeadshotPct => Head + Body + Leg > 0 ? 100.0 * Head / (Head + Body + Leg) : 0;
        public double Adr => Rounds > 0 ? (double)Damage / Rounds : 0;
    }

    public string Id = "";
    public string Map = "";      // map URL, e.g. /Game/Maps/Ascent/Ascent
    public string Queue = "";
    public string SeasonId = "";
    public bool Ranked;
    public DateTime StartUtc;
    public long LengthMs;
    public string Completion = "";
    public List<Player> Players = new();
    public Dictionary<string, (bool won, int rounds)> Teams = new(StringComparer.OrdinalIgnoreCase);

    public Player? Find(string puuid) => Players.FirstOrDefault(p => string.Equals(p.Puuid, puuid, StringComparison.OrdinalIgnoreCase));

    public enum Outcome { Win, Loss, Draw, Unknown }

    public Outcome ResultFor(string puuid)
    {
        var me = Find(puuid);
        if (me is null || !Teams.TryGetValue(me.Team, out var mine)) return Outcome.Unknown;
        if (mine.won) return Outcome.Win;
        // A draw is when nobody won (e.g. a vote draw).
        return Teams.Values.Any(t => t.won) ? Outcome.Loss : Outcome.Draw;
    }

    /// <summary>"13–9" with your team first; deathmatch shows your kills.</summary>
    public string ScoreFor(string puuid)
    {
        var me = Find(puuid);
        if (me is null) return "";
        if (Teams.Count > 2) return $"{me.Kills} kills";
        if (!Teams.TryGetValue(me.Team, out var mine)) return "";
        var other = Teams.Where(t => !string.Equals(t.Key, me.Team, StringComparison.OrdinalIgnoreCase)).Select(t => t.Value.rounds).FirstOrDefault();
        return $"{mine.rounds}–{other}";
    }

    public bool IsDeathmatch => Teams.Count > 2 || Queue.Equals("deathmatch", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ parsing

    internal static MatchSummary? FromDetails(JsonNode? json)
    {
        var info = json?["matchInfo"];
        if (info is null) return null;
        var m = new MatchSummary
        {
            Id = info.Str("matchId"),
            Map = info.Str("mapId"),
            Queue = info.Str("queueID"),
            SeasonId = info.Str("seasonId"),
            Ranked = info.Bool("isRanked"),
            StartUtc = DateTimeOffset.FromUnixTimeMilliseconds(info.Num("gameStartMillis")).UtcDateTime,
            LengthMs = info.Num("gameLengthMillis"),
            Completion = info.Str("completionState")
        };
        if (m.Id.Length == 0) return null;

        foreach (var p in json.Arr("players"))
        {
            if (p.Bool("isObserver")) continue;
            var stats = p?["stats"];
            m.Players.Add(new Player
            {
                Puuid = p.Str("subject"),
                Name = p.Str("gameName"),
                Tag = p.Str("tagLine"),
                Team = p.Str("teamId"),
                Agent = p.Str("characterId"),
                PartyId = p.Str("partyId"),
                Tier = (int)p.Num("competitiveTier"),
                Level = (int)p.Num("accountLevel"),
                Kills = (int)stats.Num("kills"),
                Deaths = (int)stats.Num("deaths"),
                Assists = (int)stats.Num("assists"),
                Score = (int)stats.Num("score"),
                Rounds = (int)stats.Num("roundsPlayed")
            });
        }

        // Shots and damage are only in the round-by-round data.
        var byId = m.Players.ToDictionary(p => p.Puuid, StringComparer.OrdinalIgnoreCase);
        foreach (var round in json.Arr("roundResults"))
        {
            foreach (var ps in round.Arr("playerStats"))
            {
                if (!byId.TryGetValue(ps.Str("subject"), out var player)) continue;
                foreach (var d in ps.Arr("damage"))
                {
                    player.Head += (int)d.Num("headshots");
                    player.Body += (int)d.Num("bodyshots");
                    player.Leg += (int)d.Num("legshots");
                    player.Damage += (int)d.Num("damage");
                }
            }
        }

        foreach (var t in json.Arr("teams"))
            if (t.Str("teamId") is { Length: > 0 } id)
                m.Teams[id] = (t.Bool("won"), (int)t.Num("roundsWon"));

        return m;
    }

    internal JsonObject ToJson() => new()
    {
        ["v"] = 1,
        ["id"] = Id,
        ["map"] = Map,
        ["queue"] = Queue,
        ["season"] = SeasonId,
        ["ranked"] = Ranked,
        ["start"] = new DateTimeOffset(StartUtc).ToUnixTimeMilliseconds(),
        ["length"] = LengthMs,
        ["completion"] = Completion,
        ["teams"] = new JsonArray(Teams.Select(t => (JsonNode)new JsonObject { ["id"] = t.Key, ["won"] = t.Value.won, ["rounds"] = t.Value.rounds }).ToArray()),
        ["players"] = new JsonArray(Players.Select(p => (JsonNode)new JsonObject
        {
            ["id"] = p.Puuid, ["name"] = p.Name, ["tag"] = p.Tag, ["team"] = p.Team, ["agent"] = p.Agent, ["party"] = p.PartyId,
            ["tier"] = p.Tier, ["level"] = p.Level, ["k"] = p.Kills, ["d"] = p.Deaths, ["a"] = p.Assists, ["score"] = p.Score,
            ["rounds"] = p.Rounds, ["hs"] = p.Head, ["bs"] = p.Body, ["ls"] = p.Leg, ["dmg"] = p.Damage
        }).ToArray())
    };

    internal static MatchSummary? FromJson(JsonNode? j)
    {
        if (j is null || j.Num("v") != 1 || j.Str("id").Length == 0) return null;
        var m = new MatchSummary
        {
            Id = j.Str("id"),
            Map = j.Str("map"),
            Queue = j.Str("queue"),
            SeasonId = j.Str("season"),
            Ranked = j.Bool("ranked"),
            StartUtc = DateTimeOffset.FromUnixTimeMilliseconds(j.Num("start")).UtcDateTime,
            LengthMs = j.Num("length"),
            Completion = j.Str("completion")
        };
        foreach (var t in j.Arr("teams"))
            m.Teams[t.Str("id")] = (t.Bool("won"), (int)t.Num("rounds"));
        foreach (var p in j.Arr("players"))
            m.Players.Add(new Player
            {
                Puuid = p.Str("id"), Name = p.Str("name"), Tag = p.Str("tag"), Team = p.Str("team"), Agent = p.Str("agent"),
                PartyId = p.Str("party"), Tier = (int)p.Num("tier"), Level = (int)p.Num("level"), Kills = (int)p.Num("k"),
                Deaths = (int)p.Num("d"), Assists = (int)p.Num("a"), Score = (int)p.Num("score"), Rounds = (int)p.Num("rounds"),
                Head = (int)p.Num("hs"), Body = (int)p.Num("bs"), Leg = (int)p.Num("ls"), Damage = (int)p.Num("dmg")
            });
        return m;
    }
}

/// <summary>Your match history, rank changes and the on-disk match cache.</summary>
internal static class MatchData
{
    private static readonly string Dir = Path.Combine(Persistence.DataDir, "matches");
    private static readonly object Lock = new();
    private static Dictionary<string, MatchSummary>? _loaded;

    internal sealed class HistoryEntry
    {
        public string MatchId = "";
        public DateTime StartUtc;
        public string Queue = "";
    }

    internal sealed class RankChange
    {
        public string MatchId = "";
        public DateTime StartUtc;
        public string SeasonId = "";
        public int TierBefore, TierAfter, RrBefore, RrAfter, RrEarned, Afk;
    }

    // ------------------------------------------------------------------ cache

    private static Dictionary<string, MatchSummary> Cache()
    {
        lock (Lock)
        {
            if (_loaded is not null) return _loaded;
            _loaded = new Dictionary<string, MatchSummary>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (Directory.Exists(Dir))
                {
                    foreach (var file in Directory.GetFiles(Dir, "*.json"))
                    {
                        try
                        {
                            if (MatchSummary.FromJson(Json.Parse(File.ReadAllText(file))) is { } m)
                                _loaded[m.Id] = m;
                        }
                        catch { /* skip a bad file */ }
                    }
                }
            }
            catch (Exception e)
            {
                Trace.WriteLine("Match cache unreadable: " + e.Message);
            }

            return _loaded;
        }
    }

    /// <summary>Reads the cache from disk ahead of time, off the UI thread.</summary>
    internal static void Warm() => System.Threading.Tasks.Task.Run(() => { lock (Lock) Cache(); });

    internal static MatchSummary? Cached(string matchId)
    {
        lock (Lock)
            return Cache().TryGetValue(matchId, out var m) ? m : null;
    }

    internal static void Save(MatchSummary m)
    {
        lock (Lock)
        {
            Cache()[m.Id] = m;
            try
            {
                Directory.CreateDirectory(Dir);
                var safe = new string(m.Id.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
                File.WriteAllText(Path.Combine(Dir, safe + ".json"), m.ToJson().ToJsonString());
            }
            catch (Exception e)
            {
                Trace.WriteLine("Couldn't cache match: " + e.Message);
            }
        }
    }

    /// <summary>Every cached match that includes this player, newest first.</summary>
    internal static List<MatchSummary> MatchesWith(string puuid)
    {
        lock (Lock)
            return Cache().Values.Where(m => m.Find(puuid) is not null).OrderByDescending(m => m.StartUtc).ToList();
    }

    /// <summary>
    /// How often you've played with or against someone, from the matches the app has seen.
    /// Returns (together, togetherWins, against, againstWins).
    /// </summary>
    internal static (int with, int withWins, int against, int againstWins) History(string you, string them)
    {
        var with = 0; var withWins = 0; var against = 0; var againstWins = 0;
        lock (Lock)
        {
            foreach (var m in Cache().Values)
            {
                var a = m.Find(you);
                var b = m.Find(them);
                if (a is null || b is null || m.IsDeathmatch) continue;
                var won = m.ResultFor(you) == MatchSummary.Outcome.Win;
                if (string.Equals(a.Team, b.Team, StringComparison.OrdinalIgnoreCase))
                {
                    with++;
                    if (won) withWins++;
                }
                else
                {
                    against++;
                    if (won) againstWins++;
                }
            }
        }

        return (with, withWins, against, againstWins);
    }

    // ------------------------------------------------------------------ requests

    internal static async Task<(List<HistoryEntry> entries, int total)> GetHistoryAsync(string puuid, int start, int end)
    {
        var json = await ValorantApi.GetPdAsync($"match-history/v1/history/{Uri.EscapeDataString(puuid)}?startIndex={start}&endIndex={end}").ConfigureAwait(false);
        var list = json.Arr("History").Select(h => new HistoryEntry
        {
            MatchId = h.Str("MatchID"),
            StartUtc = DateTimeOffset.FromUnixTimeMilliseconds(h.Num("GameStartTime")).UtcDateTime,
            Queue = h.Str("QueueID")
        }).Where(h => h.MatchId.Length > 0).ToList();
        return (list, (int)json.Num("Total", list.Count));
    }

    internal static async Task<List<RankChange>> GetRankChangesAsync(string puuid, int start, int end)
    {
        var json = await ValorantApi.GetPdAsync($"mmr/v1/players/{Uri.EscapeDataString(puuid)}/competitiveupdates?startIndex={start}&endIndex={end}&queue=competitive").ConfigureAwait(false);
        return ParseRankChanges(json);
    }

    internal static List<RankChange> ParseRankChanges(JsonNode? json) =>
        json.Arr("Matches").Select(m => new RankChange
        {
            MatchId = m.Str("MatchID"),
            StartUtc = DateTimeOffset.FromUnixTimeMilliseconds(m.Num("MatchStartTime")).UtcDateTime,
            SeasonId = m.Str("SeasonID"),
            TierBefore = (int)m.Num("TierBeforeUpdate"),
            TierAfter = (int)m.Num("TierAfterUpdate"),
            RrBefore = (int)m.Num("RankedRatingBeforeUpdate"),
            RrAfter = (int)m.Num("RankedRatingAfterUpdate"),
            RrEarned = (int)m.Num("RankedRatingEarned"),
            Afk = (int)m.Num("AFKPenalty")
        }).Where(m => m.MatchId.Length > 0).ToList();

    /// <summary>A match's summary, from the cache or (once) from Riot.</summary>
    internal static async Task<MatchSummary?> GetMatchAsync(string matchId)
    {
        if (Cached(matchId) is { } cached) return cached;
        var json = await ValorantApi.GetPdAsync($"match-details/v1/matches/{Uri.EscapeDataString(matchId)}").ConfigureAwait(false);
        var summary = MatchSummary.FromDetails(json);
        if (summary is null) return null;
        // Only keep finished matches; a match in progress has no final score yet.
        if (json?["matchInfo"].Bool("isCompleted") == true)
            Save(summary);
        return summary;
    }
}
