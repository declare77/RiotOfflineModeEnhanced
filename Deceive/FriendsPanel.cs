using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Deceive;

/// <summary>
/// Friends tab: what your friends are doing in VALORANT right now (from their chat status, which
/// the app already receives, so no requests), and your current party with ranks.
/// </summary>
internal sealed class FriendsPanel : PagePanel
{
    private static readonly string[] Views = { "Friends", "Party" };
    private const int FriendsView = 0, PartyView = 1;
    private int _view;
    private bool _active;
    private bool _showOffline;

    private readonly SearchBox _search;
    private readonly Timer _refresh = new() { Interval = 400 };
    private readonly Timer _clock = new() { Interval = 30000 };
    private ValorantContent.Catalog? _catalog;

    // Party
    private ValorantApi.Party? _party;
    private bool _partyLoading;
    private string _partyError = "";
    private DateTime _partyLoadedAt = DateTime.MinValue;
    private readonly Dictionary<string, ValorantApi.RankInfo> _ranks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _rankFailed = new(StringComparer.OrdinalIgnoreCase);
    private bool _loadingRanks;

    public FriendsPanel(float scale) : base(scale)
    {
        _search = new SearchBox(scale, "Search friends");
        _search.TextChanged += (_, _) => { ScrollToTop(); Invalidate(); };
        Controls.Add(_search);

        _refresh.Tick += (_, _) => { _refresh.Stop(); Invalidate(); };
        _clock.Tick += (_, _) =>
        {
            if (!_active) return;
            Invalidate();
            if (_view == PartyView && DateTime.UtcNow - _partyLoadedAt > TimeSpan.FromSeconds(25)) _ = LoadPartyAsync();
        };
        FriendDirectory.Changed += OnFriendsChanged;
        ImageCache.Loaded += OnImageLoaded;
        ValorantPresence.OwnStateChanged += OnOwnChanged;
    }

    public void SetActive(bool active)
    {
        _active = active;
        _clock.Enabled = active;
        if (!active) return;
        _ = LoadCatalogAsync();
        if (_view == PartyView) _ = LoadPartyAsync();
        Invalidate();
    }

    private async Task LoadCatalogAsync()
    {
        if (_catalog is not null) return;
        try
        {
            _catalog = await ValorantContent.GetAsync();
            if (!IsDisposed) Invalidate();
        }
        catch (Exception e)
        {
            Trace.WriteLine("Catalog failed: " + e.Message);
        }
    }

    private void OnFriendsChanged(object? sender, EventArgs e) => Later(() =>
    {
        if (_active && !_refresh.Enabled) _refresh.Start();
    });

    private void OnImageLoaded(object? sender, EventArgs e) => Later(() => { if (_active) Invalidate(); });

    // Your party may have changed (someone joined or left): refresh the party view.
    private void OnOwnChanged(object? sender, ValorantPresence p) => Later(() =>
    {
        if (_active && _view == PartyView) _ = LoadPartyAsync();
    });

