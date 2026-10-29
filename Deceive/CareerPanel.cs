using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Deceive;

/// <summary>
/// Career tab: your rank and progress, this session, trends over recent matches, match history
/// with scoreboards, and how you do on each map and agent.
/// </summary>
internal sealed class CareerPanel : PagePanel
{
    private static readonly string[] Views = { "Overview", "Matches", "Maps & agents" };
    private const int OverviewView = 0, MatchesView = 1, BreakdownView = 2;
    private int _view;
    private bool _competitiveOnly; // the "Competitive" chip on Overview and Maps & agents

    private bool _active;
    private bool _loading;
    private bool _loadingMatches;
    private string _error = "";
    private DateTime _loadedAt = DateTime.MinValue;

    private string _you = "";
    private ValorantContent.Catalog? _catalog;
    private ValorantApi.RankInfo? _rank;
    private readonly List<MatchData.HistoryEntry> _entries = new();
    private int _total;
    private readonly Dictionary<string, MatchData.RankChange> _rankChanges = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);

    private const int PageSize = 10;

    public CareerPanel(float scale) : base(scale)
    {
        ImageCache.Loaded += OnImageLoaded;
        // A match just ended: refresh next time you look.
        ValorantPresence.OwnStateChanged += (_, p) => { if (p.InMenus) _loadedAt = DateTime.MinValue; };
    }

    public void SetActive(bool active)
    {
        _active = active;
        if (active && !_loading && DateTime.UtcNow - _loadedAt > TimeSpan.FromMinutes(5))
            _ = LoadAsync();
    }

    private void OnImageLoaded(object? sender, EventArgs e)
    {
        if (IsDisposed || !IsHandleCreated || !_active) return;
        try { BeginInvoke(new Action(Invalidate)); }
        catch (InvalidOperationException) { /* closing */ }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ImageCache.Loaded -= OnImageLoaded;
        base.Dispose(disposing);
    }

    // ------------------------------------------------------------------ loading

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        _error = "";
        Invalidate();
        try
        {
            _you = await ValorantApi.GetPuuidAsync();
            _catalog ??= await ValorantContent.GetAsync();

            var mmr = await ValorantApi.GetPdAsync($"mmr/v1/players/{Uri.EscapeDataString(_you)}");
            _rank = ValorantApi.ParseRank(mmr, _catalog);

            var (entries, total) = await MatchData.GetHistoryAsync(_you, 0, PageSize * 2);
            _entries.Clear();
            _entries.AddRange(entries);
            _total = total;

            foreach (var change in await MatchData.GetRankChangesAsync(_you, 0, 20))
                _rankChanges[change.MatchId] = change;

            _loadedAt = DateTime.UtcNow;
            Invalidate();
            await LoadSummariesAsync();
        }
        catch (ValorantApiException e)
        {
            _error = e.Message;
        }
        catch (Exception e)
        {
            Trace.WriteLine("Career failed: " + e);
            _error = "Something went wrong loading your matches. Try again in a moment.";
        }
        finally
        {
            _loading = false;
            if (!IsDisposed) Invalidate();
        }
    }

    /// <summary>Fetches the matches not cached yet, one at a time, so Riot isn't flooded.</summary>
    private async Task LoadSummariesAsync()
    {
        if (_loadingMatches) return;
        _loadingMatches = true;
        try
        {
            foreach (var entry in _entries.ToList())
            {
                if (MatchData.Cached(entry.MatchId) is not null || _failed.Contains(entry.MatchId)) continue;
                try
                {
                    if (await MatchData.GetMatchAsync(entry.MatchId) is null)
                        _failed.Add(entry.MatchId);
                }
                catch (ValorantApiException e) when (e.Status == 429)
                {
                    _error = "Riot is limiting requests; the rest of your matches will load in a minute.";
                    return;
                }
                catch (Exception e)
                {
                    Trace.WriteLine("Match failed: " + e.Message);
                    _failed.Add(entry.MatchId);
                }

                if (!IsDisposed) Invalidate();
                await Task.Delay(250);
            }

            // Matches with an agent the cached data doesn't know (a new agent): get fresh data.
            var agents = Summaries().SelectMany(m => m.Players.Select(p => p.Agent)).Distinct().ToList();
            if (_catalog is not null && !agents.All(_catalog.Agents.ContainsKey))
                _catalog = await ValorantContent.GetAsync(mustHaveAgents: agents);
        }
        finally
        {
            _loadingMatches = false;
            if (!IsDisposed) Invalidate();
        }
    }

    private async Task LoadMoreAsync()
    {
        if (_loading || _loadingMatches || _you.Length == 0) return;
        _loading = true;
        Invalidate();
        try
        {
            var (entries, total) = await MatchData.GetHistoryAsync(_you, _entries.Count, _entries.Count + PageSize);
            _total = total;
            foreach (var e in entries)
                if (_entries.All(x => x.MatchId != e.MatchId))
                    _entries.Add(e);
            _loading = false;
            await LoadSummariesAsync();
        }
        catch (ValorantApiException e)
        {
            _error = e.Message;
        }
        finally
        {
            _loading = false;
            if (!IsDisposed) Invalidate();
        }
    }

    // ------------------------------------------------------------------ input

    protected override void OnHitClicked(string key)
    {
        if (key.StartsWith("sub:", StringComparison.Ordinal) && int.TryParse(key.Substring(4), out var view))
        {
            if (view == _view) return;
            _view = view;
            ScrollToTop();
            Invalidate();
            return;
        }

        switch (key)
        {
            case "action":
            case "refresh":
                _loadedAt = DateTime.MinValue;
                _ = LoadAsync();
                return;
            case "more":
                _ = LoadMoreAsync();
                return;
            case "filter:all":
                _competitiveOnly = false;
                Invalidate();
                return;
            case "filter:comp":
                _competitiveOnly = true;
                Invalidate();
                return;
        }

        if (key.StartsWith("m:", StringComparison.Ordinal))
        {
            var id = key.Substring(2);
            if (!_expanded.Remove(id)) _expanded.Add(id);
            Invalidate();
        }
    }

    protected override int HeaderHeight => D(46);

    protected override void PaintHeader(Graphics g, int width)
    {
        PaintSubTabs(g, 0, Views, _view);
        if (_entries.Count == 0) return;
        var text = _loading || _loadingMatches ? "Loading…" : "Refresh";
        var rw = Draw.TextWidth("Refreshing…", Theme.Small) + D(28);
        var refresh = new Rectangle(width - rw - D(2), 0, rw, D(28));
        var enabled = !_loading && !_loadingMatches;
        Draw.Button(g, refresh, text, HoverKey == "refresh", enabled, S);
        if (enabled) Hit(refresh, "refresh");
    }

    // ------------------------------------------------------------------ data helpers

    /// <summary>Loaded summaries in history order (newest first).</summary>
    private List<MatchSummary> Summaries(bool competitiveOnly = false) =>
        _entries.Select(e => MatchData.Cached(e.MatchId))
            .Where(m => m is not null && m.Find(_you) is not null)
            .Select(m => m!)
            .Where(m => !competitiveOnly || m.Queue.Equals("competitive", StringComparison.OrdinalIgnoreCase))
            .ToList();

    private string QueueName(string queue) => queue.ToLowerInvariant() switch
    {
        "competitive" => "Competitive",
        "unrated" => "Unrated",
        "swiftplay" => "Swiftplay",
        "spikerush" => "Spike Rush",
        "premier" => "Premier",
        "deathmatch" => "Deathmatch",
        "ggteam" => "Escalation",
        "hurm" => "Team Deathmatch",
        "newmap" => "New Map",
        "onefa" => "Replication",
        "snowball" => "Snowball Fight",
        "" => "Custom",
        var other => char.ToUpperInvariant(other[0]) + other.Substring(1)
    };

    private static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span.TotalMinutes < 60) return $"{Math.Max(1, (int)span.TotalMinutes)}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays}d ago";
        return utc.ToLocalTime().ToString("MMM d");
    }

    private static Color ResultColor(MatchSummary.Outcome o) => o switch
    {
        MatchSummary.Outcome.Win => Theme.Online,
        MatchSummary.Outcome.Loss => Theme.Accent,
        _ => Theme.Faint
    };

    /// <summary>
    /// Names in scoreboards: Riot's match data doesn't say who hid their name, so only you, your
    /// party, your friends and players whose names were visible in a live agent select are named.
    /// Everyone else shows as their agent.
    /// </summary>
    private string DisplayName(MatchSummary m, MatchSummary.Player p, out bool named)
    {
        named = true;
        var me = m.Find(_you);
        if (string.Equals(p.Puuid, _you, StringComparison.OrdinalIgnoreCase) ||
            (me is not null && me.PartyId.Length > 0 && string.Equals(p.PartyId, me.PartyId, StringComparison.OrdinalIgnoreCase)))
            return p.Name.Length > 0 ? p.Name + (p.Tag.Length > 0 ? " #" + p.Tag : "") : "Player";
        var prefix = p.Puuid + "@";
        var friend = FriendDirectory.All().FirstOrDefault(f => f.Jid.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (friend is not null)
            return friend.Name + " #" + friend.Tag;
        if (ValorantApi.KnownName(p.Puuid) is { } known)
            return known.name + " #" + known.tag;
        named = false;
        return _catalog?.Agents.TryGetValue(p.Agent, out var agent) == true ? agent!.Name : "New agent";
    }

    // ------------------------------------------------------------------ painting

    protected override int PaintContent(Graphics g, int width)
    {
        if (_entries.Count == 0)
        {
            if (_loading)
                return PaintMessage(g, width, "Loading your matches…", "Your recent games are fetched once, then kept on your PC.", null);
            if (_error.Length > 0)
                return PaintMessage(g, width, "Couldn't load your matches", _error, "Try again");
            return PaintMessage(g, width, "No matches yet", "Play a game and it shows up here.", "Refresh");
        }

        var y = 0;
        if (_error.Length > 0)
        {
            TextRenderer.DrawText(g, _error, Theme.Small, new Rectangle(0, y, width, D(18)), Theme.Warn, Theme.LeftMiddle);
            y += D(24);
        }

        return _view switch
        {
            MatchesView => PaintMatches(g, y, width),
            BreakdownView => PaintBreakdown(g, y, width),
            _ => PaintOverview(g, y, width)
        };
    }

    private int PaintFilter(Graphics g, int y, int width)
    {
        var x = 0;
        foreach (var (key, label, on) in new[] { ("filter:all", "All modes", !_competitiveOnly), ("filter:comp", "Competitive", _competitiveOnly) })
        {
            var w = Draw.TextWidth(label, Theme.Small) + D(22);
            var rect = new Rectangle(x, y, w, D(24));
            var rf = new RectangleF(rect.X + 0.5f, rect.Y + 0.5f, rect.Width - 1, rect.Height - 1);
            if (on) Theme.FillRound(g, Theme.Raised, rf, rf.Height / 2);
            else if (HoverKey == key) Theme.FillRound(g, Theme.Hover, rf, rf.Height / 2);
            Theme.StrokeRound(g, on ? ColorTranslator.FromHtml("#3A4250") : Theme.Border, rf, rf.Height / 2);
            TextRenderer.DrawText(g, label, Theme.Small, rect, on ? Theme.Text : Theme.Muted, Theme.CenterMiddle);
            Hit(rect, key);
            x = rect.Right + D(6);
        }

        return y + D(24);
    }

    private static void Card(Graphics g, Rectangle rect, float radius)
    {
        var rf = new RectangleF(rect.X + 0.5f, rect.Y + 0.5f, rect.Width - 1, rect.Height - 1);
        Theme.FillRound(g, Theme.Surface, rf, radius);
        Theme.StrokeRound(g, Theme.Border, rf, radius);
    }

    private int Section(Graphics g, int y, int width, string title, string right = "")
    {
        var rect = new Rectangle(0, y, width, D(16));
        TextRenderer.DrawText(g, title, Theme.Section, rect, Theme.Faint, Theme.LeftMiddle);
        if (right.Length > 0) TextRenderer.DrawText(g, right, Theme.Small, rect, Theme.Muted, Theme.RightMiddle);
        return y + D(24);
    }

    // ---- Overview

    private int PaintOverview(Graphics g, int y, int width)
    {
        y = PaintRankCard(g, y, width) + D(12);
        y = PaintSession(g, y, width) + D(22);

        y = Section(g, y, width, "TRENDS", "");
        y = PaintFilter(g, y, width) + D(10);
        var recent = Summaries(_competitiveOnly).Where(m => !m.IsDeathmatch).Take(20).Reverse().ToList();
        if (recent.Count < 2)
            return PaintNoteBox(g, y, width, _loadingMatches ? "Loading matches…" : "Play a few more matches to see trends.") + D(16);

        var me = recent.Select(m => (m, p: m.Find(_you)!)).ToList();
        var chartW = (width - D(10)) / 2;
        var chartH = D(118);
        PaintChart(g, new Rectangle(0, y, chartW, chartH), "ACS", me.Select(x => x.p.Acs).ToList(), me.Select(x => x.m.ResultFor(_you)).ToList(), "0");
        PaintChart(g, new Rectangle(chartW + D(10), y, chartW, chartH), "K/D", me.Select(x => x.p.Kd).ToList(), me.Select(x => x.m.ResultFor(_you)).ToList(), "0.00");
        y += chartH + D(10);
        PaintChart(g, new Rectangle(0, y, chartW, chartH), "Headshot %", me.Select(x => x.p.HeadshotPct).ToList(), me.Select(x => x.m.ResultFor(_you)).ToList(), "0");
        PaintChart(g, new Rectangle(chartW + D(10), y, chartW, chartH), "Damage per round", me.Select(x => x.p.Adr).ToList(), me.Select(x => x.m.ResultFor(_you)).ToList(), "0");
        y += chartH + D(8);
        TextRenderer.DrawText(g, $"Last {recent.Count} matches, oldest to newest. Dots are wins (green) and losses (red).", Theme.Small,
            new Rectangle(0, y, width, D(18)), Theme.Faint, Theme.LeftMiddle);
        return y + D(30);
    }

    private int PaintNoteBox(Graphics g, int y, int width, string text)
    {
        var box = new Rectangle(0, y, width, D(60));
        Card(g, box, D(10));
        TextRenderer.DrawText(g, text, Theme.Body, box, Theme.Muted, Theme.CenterMiddle);
        return box.Bottom;
    }

    private int PaintRankCard(Graphics g, int y, int width)
    {
        var rect = new Rectangle(0, y, width, D(116));
        Card(g, rect, D(12));
        var catalog = _catalog;
        var rank = _rank;
        var tier = rank is null ? null : catalog?.TierInfo(rank.Tier);

        var icon = new RectangleF(rect.X + D(16), rect.Y + D(18), D(64), D(64));
        if (tier is not null) Draw.Fit(g, ImageCache.Get(tier.Icon), icon);
        else TextRenderer.DrawText(g, "—", Draw.Big, Rectangle.Round(icon), Theme.Faint, Theme.CenterMiddle);

        var left = (int)icon.Right + D(16);
        var textW = rect.Right - D(16) - left;
        var actName = catalog?.ActName(catalog.CurrentActId()) ?? "";

        if (rank is null)
        {
            TextRenderer.DrawText(g, _loading ? "Loading rank…" : "Rank unavailable", Draw.Big, new Rectangle(left, rect.Y + D(16), textW, D(24)), Theme.Muted, Theme.LeftMiddle);
            return rect.Bottom;
        }

        if (tier is null)
        {
            TextRenderer.DrawText(g, rank.Games > 0 ? "Placements" : "Unranked", Draw.Big, new Rectangle(left, rect.Y + D(16), textW, D(24)), Theme.Text, Theme.LeftMiddle);
            TextRenderer.DrawText(g, rank.Games > 0 ? $"{rank.Games} competitive games this act" : $"No competitive games in {(actName.Length > 0 ? actName : "this act")} yet",
                Theme.Small, new Rectangle(left, rect.Y + D(42), textW, D(18)), Theme.Muted, Theme.LeftMiddle);
            return rect.Bottom;
        }

        TextRenderer.DrawText(g, tier.Name, Draw.Big, new Rectangle(left, rect.Y + D(14), textW, D(24)), Theme.Text, Theme.LeftMiddle);
        TextRenderer.DrawText(g, $"{rank.RankedRating} RR", Theme.BodyBold, new Rectangle(left, rect.Y + D(14), textW, D(24)), Theme.Text, Theme.RightMiddle);

        // Progress bar to the next rank (RR is out of 100 below Immortal).
        var bar = new RectangleF(left, rect.Y + D(46), textW, D(8));
        Theme.FillRound(g, Theme.Raised, bar, D(4));
        var capped = rank.Tier < 24;
        var fraction = capped ? Math.Max(0, Math.Min(1, rank.RankedRating / 100f)) : 1f;
        var fillColor = tier.Color != Color.Empty ? tier.Color : Theme.Accent;
        if (fraction > 0)
            Theme.FillRound(g, fillColor, new RectangleF(bar.X, bar.Y, Math.Max(bar.Height, bar.Width * fraction), bar.Height), D(4));

        // Average gains and what's left to rank up.
        var changes = _rankChanges.Values.Where(c => c.Afk == 0).ToList();
        var wins = changes.Where(c => c.RrEarned > 0).Select(c => c.RrEarned).ToList();
        var losses = changes.Where(c => c.RrEarned < 0).Select(c => c.RrEarned).ToList();
        string line;
        if (!capped)
            line = "Immortal and Radiant RR keeps climbing past 100.";
        else if (wins.Count > 0)
        {
            var avgWin = wins.Average();
            var need = (int)Math.Ceiling((100 - rank.RankedRating) / avgWin);
            var next = catalog?.TierInfo(rank.Tier + 1)?.Name ?? "the next rank";
            line = $"About {Math.Max(1, need)} {(need == 1 ? "win" : "wins")} to {next} · {100 - rank.RankedRating} RR to go";
        }
        else
            line = $"{100 - rank.RankedRating} RR to the next rank";

        TextRenderer.DrawText(g, line, Theme.Small, new Rectangle(left, rect.Y + D(60), textW, D(18)), Theme.Muted, Theme.LeftMiddle);

        var details = new List<string>();
        if (wins.Count > 0) details.Add($"avg +{wins.Average():0} per win");
        if (losses.Count > 0) details.Add($"{losses.Average():0} per loss");
        if (rank.Games > 0) details.Add($"{rank.Wins}W {Math.Max(0, rank.Games - rank.Wins)}L this act");
        if (rank.PeakTier > rank.Tier && catalog?.TierInfo(rank.PeakTier) is { } peak)
            details.Add($"peak {peak.Name}");
        TextRenderer.DrawText(g, string.Join(" · ", details), Theme.Small, new Rectangle(left, rect.Y + D(80), textW, D(18)), Theme.Faint, Theme.LeftMiddle);
        return rect.Bottom;
    }

    /// <summary>This session: your most recent run of games with less than two hours between them.</summary>
    private int PaintSession(Graphics g, int y, int width)
    {
        var matches = Summaries();
        var session = new List<MatchSummary>();
        foreach (var m in matches)
        {
            var last = session.LastOrDefault();
            if (last is null)
            {
                if (DateTime.UtcNow - m.StartUtc > TimeSpan.FromHours(12)) break;
            }
            else if (last.StartUtc - (m.StartUtc + TimeSpan.FromMilliseconds(m.LengthMs)) > TimeSpan.FromHours(2))
                break;

            session.Add(m);
        }

        var rect = new Rectangle(0, y, width, D(96));
        Card(g, rect, D(12));
        var pad = D(16);
        if (session.Count == 0)
        {
            TextRenderer.DrawText(g, "THIS SESSION", Theme.Section, new Rectangle(rect.X + pad, rect.Y + D(14), rect.Width - pad * 2, D(16)), Theme.Faint, Theme.LeftMiddle);
            TextRenderer.DrawText(g, _loadingMatches ? "Loading…" : "No games in the last 12 hours. Your next game starts a new session.", Theme.Small,
                new Rectangle(rect.X + pad, rect.Y + D(40), rect.Width - pad * 2, D(36)), Theme.Muted,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsTranslateTransform | TextFormatFlags.PreserveGraphicsClipping);
            return rect.Bottom;
        }

        var started = session.Last().StartUtc.ToLocalTime();
        TextRenderer.DrawText(g, "THIS SESSION", Theme.Section, new Rectangle(rect.X + pad, rect.Y + D(14), rect.Width - pad * 2, D(16)), Theme.Faint, Theme.LeftMiddle);
        TextRenderer.DrawText(g, "since " + started.ToString("h:mm tt"), Theme.Small, new Rectangle(rect.X + pad, rect.Y + D(14), rect.Width - pad * 2, D(16)), Theme.Muted, Theme.RightMiddle);

        var w = session.Count(m => m.ResultFor(_you) == MatchSummary.Outcome.Win);
        var l = session.Count(m => m.ResultFor(_you) == MatchSummary.Outcome.Loss);
        var d = session.Count - w - l;
        var rr = session.Select(m => _rankChanges.TryGetValue(m.Id, out var c) ? (int?)c.RrEarned : null).Where(v => v.HasValue).Sum(v => v!.Value);
        var anyRanked = session.Any(m => _rankChanges.ContainsKey(m.Id));

        var record = $"{w}W {l}L" + (d > 0 ? $" {d}D" : "");
        var recordW = Draw.TextWidth(record, Draw.Big);
        TextRenderer.DrawText(g, record, Draw.Big, new Rectangle(rect.X + pad, rect.Y + D(36), recordW + D(4), D(26)), Theme.Text, Theme.LeftMiddle);
        if (anyRanked)
        {
            var rrText = (rr >= 0 ? "+" : "") + rr + " RR";
            TextRenderer.DrawText(g, rrText, Theme.BodyBold, new Rectangle(rect.X + pad + recordW + D(12), rect.Y + D(36), D(120), D(26)),
                rr > 0 ? Theme.Online : rr < 0 ? Theme.Accent : Theme.Muted, Theme.LeftMiddle);
        }

        // A pill per game, oldest to newest.
        var x = rect.X + pad;
        foreach (var m in session.AsEnumerable().Reverse())
        {
            var outcome = m.ResultFor(_you);
            var text = _rankChanges.TryGetValue(m.Id, out var c) ? (c.RrEarned >= 0 ? "+" : "") + c.RrEarned : outcome == MatchSummary.Outcome.Win ? "W" : outcome == MatchSummary.Outcome.Loss ? "L" : "D";
            var pw = Draw.TextWidth(text, Theme.Section) + D(14);
            if (x + pw > rect.Right - pad) break;
            var pill = new Rectangle(x, rect.Y + D(66), pw, D(18));
            Theme.FillRound(g, Color.FromArgb(60, ResultColor(outcome)), pill, D(9));
            TextRenderer.DrawText(g, text, Theme.Section, pill, Theme.Text, Theme.CenterMiddle);
            x = pill.Right + D(5);
        }

        return rect.Bottom;
    }

    private void PaintChart(Graphics g, Rectangle rect, string title, List<double> values, List<MatchSummary.Outcome> results, string format)
    {
        Card(g, rect, D(10));
        var pad = D(12);
        var avg = values.Count > 0 ? values.Average() : 0;
        TextRenderer.DrawText(g, title, Theme.Small, new Rectangle(rect.X + pad, rect.Y + D(8), rect.Width - pad * 2, D(18)), Theme.Muted, Theme.LeftMiddle);
        TextRenderer.DrawText(g, "avg " + avg.ToString(format), Theme.BodyBold, new Rectangle(rect.X + pad, rect.Y + D(8), rect.Width - pad * 2, D(18)), Theme.Text, Theme.RightMiddle);

        var plot = new RectangleF(rect.X + pad, rect.Y + D(36), rect.Width - pad * 2, rect.Height - D(52));
        if (values.Count < 2) return;
        var min = values.Min();
        var max = values.Max();
        if (max - min < 1e-9) { max += 1; min -= 1; }
        var range = max - min;
        min -= range * 0.1; max += range * 0.1;

        PointF At(int i, double v) => new(
            plot.X + plot.Width * i / (values.Count - 1),
            plot.Bottom - (float)((v - min) / (max - min)) * plot.Height);

        // Average line
        using (var dash = new Pen(Theme.Border, Math.Max(1f, S)) { DashStyle = DashStyle.Dash })
        {
            var ay = At(0, avg).Y;
            g.DrawLine(dash, plot.X, ay, plot.Right, ay);
        }

        var points = values.Select((v, i) => At(i, v)).ToArray();
        using (var line = new Pen(ColorTranslator.FromHtml("#9FB4FF"), Math.Max(1.5f, 1.8f * S)) { LineJoin = LineJoin.Round })
            g.DrawLines(line, points);
        for (var i = 0; i < points.Length; i++)
            Theme.Dot(g, ResultColor(results[i]), points[i].X, points[i].Y, D(6));
    }

    // ---- Matches

    private int PaintMatches(Graphics g, int y, int width)
    {
        foreach (var entry in _entries)
        {
            var m = MatchData.Cached(entry.MatchId);
            var rect = new Rectangle(0, y, width, D(62));
            if (m is null || m.Find(_you) is null)
            {
                Card(g, rect, D(10));
                var label = _failed.Contains(entry.MatchId) ? "Couldn't load this match" : "Loading…";
                TextRenderer.DrawText(g, $"{QueueName(entry.Queue)} · {label}", Theme.Small, Rectangle.Inflate(rect, -D(16), 0), Theme.Faint, Theme.LeftMiddle);
                TextRenderer.DrawText(g, Ago(entry.StartUtc), Theme.Small, Rectangle.Inflate(rect, -D(16), 0), Theme.Faint, Theme.RightMiddle);
                y = rect.Bottom + D(8);
                continue;
            }

            PaintMatchRow(g, rect, m);
            Hit(rect, "m:" + m.Id);
            y = rect.Bottom + D(8);
            if (_expanded.Contains(m.Id))
                y = PaintScoreboard(g, y - D(2), width, m) + D(12);
        }

        if (_entries.Count < _total)
        {
            var busy = _loading || _loadingMatches;
            var text = busy ? "Loading…" : $"Load {Math.Min(PageSize, _total - _entries.Count)} more";
            var bw = Draw.TextWidth(text, Theme.Small) + D(40);
            var b = new Rectangle((width - bw) / 2, y + D(4), bw, D(30));
            Draw.Button(g, b, text, HoverKey == "more", !busy, S);
            if (!busy) Hit(b, "more");
            y = b.Bottom + D(10);
        }

        TextRenderer.DrawText(g, "Click a match for the scoreboard. Players are only named if they're you, your party, your friends, or showed their name in agent select.",
            Theme.Small, new Rectangle(0, y + D(4), width, D(34)), Theme.Faint,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsTranslateTransform | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.NoPadding);
        return y + D(44);
    }

    private void PaintMatchRow(Graphics g, Rectangle rect, MatchSummary m)
    {
        var me = m.Find(_you)!;
        var outcome = m.ResultFor(_you);
        var hover = HoverKey == "m:" + m.Id;
        var rf = new RectangleF(rect.X + 0.5f, rect.Y + 0.5f, rect.Width - 1, rect.Height - 1);
        Theme.FillRound(g, hover ? Theme.Hover : Theme.Surface, rf, D(10));
        Theme.StrokeRound(g, Theme.Border, rf, D(10));

        // Result stripe on the left.
        using (var path = Theme.Round(rf, D(10)))
        {
            var state = g.Save();
            g.SetClip(path, CombineMode.Intersect);
            using var brush = new SolidBrush(ResultColor(outcome));
            g.FillRectangle(brush, rect.X, rect.Y, D(4), rect.Height);
            g.Restore(state);
        }

        ValorantContent.Agent? agent = null;
        _catalog?.Agents.TryGetValue(me.Agent, out agent);
        var icon = new RectangleF(rect.X + D(14), rect.Y + D(10), D(42), D(42));
        Theme.FillRound(g, Theme.Raised, icon, D(8));
        if (agent is not null) Draw.Fit(g, ImageCache.Get(agent.Icon), icon);

        var left = (int)icon.Right + D(12);
        var right = rect.Right - D(14);

        // Right side: score + RR (line 1), time (line 2)
        var score = m.ScoreFor(_you);
        var resultText = outcome switch { MatchSummary.Outcome.Win => "Win", MatchSummary.Outcome.Loss => "Loss", MatchSummary.Outcome.Draw => "Draw", _ => "" };
        var top = $"{resultText} {score}".Trim();
        var x = right;
        if (_rankChanges.TryGetValue(m.Id, out var change))
        {
            var rrText = (change.RrEarned >= 0 ? "+" : "") + change.RrEarned + " RR";
            var rw = Draw.TextWidth(rrText, Theme.Section) + D(14);
            var pill = new Rectangle(x - rw, rect.Y + D(11), rw, D(20));
            Theme.FillRound(g, Color.FromArgb(55, change.RrEarned >= 0 ? Theme.Online : Theme.Accent), pill, D(10));
            TextRenderer.DrawText(g, rrText, Theme.Section, pill, Theme.Text, Theme.CenterMiddle);
            x = pill.Left - D(8);
        }

        var tw = Draw.TextWidth(top, Theme.BodyBold);
        TextRenderer.DrawText(g, top, Theme.BodyBold, new Rectangle(x - tw, rect.Y + D(10), tw + D(2), D(22)), ResultColor(outcome), Theme.RightMiddle);
        var ago = Ago(m.StartUtc);
        var aw = Draw.TextWidth(ago, Theme.Small);
        TextRenderer.DrawText(g, ago, Theme.Small, new Rectangle(right - aw, rect.Y + D(34), aw + D(2), D(18)), Theme.Faint, Theme.RightMiddle);

        // Left side: map · mode (line 1), stats (line 2)
        var map = _catalog?.MapName(m.Map) is { Length: > 0 } mn ? mn : "Unknown map";
        var line1Space = Math.Max(0, x - tw - D(12) - left);
        TextRenderer.DrawText(g, $"{map} · {QueueName(m.Queue)}", Theme.BodyBold, new Rectangle(left, rect.Y + D(10), line1Space, D(22)), Theme.Text, Theme.LeftMiddle);
        var stats = $"{me.Kills}/{me.Deaths}/{me.Assists} · ACS {me.Acs:0} · HS {me.HeadshotPct:0}%";
        TextRenderer.DrawText(g, stats, Theme.Small, new Rectangle(left, rect.Y + D(34), Math.Max(0, right - aw - D(12) - left), D(18)), Theme.Muted, Theme.LeftMiddle);
    }

    private int PaintScoreboard(Graphics g, int y, int width, MatchSummary m)
    {
        var me = m.Find(_you)!;
        var teams = m.Players.GroupBy(p => p.Team, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(t => string.Equals(t.Key, me.Team, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (m.IsDeathmatch)
            teams = new List<IGrouping<string, MatchSummary.Player>> { m.Players.GroupBy(_ => "all").First() };

        // Parties of 2+ get a colored marker.
        var partyColors = new[] { "#F2B63C", "#4AA3FF", "#B57BFF", "#3CCB7F", "#FF8A5B" }.Select(ColorTranslator.FromHtml).ToArray();
        var parties = m.Players.Where(p => p.PartyId.Length > 0).GroupBy(p => p.PartyId).Where(gr => gr.Count() > 1)
            .Select((gr, i) => (gr.Key, color: partyColors[i % partyColors.Length])).ToDictionary(x => x.Key, x => x.color);

        var colAcs = width - D(250);
        var colKda = width - D(185);
        var colHs = width - D(80);

        foreach (var team in teams)
        {
            var yours = string.Equals(team.Key, me.Team, StringComparison.OrdinalIgnoreCase);
            var rounds = m.Teams.TryGetValue(team.Key, out var t) ? t.rounds : 0;
            var title = m.IsDeathmatch ? "EVERYONE" : (yours ? "YOUR TEAM" : "ENEMY TEAM") + $" · {rounds}";
            var head = new Rectangle(D(4), y, width - D(8), D(18));
            TextRenderer.DrawText(g, title, Theme.Section, head, yours ? Theme.Online : Theme.Accent, Theme.LeftMiddle);
            TextRenderer.DrawText(g, "ACS", Theme.Section, new Rectangle(colAcs, y, D(50), D(18)), Theme.Faint, Theme.RightMiddle);
            TextRenderer.DrawText(g, "K / D / A", Theme.Section, new Rectangle(colKda, y, D(90), D(18)), Theme.Faint, Theme.CenterMiddle);
            TextRenderer.DrawText(g, "HS%", Theme.Section, new Rectangle(colHs, y, D(70), D(18)), Theme.Faint, Theme.RightMiddle);
            y += D(22);

            foreach (var p in team.OrderByDescending(p => p.Score))
            {
                var row = new Rectangle(0, y, width, D(36));
                var isYou = string.Equals(p.Puuid, _you, StringComparison.OrdinalIgnoreCase);
                Theme.FillRound(g, isYou ? Theme.Raised : Theme.Surface, new RectangleF(row.X + 0.5f, row.Y + 0.5f, row.Width - 1, row.Height - 1), D(7));

                if (parties.TryGetValue(p.PartyId, out var pc))
                    Theme.FillRound(g, pc, new RectangleF(row.X + D(4), row.Y + D(8), D(3), row.Height - D(16)), D(1.5f));

                ValorantContent.Agent? agent = null;
                _catalog?.Agents.TryGetValue(p.Agent, out agent);
                var icon = new RectangleF(row.X + D(12), row.Y + D(5), D(26), D(26));
                if (agent is not null) Draw.Fit(g, ImageCache.Get(agent.Icon), icon);

                var tier = _catalog?.TierInfo(p.Tier);
                var rankBox = new RectangleF(row.X + D(46), row.Y + D(8), D(20), D(20));
                if (tier is not null) Draw.Fit(g, ImageCache.Get(tier.Icon), rankBox);

                var name = DisplayName(m, p, out var named);
                TextRenderer.DrawText(g, name, isYou ? Theme.BodyBold : Theme.Body, new Rectangle(row.X + D(74), row.Y, colAcs - D(84), row.Height),
                    named ? Theme.Text : Theme.Muted, Theme.LeftMiddle);
                TextRenderer.DrawText(g, p.Acs.ToString("0"), Theme.Body, new Rectangle(colAcs, row.Y, D(50), row.Height), Theme.Text, Theme.RightMiddle);
                TextRenderer.DrawText(g, $"{p.Kills} / {p.Deaths} / {p.Assists}", Theme.Body, new Rectangle(colKda, row.Y, D(90), row.Height), Theme.Text, Theme.CenterMiddle);
                TextRenderer.DrawText(g, p.HeadshotPct.ToString("0") + "%", Theme.Body, new Rectangle(colHs, row.Y, D(70), row.Height), Theme.Muted, Theme.RightMiddle);
                y = row.Bottom + D(4);
            }

            y += D(10);
        }

        if (parties.Count > 0)
        {
            TextRenderer.DrawText(g, "A colored bar marks players who queued together.", Theme.Small, new Rectangle(D(4), y - D(4), width, D(18)), Theme.Faint, Theme.LeftMiddle);
            y += D(18);
        }

        return y;
    }

    // ---- Maps & agents

    private int PaintBreakdown(Graphics g, int y, int width)
    {
        y = PaintFilter(g, y, width) + D(14);
        var matches = Summaries(_competitiveOnly).Where(m => !m.IsDeathmatch).ToList();
        if (matches.Count == 0)
            return PaintNoteBox(g, y, width, _loadingMatches ? "Loading matches…" : "No matches to break down yet.") + D(16);

        TextRenderer.DrawText(g, $"From your last {matches.Count} matches. Load more on the Matches page to include older games.", Theme.Small,
            new Rectangle(0, y, width, D(18)), Theme.Faint, Theme.LeftMiddle);
        y += D(28);

        var maps = matches.GroupBy(m => m.Map, StringComparer.OrdinalIgnoreCase)
            .Select(gr => (name: _catalog?.MapName(gr.Key) is { Length: > 0 } n ? n : "Unknown", icon: "", games: gr.ToList()))
            .OrderByDescending(x => x.games.Count).ToList();
        y = PaintTable(g, y, width, "MAPS", maps) + D(20);

        var agents = matches.GroupBy(m => m.Find(_you)!.Agent, StringComparer.OrdinalIgnoreCase)
            .Select(gr =>
            {
                ValorantContent.Agent? a = null;
                _catalog?.Agents.TryGetValue(gr.Key, out a);
                return (name: a?.Name ?? "New agent", icon: a?.Icon ?? "", games: gr.ToList());
            })
            .OrderByDescending(x => x.games.Count).ToList();
        return PaintTable(g, y, width, "AGENTS", agents) + D(16);
    }

    private int PaintTable(Graphics g, int y, int width, string title, List<(string name, string icon, List<MatchSummary> games)> rows)
    {
        // Right edges of the number columns, and the left edge of the win % bar.
        var colKd = width - D(14);
        var colAcs = width - D(84);
        var colWin = width - D(232);
        var colGames = width - D(252);
        var head = new Rectangle(0, y, width, D(16));
        TextRenderer.DrawText(g, title, Theme.Section, head, Theme.Faint, Theme.LeftMiddle);
        TextRenderer.DrawText(g, "GAMES", Theme.Section, new Rectangle(colGames - D(50), y, D(50), D(16)), Theme.Faint, Theme.RightMiddle);
        TextRenderer.DrawText(g, "WIN %", Theme.Section, new Rectangle(colWin, y, D(90), D(16)), Theme.Faint, Theme.LeftMiddle);
        TextRenderer.DrawText(g, "ACS", Theme.Section, new Rectangle(colAcs - D(50), y, D(50), D(16)), Theme.Faint, Theme.RightMiddle);
        TextRenderer.DrawText(g, "K/D", Theme.Section, new Rectangle(colKd - D(50), y, D(50), D(16)), Theme.Faint, Theme.RightMiddle);
        y += D(22);

        foreach (var (name, icon, games) in rows)
        {
            var row = new Rectangle(0, y, width, D(38));
            Theme.FillRound(g, Theme.Surface, new RectangleF(row.X + 0.5f, row.Y + 0.5f, row.Width - 1, row.Height - 1), D(8));
            var textX = row.X + D(12);
            if (icon.Length > 0)
            {
                Draw.Fit(g, ImageCache.Get(icon), new RectangleF(row.X + D(8), row.Y + D(6), D(26), D(26)));
                textX = row.X + D(42);
            }

            var me = games.Select(m => m.Find(_you)!).ToList();
            var wins = games.Count(m => m.ResultFor(_you) == MatchSummary.Outcome.Win);
            var pct = (double)wins / games.Count;
            TextRenderer.DrawText(g, name, Theme.BodyBold, new Rectangle(textX, row.Y, colGames - D(60) - textX, row.Height), Theme.Text, Theme.LeftMiddle);
            TextRenderer.DrawText(g, games.Count.ToString(), Theme.Body, new Rectangle(colGames - D(50), row.Y, D(50), row.Height), Theme.Text, Theme.RightMiddle);

            // Win % with a small bar.
            var bar = new RectangleF(colWin, row.Y + row.Height / 2f - D(3), D(40), D(6));
            Theme.FillRound(g, Theme.Raised, bar, D(3));
            if (pct > 0) Theme.FillRound(g, pct >= 0.5 ? Theme.Online : Theme.Accent, new RectangleF(bar.X, bar.Y, Math.Max(bar.Height, bar.Width * (float)pct), bar.Height), D(3));
            TextRenderer.DrawText(g, $"{pct * 100:0}%", Theme.Body, new Rectangle((int)bar.Right + D(8), row.Y, D(48), row.Height), Theme.Text, Theme.LeftMiddle);

            var kills = me.Sum(p => p.Kills);
            var deaths = me.Sum(p => p.Deaths);
            var rounds = me.Sum(p => p.Rounds);
            var acs = rounds > 0 ? (double)me.Sum(p => p.Score) / rounds : 0;
            TextRenderer.DrawText(g, acs.ToString("0"), Theme.Body, new Rectangle(colAcs - D(50), row.Y, D(50), row.Height), Theme.Text, Theme.RightMiddle);
            TextRenderer.DrawText(g, (deaths > 0 ? (double)kills / deaths : kills).ToString("0.00"), Theme.Body, new Rectangle(colKd - D(50), row.Y, D(50), row.Height), Theme.Text, Theme.RightMiddle);
            y = row.Bottom + D(5);
        }

        return y;
    }
}
