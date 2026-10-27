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
/// Match tab: during agent select, your team's names, agents, ranks and records; once the match
/// loads, both teams with ranks and weapon skins. It only asks Riot for anything while this tab
/// is open and the window isn't minimized, and uses your own chat status to skip checks while
/// you're in the menus or mid-match.
/// </summary>
internal sealed class MatchPanel : PagePanel
{
    private readonly Timer _poll = new() { Interval = 3000 };
    private bool _active;
    private bool _busy;

    private ValorantApi.Pregame? _pregame;
    private bool _pregameEnded;
    private DateTime _pregameEndedAt;

    private ValorantApi.LiveMatch? _live;
    private bool _liveEnded;
    private DateTime _liveEndedAt;
    private DateTime _liveCheckedAt = DateTime.MinValue;

    private string _message = "";   // shown when there's nothing to show
    private string _detail = "";
    private string _error = "";     // a problem while something is showing
    private readonly Dictionary<string, ValorantApi.RankInfo> _ranks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _rankFailed = new(StringComparer.OrdinalIgnoreCase);
    private bool _loadingRanks;
    private DateTime _ranksPausedUntil = DateTime.MinValue;
    private ValorantContent.Catalog? _catalog;

    private static readonly string[] SkinWeapons =
    {
        "9c82e19d-4575-0200-1a81-3eacf00cf872", // Vandal
        "ee8e8d15-496b-07ac-e5f6-8fae5d4c7b1a", // Phantom
        "a03b24d3-4319-996d-0f8c-94bbfba1dfc7", // Operator
        "e336c6b8-418d-9340-d77f-7a9e4cfe0702", // Sheriff
        "2f59173c-4bed-b6c3-2191-dea9b58be9c7"  // Melee
    };

    public MatchPanel(float scale) : base(scale)
    {
        _poll.Tick += async (_, _) => await PollAsync();
        ImageCache.Loaded += OnImageLoaded;
        ValorantPresence.OwnStateChanged += OnOwnStateChanged;
        SetIdle("Waiting for agent select", "Your team shows up here as soon as agent select starts.");
    }