    private void Later(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException) { /* closing */ }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            FriendDirectory.Changed -= OnFriendsChanged;
            ImageCache.Loaded -= OnImageLoaded;
            ValorantPresence.OwnStateChanged -= OnOwnChanged;
            _refresh.Dispose();
            _clock.Dispose();
        }

        base.Dispose(disposing);
    }

    // ------------------------------------------------------------------ party loading

    private async Task LoadPartyAsync()
    {
        if (_partyLoading) return;
        _partyLoading = true;
        _partyError = "";
        Invalidate();
        try
        {
            if (!ValorantApi.IsValorantRunning())
            {
                _party = null;
                _partyError = "VALORANT isn't running. Start it to see your party.";
                return;
            }

            await LoadCatalogAsync();
            var party = await ValorantApi.GetPartyAsync(MatchPanel.FriendLookup);
            if (party?.Id != _party?.Id) _rankFailed.Clear();
            _party = party;
            _partyLoadedAt = DateTime.UtcNow;
            if (party is not null) _ = LoadRanksAsync(party);
        }
        catch (ValorantApiException e)
        {
            _partyError = e.Message;
        }
        catch (Exception e)
        {
            Trace.WriteLine("Party failed: " + e);
            _partyError = "Something went wrong loading your party.";
        }
        finally
        {
            _partyLoading = false;
            if (!IsDisposed) Invalidate();
        }
    }

    private async Task LoadRanksAsync(ValorantApi.Party party)
    {
        if (_loadingRanks || _catalog is null) return;
        _loadingRanks = true;
        try
        {
            foreach (var m in party.Members.ToList())
            {
                if (_ranks.ContainsKey(m.Puuid) || _rankFailed.Contains(m.Puuid)) continue;
                try { _ranks[m.Puuid] = await ValorantApi.GetRankAsync(m.Puuid, _catalog); }
                catch (ValorantApiException e) when (e.Status == 429) { return; }
                catch (Exception) { _rankFailed.Add(m.Puuid); }
                if (!IsDisposed) Invalidate();
            }
        }
        finally
        {
            _loadingRanks = false;
        }
    }

    // ------------------------------------------------------------------ input + header

    protected override int HeaderHeight => _view == FriendsView ? D(92) : D(46);

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutSearch();
    }

    private void LayoutSearch()
    {
        if (_search is null) return;
        _search.Bounds = new Rectangle(0, D(42), Math.Max(0, Width - D(2)), D(36));
        _search.Visible = _view == FriendsView;
    }

    protected override void PaintHeader(Graphics g, int width)
    {
        PaintSubTabs(g, 0, Views, _view);
        if (_view == PartyView)
        {
            var text = _partyLoading ? "Loading…" : "Refresh";
            var rw = Draw.TextWidth("Refreshing…", Theme.Small) + D(28);
            var refresh = new Rectangle(width - rw - D(2), 0, rw, D(28));
            Draw.Button(g, refresh, text, HoverKey == "refresh", !_partyLoading, S);
            if (!_partyLoading) Hit(refresh, "refresh");
        }
    }

    protected override void OnHitClicked(string key)
    {
        if (key.StartsWith("sub:", StringComparison.Ordinal) && int.TryParse(key.Substring(4), out var view))
        {
            if (view == _view) return;
            _view = view;
            LayoutSearch();
            ScrollToTop();
            if (view == PartyView) _ = LoadPartyAsync();
            Invalidate();
            return;
        }

        switch (key)
        {
            case "offline":
                _showOffline = !_showOffline;
                Invalidate();
                return;
            case "refresh":
            case "action":
                _ = LoadPartyAsync();
                return;
        }
    }

    // ------------------------------------------------------------------ painting

    protected override int PaintContent(Graphics g, int width) =>
        _view == PartyView ? PaintParty(g, width) : PaintFriends(g, width);

    private int Section(Graphics g, int y, int width, string title, string right = "")
    {
        var rect = new Rectangle(0, y, width, D(16));
        TextRenderer.DrawText(g, title, Theme.Section, rect, Theme.Faint, Theme.LeftMiddle);
        if (right.Length > 0) TextRenderer.DrawText(g, right, Theme.Small, rect, Theme.Muted, Theme.RightMiddle);
        return y + D(24);
    }

    private sealed class Row
    {
        public FriendDirectory.Friend Friend = null!;
        public FriendDirectory.OnlineState State;
        public ValorantPresence? Valorant;
        public DateTime? Since;
    }

    private int PaintFriends(Graphics g, int width)
    {
        var query = _search.Text.Trim();
        var rows = FriendDirectory.All()
            .Where(f => query.Length == 0 || (f.Name + "#" + f.Tag).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            .Select(f => new Row
            {
                Friend = f,
                State = FriendDirectory.GetState(f.Jid),
                Valorant = FriendDirectory.GetValorant(f.Jid),
                Since = FriendDirectory.ValorantSince(f.Jid)
            })
            .ToList();

        if (FriendDirectory.Count == 0)
            return PaintMessage(g, width, "No friends yet", "Your friends show up here once you're signed in to the Riot Client.", null);
        if (rows.Count == 0)
            return PaintMessage(g, width, $"No friends match \"{query}\"", "", null);

        var playing = rows.Where(r => r.State != FriendDirectory.OnlineState.Offline && r.Valorant is not null)
            .OrderBy(r => r.Valorant!.InMatch ? 0 : r.Valorant.InAgentSelect ? 1 : 2)
            .ThenBy(r => r.Friend.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var online = rows.Where(r => r.State != FriendDirectory.OnlineState.Offline && r.Valorant is null)
            .OrderBy(r => r.Friend.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var offline = rows.Where(r => r.State == FriendDirectory.OnlineState.Offline)
            .OrderBy(r => r.Friend.Name, StringComparer.OrdinalIgnoreCase).ToList();

        var y = 0;
        if (playing.Count > 0)
        {
            y = Section(g, y, width, "PLAYING VALORANT", playing.Count.ToString());
            foreach (var r in playing) y = PaintPlayingRow(g, y, width, r) + D(6);
            y += D(14);
        }

        if (online.Count > 0)
        {
            y = Section(g, y, width, "ONLINE", online.Count.ToString());
            foreach (var r in online) y = PaintSimpleRow(g, y, width, r) + D(4);
            y += D(14);
        }

        if (offline.Count > 0)
        {
            var toggle = _showOffline ? "Hide" : "Show";
            y = Section(g, y, width, $"OFFLINE ({offline.Count})", "");
            var tw = Draw.TextWidth(toggle, Theme.Small) + D(20);
            var button = new Rectangle(width - tw, y - D(26), tw, D(22));
            Draw.Button(g, button, toggle, HoverKey == "offline", true, S);
            Hit(button, "offline");
            if (_showOffline)
                foreach (var r in offline) y = PaintSimpleRow(g, y, width, r) + D(4);
        }

        y += D(10);
        TextRenderer.DrawText(g, "Activity comes from your friends' VALORANT status, which Riot chat already sends you. Some details, like the score, are only there for some modes.",
            Theme.Small, new Rectangle(0, y, width, D(34)), Theme.Faint,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        return y + D(40);
    }

    private static string QueueName(string queue) => queue.ToLowerInvariant() switch
    {
        "competitive" => "Competitive",
        "unrated" => "Unrated",
        "swiftplay" => "Swiftplay",
        "spikerush" => "Spike Rush",
        "premier" => "Premier",
        "deathmatch" => "Deathmatch",
        "ggteam" => "Escalation",
        "hurm" => "Team Deathmatch",
        "" => "",
        var other => char.ToUpperInvariant(other[0]) + other.Substring(1)
    };

    private string Activity(ValorantPresence v, out Color color)
    {
        var map = _catalog?.MapName(v.MatchMap) ?? "";
        var queue = v.CustomGameName.Length > 0 || v.ProvisioningFlow.Equals("CustomGame", StringComparison.OrdinalIgnoreCase)
            ? "Custom game"
            : QueueName(v.QueueId);
        var parts = new List<string>();
        if (v.InMatch)
        {
            color = Theme.InGame;
            parts.Add("In a match");
            if (map.Length > 0) parts.Add(map);
            if (queue.Length > 0) parts.Add(queue);
            if (v.ScoreAlly is { } a && v.ScoreEnemy is { } b && a + b > 0) parts.Add($"{a}–{b}");
        }
        else if (v.InAgentSelect)
        {
            color = Theme.Away;
            parts.Add("In agent select");
            if (map.Length > 0) parts.Add(map);
            if (queue.Length > 0) parts.Add(queue);
        }
        else
        {
            color = v.IsIdle ? Theme.Away : Theme.Online;
            parts.Add(v.IsIdle ? "Away" : "In the menus");
            if (queue.Length > 0) parts.Add(queue + " selected");
        }

        if (v.PartySize > 1)
            parts.Add($"party of {v.PartySize}" + (v.MaxPartySize > 0 ? $"/{v.MaxPartySize}" : "") + (v.PartyOpen ? " (open)" : ""));
        return string.Join(" · ", parts);
    }

    private static string For(DateTime? since)
    {
        if (since is null) return "";
        var span = DateTime.UtcNow - since.Value;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"for {(int)span.TotalMinutes}m";
        return $"for {(int)span.TotalHours}h {span.Minutes}m";
    }

    private int PaintPlayingRow(Graphics g, int y, int width, Row r)
    {
        var v = r.Valorant!;
        var rect = new Rectangle(0, y, width, D(58));
        var rf = new RectangleF(rect.X + 0.5f, rect.Y + 0.5f, rect.Width - 1, rect.Height - 1);
        Theme.FillRound(g, Theme.Surface, rf, D(10));
        Theme.StrokeRound(g, Theme.Border, rf, D(10));

        var activity = Activity(v, out var color);
        Theme.Dot(g, color, rect.X + D(18), rect.Y + rect.Height / 2f, D(9));

        var left = rect.X + D(34);
        var right = rect.Right - D(14);

        // Rank from their status (on the right).
        var tier = _catalog?.TierInfo(v.CompetitiveTier);
        var rightEdge = right;
        if (tier is not null)
        {
            var tw = Draw.TextWidth(tier.Name, Theme.Small);
            TextRenderer.DrawText(g, tier.Name, Theme.Small, new Rectangle(right - tw, rect.Y + D(10), tw + D(2), D(20)), Theme.Muted, Theme.RightMiddle);
            Draw.Fit(g, ImageCache.Get(tier.Icon), new RectangleF(right - tw - D(26), rect.Y + D(10), D(20), D(20)));
            rightEdge = right - tw - D(34);
        }

        var since = For(r.Since);
        var sw = Draw.TextWidth(since, Theme.Small);
        if (since.Length > 0)
            TextRenderer.DrawText(g, since, Theme.Small, new Rectangle(right - sw, rect.Y + D(32), sw + D(2), D(18)), Theme.Faint, Theme.RightMiddle);

        var nameW = Draw.TextWidth(r.Friend.Name, Theme.BodyBold) + D(2);
        nameW = Math.Min(nameW, Math.Max(0, rightEdge - left - D(60)));
        TextRenderer.DrawText(g, r.Friend.Name, Theme.BodyBold, new Rectangle(left, rect.Y + D(9), nameW, D(22)), Theme.Text, Theme.LeftMiddle);
        TextRenderer.DrawText(g, "#" + r.Friend.Tag, Theme.Small, new Rectangle(left + nameW + D(3), rect.Y + D(9), Math.Max(0, rightEdge - left - nameW - D(3)), D(22)), Theme.Faint, Theme.LeftMiddle);
        TextRenderer.DrawText(g, activity, Theme.Small, new Rectangle(left, rect.Y + D(32), Math.Max(0, right - sw - D(12) - left), D(18)), Theme.Muted, Theme.LeftMiddle);
        return rect.Bottom;
    }

    private int PaintSimpleRow(Graphics g, int y, int width, Row r)
    {
        var rect = new Rectangle(0, y, width, D(38));
        Theme.FillRound(g, Theme.Surface, new RectangleF(rect.X + 0.5f, rect.Y + 0.5f, rect.Width - 1, rect.Height - 1), D(8));
        Theme.Dot(g, Theme.StateColor(r.State), rect.X + D(18), rect.Y + rect.Height / 2f, D(8));
        var left = rect.X + D(34);
        var state = r.State == FriendDirectory.OnlineState.Offline ? "Offline" : Theme.StateText(r.State);
        var sw = Draw.TextWidth(state, Theme.Small);
        TextRenderer.DrawText(g, state, Theme.Small, new Rectangle(rect.Right - D(14) - sw, rect.Y, sw + D(2), rect.Height), Theme.Muted, Theme.RightMiddle);
        var nameW = Math.Min(Draw.TextWidth(r.Friend.Name, Theme.Body) + D(2), Math.Max(0, rect.Right - D(30) - sw - left - D(60)));
        var color = r.State == FriendDirectory.OnlineState.Offline ? Theme.Muted : Theme.Text;
        TextRenderer.DrawText(g, r.Friend.Name, Theme.Body, new Rectangle(left, rect.Y, nameW, rect.Height), color, Theme.LeftMiddle);
        TextRenderer.DrawText(g, "#" + r.Friend.Tag, Theme.Small, new Rectangle(left + nameW + D(3), rect.Y, Math.Max(0, rect.Right - D(30) - sw - left - nameW), rect.Height), Theme.Faint, Theme.LeftMiddle);
        return rect.Bottom;
    }

    private int PaintParty(Graphics g, int width)
    {
        var party = _party;
        if (party is null)
        {
            if (_partyLoading) return PaintMessage(g, width, "Loading your party…", "", null);
            return PaintMessage(g, width, "Couldn't load your party", _partyError.Length > 0 ? _partyError : "Open this tab while VALORANT is running.", "Try again");
        }

        var y = 0;
        if (_partyError.Length > 0)
        {
            TextRenderer.DrawText(g, _partyError, Theme.Small, new Rectangle(0, y, width, D(18)), Theme.Warn, Theme.LeftMiddle);
            y += D(24);
        }

        var details = new List<string> { $"{party.Members.Count} {(party.Members.Count == 1 ? "player" : "players")}", party.Open ? "Open" : "Closed" };
        if (QueueName(party.Queue) is { Length: > 0 } q) details.Add(q);
        TextRenderer.DrawText(g, "Your party", Draw.Big, new Rectangle(0, y, width, D(24)), Theme.Text, Theme.LeftMiddle);
        y += D(26);
        TextRenderer.DrawText(g, string.Join(" · ", details), Theme.Small, new Rectangle(0, y, width, D(18)), Theme.Muted, Theme.LeftMiddle);
        y += D(28);

        foreach (var m in party.Members)
        {
            var rect = new Rectangle(0, y, width, D(62));
            var rf = new RectangleF(rect.X + 0.5f, rect.Y + 0.5f, rect.Width - 1, rect.Height - 1);
            Theme.FillRound(g, Theme.Surface, rf, D(10));
            Theme.StrokeRound(g, m.IsYou ? Color.FromArgb(110, Theme.Accent) : Theme.Border, rf, D(10));
            var left = rect.X + D(16);
            var right = rect.Right - D(14);

            _ranks.TryGetValue(m.Puuid, out var rank);
            string rankText; string record = "";
            Image? rankImage = null;
            if (rank is { RankHidden: true } && !m.IsYou) rankText = "Rank hidden";
            else if (rank is null) rankText = _rankFailed.Contains(m.Puuid) ? "Unavailable" : "Loading…";
            else
            {
                var tier = _catalog?.TierInfo(rank.Tier);
                rankText = tier is not null ? $"{tier.Name} · {rank.RankedRating} RR" : rank.Games > 0 ? "Placements" : "Unranked";
                if (tier is not null) rankImage = ImageCache.Get(tier.Icon);
                if (rank.Games > 0)
                    record = $"{rank.Wins}W {Math.Max(0, rank.Games - rank.Wins)}L this act · {(int)Math.Round(100.0 * rank.Wins / rank.Games)}%";
            }

            var rw = Draw.TextWidth(rankText, Theme.BodyBold);
            TextRenderer.DrawText(g, rankText, Theme.BodyBold, new Rectangle(right - rw, rect.Y + D(10), rw + D(2), D(22)), rank is null ? Theme.Muted : Theme.Text, Theme.RightMiddle);
            var edge = right - rw;
            if (rankImage is not null)
            {
                Draw.Fit(g, rankImage, new RectangleF(edge - D(30), rect.Y + D(9), D(24), D(24)));
                edge -= D(30);
            }

            if (record.Length > 0)
            {
                var recW = Draw.TextWidth(record, Theme.Small);
                TextRenderer.DrawText(g, record, Theme.Small, new Rectangle(right - recW, rect.Y + D(34), recW + D(2), D(18)), Theme.Muted, Theme.RightMiddle);
            }

            var name = m.Hidden ? "Hidden name" : (m.Name.Length > 0 ? m.Name : "Player");
            var suffix = m.Hidden ? "" : (m.Tag.Length > 0 ? "#" + m.Tag : "") + (m.IsYou ? "  (you)" : "");
            var nameW = Math.Min(Draw.TextWidth(name, Theme.BodyBold) + D(2), Math.Max(0, edge - D(12) - left - D(40)));
            TextRenderer.DrawText(g, name, Theme.BodyBold, new Rectangle(left, rect.Y + D(10), nameW, D(22)), m.Hidden ? Theme.Muted : Theme.Text, Theme.LeftMiddle);
            TextRenderer.DrawText(g, suffix, Theme.Small, new Rectangle(left + nameW + D(3), rect.Y + D(10), Math.Max(0, edge - D(12) - left - nameW), D(22)), Theme.Faint, Theme.LeftMiddle);

            var info = new List<string>();
            if (m.IsOwner) info.Add("Leader");
            info.Add(m.IsReady ? "Ready" : "Not ready");
            if (m.Level > 0) info.Add($"Lvl {m.Level}");
            TextRenderer.DrawText(g, string.Join(" · ", info), Theme.Small, new Rectangle(left, rect.Y + D(34), D(260), D(18)), m.IsReady ? Theme.Muted : Theme.Warn, Theme.LeftMiddle);
            y = rect.Bottom + D(8);
        }

        return y + D(10);
    }
}
