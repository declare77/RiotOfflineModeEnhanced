using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Deceive;

/// <summary>
/// Agent select tab: your team's names, agents, ranks and act records while you pick.
/// It only asks Riot for anything while this tab is open and the window isn't minimized.
/// </summary>
internal sealed class AgentSelectPanel : PagePanel
{
    private readonly Timer _poll = new() { Interval = 3000 };
    private bool _active;
    private bool _busy;

    private ValorantApi.Pregame? _pregame;
    private bool _ended;            // the last agent select finished
    private DateTime _endedAt;
    private string _message = "";   // shown when there's no team to show
    private string _detail = "";
    private string _error = "";     // a problem while a team is showing
    private readonly Dictionary<string, ValorantApi.RankInfo> _ranks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _rankFailed = new(StringComparer.OrdinalIgnoreCase);
    private bool _loadingRanks;
    private DateTime _ranksPausedUntil = DateTime.MinValue;
    private ValorantContent.Catalog? _catalog;

    public AgentSelectPanel(float scale) : base(scale)
    {
        _poll.Tick += async (_, _) => await PollAsync();
        ImageCache.Loaded += OnImageLoaded;
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
            _poll.Dispose();
        }

        base.Dispose(disposing);
    }

    private void SetIdle(string message, string detail)
    {
        _message = message;
        _detail = detail;
    }

    private static (string, string)? FriendLookup(string puuid)
    {
        var prefix = puuid + "@";
        var friend = FriendDirectory.All().FirstOrDefault(f => f.Jid.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return friend is null ? null : (friend.Name, friend.Tag);
    }

    private async Task PollAsync()
    {
        if (_busy || !_active || IsDisposed) return;
        _busy = true;
        try
        {
            if (!ValorantApi.IsValorantRunning())
            {
                _poll.Interval = 5000;
                if (_pregame is null || !_ended)
                {
                    _pregame = null;
                    SetIdle("VALORANT isn't running", "Start VALORANT and queue up. Your team shows up here during agent select.");
                }

                _error = "";
                return;
            }

            _catalog ??= await LoadCatalogAsync();

            var pregame = await ValorantApi.GetPregameAsync(FriendLookup);
            _error = "";
            // Quicker while picking (to follow hovers and locks), slower while waiting.
            _poll.Interval = pregame is null ? 5000 : 3000;

            if (pregame is null)
            {
                if (_pregame is not null && !_ended)
                {
                    _ended = true;
                    _endedAt = DateTime.Now;
                }

                if (_pregame is null)
                    SetIdle("Waiting for agent select", "Your team shows up here as soon as agent select starts.");
                return;
            }

            if (_pregame is null || _pregame.MatchId != pregame.MatchId)
            {
                _ranks.Clear();
                _rankFailed.Clear();
            }

            _pregame = pregame;
            _ended = false;
            _ = LoadRanksAsync(pregame);
        }
        catch (ValorantApiException e)
        {
            // Back off; a rate limit gets a longer pause.
            _poll.Interval = e.Status == 429 ? 60000 : 10000;
            if (_pregame is null)
                SetIdle("Can't check agent select right now", e.Message);
            else
                _error = e.Message;
        }
        catch (Exception e)
        {
            Trace.WriteLine("Agent select failed: " + e);
            _poll.Interval = 10000;
            if (_pregame is null)
                SetIdle("Can't check agent select right now", "Something went wrong. Trying again shortly.");
            else
                _error = "Something went wrong. Trying again shortly.";
        }
        finally
        {
            _busy = false;
            if (!IsDisposed) Invalidate();
        }
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

    /// <summary>One rank lookup per teammate, one at a time, only once per match.</summary>
    private async Task LoadRanksAsync(ValorantApi.Pregame pregame)
    {
        if (_loadingRanks || _catalog is null || DateTime.UtcNow < _ranksPausedUntil) return;
        _loadingRanks = true;
        try
        {
            // Hidden-name players get stats too; only their name stays hidden.
            foreach (var mate in pregame.Team.ToList())
            {
                if (_pregame?.MatchId != pregame.MatchId) return; // a new match started
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

    protected override void OnHitClicked(string key)
    {
        if (key == "action")
        {
            _poll.Interval = 3000;
            _ = PollAsync();
        }
    }

    // ------------------------------------------------------------------ painting

    protected override int PaintContent(Graphics g, int width)
    {
        if (_pregame is null)
        {
            var retry = _message.StartsWith("Can't", StringComparison.Ordinal) ? "Try again" : null;
            return PaintMessage(g, width, _message, _detail, retry, _busy);
        }

        var y = 0;
        var catalog = _catalog;

        // Header: map and mode, plus whether this is live.
        var map = catalog?.MapName(_pregame.MapId) ?? "";
        var mode = ModeName(_pregame.QueueId);
        var title = string.Join(" · ", new[] { map, mode }.Where(s => s.Length > 0));
        if (title.Length == 0) title = "Agent select";
        var header = new Rectangle(0, y, width, D(24));

        var chip = _ended ? $"Ended {_endedAt:h:mm tt}" : "Live";
        var chipW = Draw.TextWidth(chip, Theme.Small) + D(28);
        var chipRect = new Rectangle(width - chipW, y + D(1), chipW, D(22));
        Theme.FillRound(g, Theme.Surface, new RectangleF(chipRect.X + 0.5f, chipRect.Y + 0.5f, chipRect.Width - 1, chipRect.Height - 1), D(11));
        Theme.Dot(g, _ended ? Theme.Faint : Theme.Online, chipRect.X + D(12), chipRect.Y + chipRect.Height / 2f, D(7));
        TextRenderer.DrawText(g, chip, Theme.Small, new Rectangle(chipRect.X + D(20), chipRect.Y, chipRect.Width - D(24), chipRect.Height), Theme.Muted, Theme.LeftMiddle);

        TextRenderer.DrawText(g, title, Draw.Big, new Rectangle(0, y, width - chipW - D(10), header.Height), Theme.Text, Theme.LeftMiddle);
        y += D(26);

        var locked = _pregame.Team.Count(t => t.SelectionState == "locked");
        var sub = $"{locked} of {_pregame.Team.Count} locked in";
        if (catalog?.CurrentActId() is { Length: > 0 } act && catalog.ActName(act) is { Length: > 0 } actName)
            sub += $" · records are for {actName} competitive";
        TextRenderer.DrawText(g, sub, Theme.Small, new Rectangle(0, y, width, D(18)), Theme.Muted, Theme.LeftMiddle);
        y += D(26);

        if (_error.Length > 0)
        {
            TextRenderer.DrawText(g, _error, Theme.Small, new Rectangle(0, y - D(4), width, D(18)), Theme.Warn, Theme.LeftMiddle);
            y += D(18);
        }

        foreach (var mate in _pregame.Team)
        {
            var row = new Rectangle(0, y, width, D(74));
            PaintRow(g, row, mate, catalog);
            y = row.Bottom + D(8);
        }

        y += D(4);
        TextRenderer.DrawText(g, "Hidden names stay hidden unless they're on your friends list. Hidden ranks stay hidden.", Theme.Small,
            new Rectangle(0, y, width, D(32)), Theme.Faint,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        return y + D(36);
    }

    private void PaintRow(Graphics g, Rectangle rect, ValorantApi.Teammate mate, ValorantContent.Catalog? catalog)
    {
        var rf = new RectangleF(rect.X + 0.5f, rect.Y + 0.5f, rect.Width - 1, rect.Height - 1);
        Theme.FillRound(g, Theme.Surface, rf, D(10));
        Theme.StrokeRound(g, mate.IsYou ? Color.FromArgb(110, Theme.Accent) : Theme.Border, rf, D(10));

        // Agent picture (or an empty slot while they haven't hovered anyone).
        ValorantContent.Agent? agent = null;
        if (mate.AgentId.Length > 0) catalog?.Agents.TryGetValue(mate.AgentId, out agent);
        var iconSize = D(48);
        var icon = new RectangleF(rect.X + D(12), rect.Y + (rect.Height - iconSize) / 2f, iconSize, iconSize);
        Theme.FillRound(g, Theme.Raised, icon, D(8));
        var image = agent is null ? null : ImageCache.Get(agent.Icon);
        if (image is not null)
        {
            using var clip = Theme.Round(icon, D(8));
            var state = g.Save();
            g.SetClip(clip);
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
        Color rankColor = Theme.Text;
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

        var recordW = Draw.TextWidth(recordText, Theme.Small);
        var maxRecord = Math.Max(0, (right - left) * 3 / 5);
        recordW = Math.Min(recordW, maxRecord);
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
            var tag = mate.Tag.Length > 0 ? "#" + mate.Tag : "";
            var you = mate.IsYou ? "  (you)" : "";
            var suffix = tag + you;
            var suffixW = Draw.TextWidth(suffix, Theme.Small);
            var nameW = Math.Min(Draw.TextWidth(name, Theme.BodyBold) + D(2), Math.Max(0, nameSpace - suffixW - D(4)));
            TextRenderer.DrawText(g, name, Theme.BodyBold, new Rectangle(left, line1.Y, nameW, line1.Height), Theme.Text, Theme.LeftMiddle);
            if (suffix.Length > 0 && nameSpace - nameW > D(10))
                TextRenderer.DrawText(g, suffix, Theme.Small, new Rectangle(left + nameW + D(3), line1.Y, nameSpace - nameW - D(3), line1.Height), Theme.Faint, Theme.LeftMiddle);
        }

        var agentText = agent?.Name ?? "Choosing…";
        var stateText = mate.SelectionState switch
        {
            "locked" => "Locked",
            "selected" => "Hovering",
            _ => ""
        };
        var info = agentText;
        if (stateText.Length > 0) info += " · " + stateText;
        if (mate.Level > 0) info += $" · Lvl {mate.Level}";
        var infoSpace = Math.Max(0, rightEdge2 - D(12) - left);
        var infoColor = mate.SelectionState == "locked" ? Theme.Text : Theme.Muted;
        TextRenderer.DrawText(g, info, Theme.Small, new Rectangle(left, line2.Y, infoSpace, line2.Height), infoColor, Theme.LeftMiddle);
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
}