    public void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        _poll.Enabled = active;
        if (active)
            _ = PollAsync();
    }

    // Your presence changed (agent select started, the match loaded or ended): check right away.
    private void OnOwnStateChanged(object? sender, ValorantPresence presence)
    {
        if (IsDisposed || !IsHandleCreated || !_active) return;
        try { BeginInvoke(new Action(() => _ = PollAsync())); }
        catch (InvalidOperationException) { /* closing */ }
    }

    private void OnImageLoaded(object? sender, EventArgs e)
    {
        if (IsDisposed || !IsHandleCreated || !_active) return;
        try { BeginInvoke(new Action(Invalidate)); }
        catch (InvalidOperationException) { /* closing */ }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ImageCache.Loaded -= OnImageLoaded;
            ValorantPresence.OwnStateChanged -= OnOwnStateChanged;
            _poll.Dispose();
        }

        base.Dispose(disposing);
    }

    private void SetIdle(string message, string detail)
    {
        _message = message;
        _detail = detail;
    }

    internal static (string, string)? FriendLookup(string puuid)
    {
        var prefix = puuid + "@";
        var friend = FriendDirectory.All().FirstOrDefault(f => f.Jid.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return friend is null ? null : (friend.Name, friend.Tag);
    }

    // ------------------------------------------------------------------ polling

    private async Task PollAsync()
    {
        if (_busy || !_active || IsDisposed) return;
        _busy = true;
        try
        {
            if (!ValorantApi.IsValorantRunning())
            {
                _poll.Interval = 5000;
                MarkEnded();
                if (_pregame is null && _live is null)
                    SetIdle("VALORANT isn't running", "Start VALORANT and queue up. Your team shows up here during agent select.");
                _error = "";
                return;
            }

            _catalog ??= await LoadCatalogAsync();
            var own = ValorantPresence.Own;

            // In the menus (per your own status): nothing to ask Riot.
            if (own is { InMenus: true })
            {
                _poll.Interval = 5000;
                MarkEnded();
                if (_pregame is null && _live is null)
                    SetIdle("Waiting for agent select", "Your team shows up here as soon as agent select starts.");
                _error = "";
                return;
            }

            if (own is null || own.InAgentSelect)
            {
                var pregame = await ValorantApi.GetPregameAsync(FriendLookup);
                _error = "";
                if (pregame is not null)
                {
                    if (_pregame is null || _pregame.MatchId != pregame.MatchId)
                    {
                        // A new agent select: forget the previous match.
                        _ranks.Clear();
                        _rankFailed.Clear();
                        _live = null;
                    }

                    _pregame = pregame;
                    _pregameEnded = false;
                    _poll.Interval = 3000; // follow hovers and locks
                    _ = LoadRanksAsync(pregame.Team, pregame.MatchId);
                    return;
                }

                if (_pregame is not null && !_pregameEnded)
                {
                    _pregameEnded = true;
                    _pregameEndedAt = DateTime.Now;
                }
            }

            if (own is null || own.InMatch)
                await PollLiveAsync(own is not null);

            if (_pregame is null && _live is null)
                SetIdle("Waiting for agent select", "Your team shows up here as soon as agent select starts.");
            if (_poll.Interval < 5000 && (_live is null || _liveEnded)) _poll.Interval = 5000;
        }
        catch (ValorantApiException e)
        {
            // Back off; a rate limit gets a longer pause.
            _poll.Interval = e.Status == 429 ? 60000 : 10000;
            if (_pregame is null && _live is null)
                SetIdle("Can't check your match right now", e.Message);
            else
                _error = e.Message;
        }
        catch (Exception e)
        {
            Trace.WriteLine("Match tab failed: " + e);
            _poll.Interval = 10000;
            if (_pregame is null && _live is null)
                SetIdle("Can't check your match right now", "Something went wrong. Trying again shortly.");
            else
                _error = "Something went wrong. Trying again shortly.";
        }
        finally
        {
            _busy = false;
            if (!IsDisposed) Invalidate();
        }
    }

    /// <summary>
    /// The live match is fetched once (players and loadouts don't change). After that we only
    /// check whether it's still going, and not even that when your status says you're in it.
    /// </summary>
    private async Task PollLiveAsync(bool presenceSaysInMatch)
    {
        if (_live is not null && !_liveEnded && presenceSaysInMatch)
        {
            _poll.Interval = 15000;
            return;
        }

        if (_live is not null && !_liveEnded && DateTime.UtcNow - _liveCheckedAt < TimeSpan.FromSeconds(14))
            return;

        _liveCheckedAt = DateTime.UtcNow;
        var id = await ValorantApi.GetLiveMatchIdAsync();
        _error = "";
        if (id is null)
        {
            if (_live is not null && !_liveEnded)
            {
                _liveEnded = true;
                _liveEndedAt = DateTime.Now;
            }

            return;
        }

        if (_live is null || _live.MatchId != id || _liveEnded)
        {
            var live = await ValorantApi.GetLiveMatchAsync(id, FriendLookup);
            if (live is null) return;
            _live = live;
            _liveEnded = false;
            if (_pregame is not null && !_pregameEnded)
            {
                _pregameEnded = true;
                _pregameEndedAt = DateTime.Now;
            }
        }

        _poll.Interval = 15000;
        _ = LoadRanksAsync(_live.Players, _live.MatchId);
    }

    private void MarkEnded()
    {
        if (_pregame is not null && !_pregameEnded) { _pregameEnded = true; _pregameEndedAt = DateTime.Now; }
        if (_live is not null && !_liveEnded) { _liveEnded = true; _liveEndedAt = DateTime.Now; }
    }

    private static async Task<ValorantContent.Catalog?> LoadCatalogAsync()
    {
        try { return await ValorantContent.GetAsync(); }
        catch (Exception e)
        {
            Trace.WriteLine("Catalog failed: " + e.Message);
            return null;
        }
    }

    private string CurrentMatchKey => _live is not null && !(_liveEnded && _pregame is not null && !_pregameEnded) ? _live.MatchId : _pregame?.MatchId ?? "";

    /// <summary>One rank lookup per player, one at a time, only once per match.</summary>
    private async Task LoadRanksAsync(List<ValorantApi.Teammate> players, string matchKey)
    {
        if (_loadingRanks || _catalog is null || DateTime.UtcNow < _ranksPausedUntil) return;
        _loadingRanks = true;
        try
        {
            // Hidden-name players get stats too; only their name stays hidden.
            foreach (var mate in players.ToList())
            {
                if (CurrentMatchKey != matchKey) return; // something newer is showing
                if (_ranks.ContainsKey(mate.Puuid) || _rankFailed.Contains(mate.Puuid)) continue;
                try
                {
                    _ranks[mate.Puuid] = await ValorantApi.GetRankAsync(mate.Puuid, _catalog);
                }
                catch (ValorantApiException e) when (e.Status == 429)
                {
                    // Rate limited: leave the rest for a minute rather than asking again.
                    _ranksPausedUntil = DateTime.UtcNow.AddMinutes(1);
                    return;
                }
                catch (Exception e)
                {
                    Trace.WriteLine("Rank failed: " + e.Message);
                    _rankFailed.Add(mate.Puuid);
                }

                if (!IsDisposed) Invalidate();
            }
        }
        finally
        {
            _loadingRanks = false;
            if (!IsDisposed) Invalidate();
        }
    }

    // ------------------------------------------------------------------ header + input

    protected override int HeaderHeight => D(40);

    protected override void PaintHeader(Graphics g, int width)
    {
        var settings = AppSettings.Current;
        var x = PaintOption(g, 0, "opt:alert", "Alert me when agent select starts", settings.AgentSelectAlert);
        PaintOption(g, x + D(18), "opt:sound", "Play a sound", settings.AgentSelectSound);
        using var pen = new Pen(Theme.Border);
        g.DrawLine(pen, 0, D(39), width, D(39));
    }

    private int PaintOption(Graphics g, int x, string key, string text, bool on)
    {
        var box = D(16);
        var textW = Draw.TextWidth(text, Theme.Small);
        var rect = new Rectangle(x, D(4), box + D(8) + textW + D(4), D(24));
        var hover = HoverKey == key;
        FriendList.DrawCheckbox(g, new RectangleF(rect.X + 0.5f, rect.Y + (rect.Height - box) / 2f, box, box), on, hover, S);
        TextRenderer.DrawText(g, text, Theme.Small, new Rectangle(rect.X + box + D(8), rect.Y, textW + D(4), rect.Height), hover ? Theme.Text : Theme.Muted, Theme.LeftMiddle);
        Hit(rect, key);
        return rect.Right;
    }

    protected override void OnHitClicked(string key)
    {
        if (key is "opt:alert" or "opt:sound")
        {
            var settings = AppSettings.Current.Clone();
            if (key == "opt:alert") settings.AgentSelectAlert = !settings.AgentSelectAlert;
            else
            {
                settings.AgentSelectSound = !settings.AgentSelectSound;
                if (settings.AgentSelectSound) Alerts.PlayChime(); // a preview
            }

            AppSettings.Save(settings);
            Invalidate();
            return;
        }

        if (key == "action")
        {
            _poll.Interval = 3000;
            _liveCheckedAt = DateTime.MinValue;
            _ = PollAsync();
        }
    }

    // ------------------------------------------------------------------ painting

    protected override int PaintContent(Graphics g, int width)
    {
        // An agent select in progress wins; otherwise the live (or last) match; otherwise the last agent select.
        if (_pregame is not null && !_pregameEnded)
            return PaintPregame(g, width);
        if (_live is not null)
            return PaintLive(g, width);
        if (_pregame is not null)
            return PaintPregame(g, width);

        var retry = _message.StartsWith("Can't", StringComparison.Ordinal) ? "Try again" : null;
        return PaintMessage(g, width, _message, _detail, retry, _busy);
    }

    private int PaintTitle(Graphics g, int y, int width, string title, bool ended, DateTime endedAt, string sub)
    {
        var chip = ended ? $"Ended {endedAt:h:mm tt}" : "Live";
        var chipW = Draw.TextWidth(chip, Theme.Small) + D(28);
        var chipRect = new Rectangle(width - chipW, y + D(1), chipW, D(22));
        Theme.FillRound(g, Theme.Surface, new RectangleF(chipRect.X + 0.5f, chipRect.Y + 0.5f, chipRect.Width - 1, chipRect.Height - 1), D(11));
        Theme.Dot(g, ended ? Theme.Faint : Theme.Online, chipRect.X + D(12), chipRect.Y + chipRect.Height / 2f, D(7));
        TextRenderer.DrawText(g, chip, Theme.Small, new Rectangle(chipRect.X + D(20), chipRect.Y, chipRect.Width - D(24), chipRect.Height), Theme.Muted, Theme.LeftMiddle);
        TextRenderer.DrawText(g, title, Draw.Big, new Rectangle(0, y, width - chipW - D(10), D(24)), Theme.Text, Theme.LeftMiddle);
        y += D(26);
        TextRenderer.DrawText(g, sub, Theme.Small, new Rectangle(0, y, width, D(18)), Theme.Muted, Theme.LeftMiddle);
        y += D(26);
        if (_error.Length > 0)
        {
            TextRenderer.DrawText(g, _error, Theme.Small, new Rectangle(0, y - D(4), width, D(18)), Theme.Warn, Theme.LeftMiddle);
            y += D(18);
        }

        return y;
    }

    private string ActSuffix()
    {
        var catalog = _catalog;
        return catalog?.CurrentActId() is { Length: > 0 } act && catalog.ActName(act) is { Length: > 0 } actName
            ? $" · records are for {actName} competitive"
            : "";
    }

    private int PaintPregame(Graphics g, int width)
    {
        var pregame = _pregame!;
        var map = _catalog?.MapName(pregame.MapId) ?? "";
        var title = string.Join(" · ", new[] { map, ModeName(pregame.QueueId) }.Where(s => s.Length > 0));
        var locked = pregame.Team.Count(t => t.SelectionState == "locked");
        var y = PaintTitle(g, 0, width, title.Length > 0 ? title : "Agent select", _pregameEnded, _pregameEndedAt,
            $"{locked} of {pregame.Team.Count} locked in" + ActSuffix());

        var you = pregame.Team.FirstOrDefault(t => t.IsYou)?.Puuid ?? "";
        var parties = PartyColors(pregame.Team);
        foreach (var mate in pregame.Team)
        {
            var row = new Rectangle(0, y, width, D(74));
            PaintRow(g, row, mate, you, parties, showSkins: false);
            y = row.Bottom + D(8);
        }

        return PaintFootnote(g, y + D(4), width, parties.Count > 0);
    }

    private int PaintLive(Graphics g, int width)
    {
        var live = _live!;
        var map = _catalog?.MapName(live.MapId) ?? "";
        var queue = ValorantPresence.Own is { } own && own.QueueId.Length > 0 ? ModeName(own.QueueId) : ModeFromId(live.ModeId);
        var title = string.Join(" · ", new[] { map, queue }.Where(s => s.Length > 0));
        var y = PaintTitle(g, 0, width, title.Length > 0 ? title : "Match", _liveEnded, _liveEndedAt, "In match" + ActSuffix());

        var you = live.Players.FirstOrDefault(t => t.IsYou)?.Puuid ?? "";
        var parties = PartyColors(live.Players);
        var deathmatch = live.Players.Select(p => p.Team).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 2;
        var groups = deathmatch
            ? new List<(string title, List<ValorantApi.Teammate> players, Color color)> { ("EVERYONE", live.Players, Theme.Muted) }
            : live.Players.GroupBy(p => p.Team, StringComparer.OrdinalIgnoreCase)
                .Select(gr => (string.Equals(gr.Key, live.YourTeam, StringComparison.OrdinalIgnoreCase) ? "YOUR TEAM" : "ENEMY TEAM",
                    gr.ToList(), string.Equals(gr.Key, live.YourTeam, StringComparison.OrdinalIgnoreCase) ? Theme.Online : Theme.Accent))
                .ToList();

        foreach (var (groupTitle, players, color) in groups)
        {
            TextRenderer.DrawText(g, groupTitle, Theme.Section, new Rectangle(0, y, width, D(16)), color, Theme.LeftMiddle);
            y += D(22);
            foreach (var mate in players)
            {
                var row = new Rectangle(0, y, width, D(92));
                PaintRow(g, row, mate, you, parties, showSkins: true);
                y = row.Bottom + D(8);
            }

            y += D(10);
        }

        return PaintFootnote(g, y, width, parties.Count > 0);
    }

    private int PaintFootnote(Graphics g, int y, int width, bool parties)
    {
        var text = "Hidden names stay hidden unless they're on your friends list. Hidden ranks stay hidden.";
        if (parties)
            text += " A colored bar marks players in the same party; the app can only see parties of you and your friends.";
        TextRenderer.DrawText(g, text, Theme.Small, new Rectangle(0, y, width, D(48)), Theme.Faint,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsTranslateTransform | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.NoPadding);
        return y + D(52);
    }

    /// <summary>
    /// Parties of two or more, as far as the app can tell: you and your party (from your own status)
    /// and friends (from theirs). Riot doesn't share other players' parties during a match.
    /// </summary>
    private static Dictionary<string, Color> PartyColors(List<ValorantApi.Teammate> players)
    {
        var partyOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var own = ValorantPresence.Own;
        foreach (var p in players)
        {
            if (p.IsYou && own is not null && own.PartyId.Length > 0)
            {
                partyOf[p.Puuid] = own.PartyId;
                continue;
            }

            var prefix = p.Puuid + "@";
            var friend = FriendDirectory.All().FirstOrDefault(f => f.Jid.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (friend is not null && FriendDirectory.GetValorant(friend.Jid) is { PartyId.Length: > 0 } vp)
                partyOf[p.Puuid] = vp.PartyId;
        }

        var palette = new[] { "#F2B63C", "#4AA3FF", "#B57BFF", "#3CCB7F", "#FF8A5B" }.Select(ColorTranslator.FromHtml).ToArray();
        var colors = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        foreach (var group in partyOf.GroupBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase).Where(gr => gr.Count() > 1))
        {
            var color = palette[i++ % palette.Length];
            foreach (var member in group) colors[member.Key] = color;
        }

        return colors;
    }

    private void PaintRow(Graphics g, Rectangle rect, ValorantApi.Teammate mate, string you, Dictionary<string, Color> parties, bool showSkins)
    {
        var catalog = _catalog;
        var rf = new RectangleF(rect.X + 0.5f, rect.Y + 0.5f, rect.Width - 1, rect.Height - 1);
        Theme.FillRound(g, Theme.Surface, rf, D(10));
        Theme.StrokeRound(g, mate.IsYou ? Color.FromArgb(110, Theme.Accent) : Theme.Border, rf, D(10));
        if (parties.TryGetValue(mate.Puuid, out var partyColor))
            Theme.FillRound(g, partyColor, new RectangleF(rect.X + D(4), rect.Y + D(12), D(3), rect.Height - D(24)), D(1.5f));

        // Agent picture (or an empty slot while they haven't hovered anyone).
        ValorantContent.Agent? agent = null;
        if (mate.AgentId.Length > 0) catalog?.Agents.TryGetValue(mate.AgentId, out agent);
        var iconSize = D(48);
        var icon = new RectangleF(rect.X + D(12), rect.Y + D(13), iconSize, iconSize);
        Theme.FillRound(g, Theme.Raised, icon, D(8));
        var image = agent is null ? null : ImageCache.Get(agent.Icon);
        if (image is not null)
        {
            using var clip = Theme.Round(icon, D(8));
            var state = g.Save();
            g.SetClip(clip, CombineMode.Intersect);
            Draw.Fit(g, image, icon);
            g.Restore(state);
            if (mate.SelectionState != "locked")
            {
                // Hovering only: fade it so locked picks stand out.
                using var fade = new SolidBrush(Color.FromArgb(110, Theme.Surface));
                g.FillPath(fade, clip);
            }
        }
        else
        {
            TextRenderer.DrawText(g, "?", Draw.Big, Rectangle.Round(icon), Theme.Faint, Theme.CenterMiddle);
        }

        var left = (int)icon.Right + D(14);
        var line1 = new Rectangle(left, rect.Y + D(14), 0, D(22));
        var line2 = new Rectangle(left, rect.Y + D(40), 0, D(20));
        var right = rect.Right - D(14);

        // ---- right side: rank (line 1) and record (line 2)
        _ranks.TryGetValue(mate.Puuid, out var rank);
        string rankText, recordText;
        var rankColor = Theme.Text;
        Image? rankImage = null;

        if (rank is { RankHidden: true } && !mate.IsYou)
        {
            // They chose to hide their rank, so it stays hidden.
            rankText = "Rank hidden";
            recordText = "";
            rankColor = Theme.Muted;
        }
        else if (rank is null)
        {
            var tier = catalog?.TierInfo(mate.PregameTier);
            rankText = _rankFailed.Contains(mate.Puuid) || catalog is null ? (tier?.Name ?? "Unavailable") : "Loading…";
            recordText = "";
            rankColor = Theme.Muted;
            if (tier is not null) rankImage = ImageCache.Get(tier.Icon);
        }
        else
        {
            var tier = catalog?.TierInfo(rank.Tier);
            if (tier is not null)
            {
                rankText = $"{tier.Name} · {rank.RankedRating} RR";
                rankImage = ImageCache.Get(tier.Icon);
            }
            else
            {
                rankText = rank.Games > 0 ? "Placements" : "Unranked";
                rankColor = Theme.Muted;
            }

            var parts = new List<string>();
            if (rank.Games > 0)
            {
                var losses = Math.Max(0, rank.Games - rank.Wins);
                var pct = (int)Math.Round(100.0 * rank.Wins / rank.Games);
                parts.Add($"{rank.Wins}W {losses}L · {pct}%");
            }
            else
            {
                parts.Add("No games this act");
            }

            if (rank.PeakTier > rank.Tier && catalog?.TierInfo(rank.PeakTier) is { } peak)
            {
                var when = catalog.ActName(rank.PeakActId);
                parts.Add("Peak " + peak.Name + (when.Length > 0 ? $" ({when})" : ""));
            }

            recordText = string.Join("  ·  ", parts);
        }

        var rankW = Draw.TextWidth(rankText, Theme.BodyBold);
        var rankRect = new Rectangle(right - rankW, line1.Y, rankW + D(2), line1.Height);
        TextRenderer.DrawText(g, rankText, Theme.BodyBold, rankRect, rankColor, Theme.RightMiddle);
        var rightEdge1 = rankRect.Left;
        if (rankImage is not null)
        {
            var size = D(24);
            Draw.Fit(g, rankImage, new RectangleF(rankRect.Left - D(6) - size, line1.Y + (line1.Height - size) / 2f, size, size));
            rightEdge1 = rankRect.Left - D(6) - size;
        }

        var recordW = Math.Min(Draw.TextWidth(recordText, Theme.Small), Math.Max(0, (right - left) * 3 / 5));
        var recordRect = new Rectangle(right - recordW, line2.Y, recordW + D(2), line2.Height);
        if (recordText.Length > 0)
            TextRenderer.DrawText(g, recordText, Theme.Small, recordRect, Theme.Muted, Theme.LeftMiddle | TextFormatFlags.Right);
        var rightEdge2 = recordText.Length > 0 ? recordRect.Left : right;

        // ---- left side: name (line 1), agent + level (line 2)
        var nameSpace = Math.Max(0, rightEdge1 - D(12) - left);
        if (mate.Hidden)
        {
            TextRenderer.DrawText(g, "Hidden name", Theme.BodyBold, new Rectangle(left, line1.Y, nameSpace, line1.Height), Theme.Muted, Theme.LeftMiddle);
        }
        else
        {
            var name = mate.Name.Length > 0 ? mate.Name : "Player";
            var suffix = (mate.Tag.Length > 0 ? "#" + mate.Tag : "") + (mate.IsYou ? "  (you)" : "");
            var suffixW = Draw.TextWidth(suffix, Theme.Small);
            var nameW = Math.Min(Draw.TextWidth(name, Theme.BodyBold) + D(2), Math.Max(0, nameSpace - suffixW - D(4)));
            TextRenderer.DrawText(g, name, Theme.BodyBold, new Rectangle(left, line1.Y, nameW, line1.Height), Theme.Text, Theme.LeftMiddle);
            if (suffix.Length > 0 && nameSpace - nameW > D(10))
                TextRenderer.DrawText(g, suffix, Theme.Small, new Rectangle(left + nameW + D(3), line1.Y, nameSpace - nameW - D(3), line1.Height), Theme.Faint, Theme.LeftMiddle);
        }

        var info = agent?.Name ?? "Choosing…";
        if (!showSkins)
        {
            var stateText = mate.SelectionState switch { "locked" => "Locked", "selected" => "Hovering", _ => "" };
            if (stateText.Length > 0) info += " · " + stateText;
        }

        if (mate.Level > 0) info += $" · Lvl {mate.Level}";

        // "Played with you before", from the matches the app has seen.
        if (!mate.IsYou && you.Length > 0)
        {
            var (with, withWins, against, _) = MatchData.History(you, mate.Puuid);
            if (with > 0) info += $" · With you {with}× ({withWins}W {with - withWins}L)";
            else if (against > 0) info += $" · Played against you {against}×";
        }

        var infoSpace = Math.Max(0, rightEdge2 - D(12) - left);
        var infoColor = mate.SelectionState == "locked" ? Theme.Text : Theme.Muted;
        TextRenderer.DrawText(g, info, Theme.Small, new Rectangle(left, line2.Y, infoSpace, line2.Height), infoColor, Theme.LeftMiddle);

        // ---- skins (live match only)
        if (showSkins && catalog is not null)
        {
            var skins = SkinWeapons
                .Select(w => mate.Loadout.TryGetValue(w, out var ids) ? ids.Select(id => catalog.Skins.TryGetValue(id, out var s) ? s : null).FirstOrDefault(s => s is not null) : null)
                .Where(s => s is not null && s.Rarity != Color.Empty) // standard skins have no rarity
                .Select(s => s!)
                .ToList();
            var line3 = new Rectangle(left, rect.Y + D(64), right - left, D(18));
            if (skins.Count == 0)
            {
                TextRenderer.DrawText(g, mate.Loadout.Count == 0 ? "Skins unavailable" : "Standard skins", Theme.Small, line3, Theme.Faint, Theme.LeftMiddle);
            }
            else
            {
                var x = line3.X;
                foreach (var skin in skins)
                {
                    var w = Draw.TextWidth(skin.Name, Theme.Small) + D(16);
                    if (x + w > line3.Right) break;
                    var chip = new Rectangle(x, line3.Y, w, line3.Height);
                    Theme.FillRound(g, Color.FromArgb(50, skin.Rarity), chip, D(9));
                    TextRenderer.DrawText(g, skin.Name, Theme.Small, chip, Theme.Text, Theme.CenterMiddle);
                    x = chip.Right + D(5);
                }
            }
        }
    }

    private static string ModeName(string queueId) => queueId.ToLowerInvariant() switch
    {
        "competitive" => "Competitive",
        "unrated" => "Unrated",
        "swiftplay" => "Swiftplay",
        "spikerush" => "Spike Rush",
        "premier" => "Premier",
        "deathmatch" => "Deathmatch",
        "ggteam" => "Escalation",
        "hurm" => "Team Deathmatch",
        "" => "Custom game",
        var other => char.ToUpperInvariant(other[0]) + other.Substring(1)
    };

    private static string ModeFromId(string modeId)
    {
        var m = modeId.ToLowerInvariant();
        if (m.Contains("deathmatch")) return "Deathmatch";
        if (m.Contains("quickbomb")) return "Spike Rush";
        if (m.Contains("ggteam")) return "Escalation";
        if (m.Contains("hurm")) return "Team Deathmatch";
        if (m.Contains("bomb")) return "Standard";
        return "";
    }
}
