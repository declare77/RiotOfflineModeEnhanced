using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Deceive;

/// <summary>Collection tab: the skins you own, battle pass progress and your missions.</summary>
internal sealed class CollectionPanel : PagePanel
{
    private static readonly string[] Views = { "Skins", "Battle pass", "Missions" };
    private const int SkinsView = 0, PassView = 1, MissionsView = 2;
    private int _view;
    private bool _active;
    private string _weaponFilter = ""; // "" = all weapons

    private bool _loading;
    private string _error = "";
    private DateTime _loadedAt = DateTime.MinValue;
    private ValorantContent.Catalog? _catalog;
    private List<ValorantContent.Skin>? _owned;
    private ProgressData.Contracts? _contracts;
    private bool _refreshedForPass; // re-downloaded the catalog once because this act's pass was missing

    public CollectionPanel(float scale) : base(scale)
    {
        ImageCache.Loaded += OnImageLoaded;
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

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        _error = "";
        Invalidate();
        try
        {
            _catalog ??= await ValorantContent.GetAsync();
            var catalog = _catalog;
            var items = await ProgressData.GetOwnedSkinItemsAsync();
            _owned = items
                .Select(id => catalog.Skins.TryGetValue(id, out var s) ? s : null)
                .Where(s => s is not null && s.RarityRank > 0)
                .Select(s => s!)
                .Distinct()
                .ToList();
            _contracts = await ProgressData.GetContractsAsync();
            if (!_refreshedForPass && ProgressData.CurrentPass(catalog, _contracts, out _) is null)
            {
                // The cached data may predate this act's battle pass; check for a newer copy once.
                _refreshedForPass = true;
                _catalog = await ValorantContent.RefreshAsync();
            }

            _loadedAt = DateTime.UtcNow;
        }
        catch (ValorantApiException e)
        {
            _error = e.Message;
        }
        catch (Exception e)
        {
            Trace.WriteLine("Collection failed: " + e);
            _error = "Something went wrong loading your collection. Try again in a moment.";
        }
        finally
        {
            _loading = false;
            if (!IsDisposed) Invalidate();
        }
    }

    // ------------------------------------------------------------------ header + input

    protected override int HeaderHeight => D(46);

    protected override void PaintHeader(Graphics g, int width)
    {
        PaintSubTabs(g, 0, Views, _view);
        if (_owned is null && _contracts is null) return;
        var text = _loading ? "Loading…" : "Refresh";
        var rw = Draw.TextWidth("Refreshing…", Theme.Small) + D(28);
        var refresh = new Rectangle(width - rw - D(2), 0, rw, D(28));
        Draw.Button(g, refresh, text, HoverKey == "refresh", !_loading, S);
        if (!_loading) Hit(refresh, "refresh");
    }

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

        if (key.StartsWith("weapon:", StringComparison.Ordinal))
        {
            _weaponFilter = key.Substring(7);
            ScrollToTop();
            Invalidate();
            return;
        }

        if (key is "refresh" or "action")
        {
            _loadedAt = DateTime.MinValue;
            _ = LoadAsync();
        }
    }

    // ------------------------------------------------------------------ painting

    protected override int PaintContent(Graphics g, int width)
    {
        if (_owned is null && _contracts is null)
        {
            if (_loading) return PaintMessage(g, width, "Loading your collection…", "", null);
            return PaintMessage(g, width, "Couldn't load your collection", _error.Length > 0 ? _error : "Open this tab while the Riot Client is running.", "Try again");
        }

        var y = 0;
        if (_error.Length > 0)
        {
            TextRenderer.DrawText(g, _error, Theme.Small, new Rectangle(0, y, width, D(18)), Theme.Warn, Theme.LeftMiddle);
            y += D(24);
        }

        return _view switch
        {
            PassView => PaintPass(g, y, width),
            MissionsView => PaintMissions(g, y, width),
            _ => PaintSkins(g, y, width)
        };
    }

    private int Section(Graphics g, int y, int width, string title, string right = "")
    {
        var rect = new Rectangle(0, y, width, D(16));
        TextRenderer.DrawText(g, title, Theme.Section, rect, Theme.Faint, Theme.LeftMiddle);
        if (right.Length > 0) TextRenderer.DrawText(g, right, Theme.Small, rect, Theme.Muted, Theme.RightMiddle);
        return y + D(24);
    }

    private static void Card(Graphics g, Rectangle rect, float radius, Color rarity = default)
    {
        var rf = new RectangleF(rect.X + 0.5f, rect.Y + 0.5f, rect.Width - 1, rect.Height - 1);
        Theme.FillRound(g, Theme.Surface, rf, radius);
        if (rarity != Color.Empty && rarity.A > 0)
        {
            using var path = Theme.Round(rf, radius);
            using var brush = new LinearGradientBrush(rect, Color.FromArgb(40, rarity), Color.FromArgb(0, rarity), LinearGradientMode.Vertical);
            g.FillPath(brush, path);
        }

        Theme.StrokeRound(g, rarity != Color.Empty && rarity.A > 0 ? Color.FromArgb(80, rarity) : Theme.Border, rf, radius);
    }

    // ---- Skins

    private static readonly string[] WeaponOrder =
        { "Vandal", "Phantom", "Operator", "Sheriff", "Melee", "Spectre", "Ghost", "Classic", "Guardian", "Marshal", "Outlaw", "Bulldog", "Judge", "Bucky", "Stinger", "Frenzy", "Shorty", "Bandit", "Odin", "Ares" };

    private static int WeaponRank(string weapon)
    {
        var i = Array.IndexOf(WeaponOrder, weapon);
        return i < 0 ? 99 : i;
    }

    private int PaintSkins(Graphics g, int y, int width)
    {
        var owned = _owned ?? new List<ValorantContent.Skin>();
        if (owned.Count == 0)
            return PaintMessage(g, width, "No skins yet", "Skins you buy or unlock show up here.", null);

        // Summary by rarity
        var rarities = owned.GroupBy(s => s.RarityName).OrderBy(gr => gr.First().RarityRank).ToList();
        TextRenderer.DrawText(g, $"{owned.Count} skins", Draw.Big, new Rectangle(0, y, width, D(24)), Theme.Text, Theme.LeftMiddle);
        y += D(28);
        var x = 0;
        foreach (var gr in rarities)
        {
            var label = $"{gr.Count()} {gr.Key}";
            var w = Draw.TextWidth(label, Theme.Small) + D(36);
            var chip = new Rectangle(x, y, w, D(22));
            Theme.FillRound(g, Color.FromArgb(45, gr.First().Rarity), chip, D(11));
            Draw.Fit(g, ImageCache.Get(gr.First().RarityIcon), new RectangleF(chip.X + D(7), chip.Y + D(4), D(14), D(14)));
            TextRenderer.DrawText(g, label, Theme.Small, new Rectangle(chip.X + D(26), chip.Y, w - D(30), chip.Height), Theme.Text, Theme.LeftMiddle);
            x = chip.Right + D(6);
        }

        y += D(34);

        // Weapon filter chips
        var weapons = owned.GroupBy(s => s.Weapon).OrderBy(gr => WeaponRank(gr.Key)).ThenBy(gr => gr.Key).ToList();
        x = 0;
        foreach (var (key, label) in new[] { ("", "All") }.Concat(weapons.Select(w => (w.Key, $"{(w.Key == "Melee" ? "Knife" : w.Key)} {w.Count()}"))))
        {
            var w = Draw.TextWidth(label, Theme.Small) + D(20);
            if (x + w > width) { x = 0; y += D(30); }
            var chip = new Rectangle(x, y, w, D(24));
            var on = _weaponFilter == key;
            var rf = new RectangleF(chip.X + 0.5f, chip.Y + 0.5f, chip.Width - 1, chip.Height - 1);
            if (on) Theme.FillRound(g, Theme.Raised, rf, rf.Height / 2);
            else if (HoverKey == "weapon:" + key) Theme.FillRound(g, Theme.Hover, rf, rf.Height / 2);
            Theme.StrokeRound(g, on ? ColorTranslator.FromHtml("#3A4250") : Theme.Border, rf, rf.Height / 2);
            TextRenderer.DrawText(g, label, Theme.Small, chip, on ? Theme.Text : Theme.Muted, Theme.CenterMiddle);
            Hit(chip, "weapon:" + key);
            x = chip.Right + D(6);
        }

        y += D(36);

        var gap = D(8);
        var cols = width >= D(480) ? 4 : 3;
        var cellW = (width - gap * (cols - 1)) / cols;
        var cellH = D(84);
        foreach (var group in weapons.Where(w => _weaponFilter.Length == 0 || w.Key == _weaponFilter))
        {
            y = Section(g, y, width, (group.Key == "Melee" ? "KNIFE" : group.Key.ToUpperInvariant()), group.Count().ToString());
            var skins = group.OrderByDescending(s => s.RarityRank).ThenBy(s => s.Name).ToList();
            for (var i = 0; i < skins.Count; i++)
            {
                var rect = new Rectangle(i % cols * (cellW + gap), y + i / cols * (cellH + gap), cellW, cellH);
                // Only draw (and download pictures for) cards that are on screen.
                if (g.IsVisible(rect))
                {
                    var skin = skins[i];
                    Card(g, rect, D(8), skin.Rarity);
                    Draw.Fit(g, ImageCache.Get(skin.Icon), new RectangleF(rect.X + D(8), rect.Y + D(10), rect.Width - D(16), D(38)));
                    TextRenderer.DrawText(g, skin.Name, Theme.Small, new Rectangle(rect.X + D(8), rect.Bottom - D(28), rect.Width - D(16), D(18)), Theme.Text, Theme.LeftMiddle);
                }
            }

            var rows = (skins.Count + cols - 1) / cols;
            y += rows * cellH + Math.Max(0, rows - 1) * gap + D(18);
        }

        return y;
    }

    // ---- Battle pass

    private string RewardName(ValorantContent.PassLevel level, out string icon)
    {
        icon = "";
        var catalog = _catalog;
        if (catalog is null) return level.RewardType;
        switch (level.RewardType)
        {
            case "EquippableSkinLevel" when catalog.Skins.TryGetValue(level.RewardId, out var skin):
                icon = skin.Icon;
                return skin.Name;
            case "Currency":
                icon = "https://media.valorant-api.com/currencies/e59aa87c-4cbf-517a-5983-6e81511be9b7/displayicon.png";
                return level.Amount > 1 ? $"{level.Amount} Radianite" : "Radianite";
            case "EquippableCharmLevel":
            case "PlayerCard":
            case "Spray":
            case "Title":
                if (catalog.Items.TryGetValue(level.RewardId, out var item))
                {
                    icon = item.Icon;
                    return item.Kind == "Title" ? $"\"{item.Name}\" title" : item.Name;
                }

                break;
        }

        return level.RewardType switch
        {
            "EquippableCharmLevel" => "Gun buddy",
            "PlayerCard" => "Player card",
            "Spray" => "Spray",
            "Title" => "Title",
            "EquippableSkinLevel" => "Weapon skin",
            _ => "Reward"
        };
    }

    private int PaintPass(Graphics g, int y, int width)
    {
        var catalog = _catalog;
        var contracts = _contracts;
        if (catalog is null || contracts is null)
            return PaintMessage(g, width, "Battle pass unavailable", _loading ? "Loading…" : "Couldn't load your progress.", "Try again");

        var pass = ProgressData.CurrentPass(catalog, contracts, out _);
        if (pass is null || !contracts.Progress.TryGetValue(pass.Id, out var progress))
            return PaintMessage(g, width, "This act's battle pass isn't listed yet",
                "The skin and reward data comes from valorant-api.com, which usually adds a new battle pass within a day or two of an act starting.", "Try again");

        var levels = pass.Levels;
        var reached = Math.Max(0, Math.Min(levels.Count, progress.level));
        var done = reached >= levels.Count;
        var nextXp = done ? 0 : levels[reached].Xp;
        var remaining = done ? 0 : levels.Skip(reached).Sum(l => l.Xp) - progress.towardsNext;

        // Header card
        var rect = new Rectangle(0, y, width, D(118));
        Card(g, rect, D(12));
        var pad = D(16);
        TextRenderer.DrawText(g, "BATTLE PASS", Theme.Section, new Rectangle(rect.X + pad, rect.Y + D(14), rect.Width - pad * 2, D(16)), Theme.Faint, Theme.LeftMiddle);
        var actEnd = catalog.Seasons.TryGetValue(pass.ActId, out var act) ? act.End : DateTime.MinValue;
        if (actEnd > DateTime.UtcNow)
            TextRenderer.DrawText(g, $"Act ends in {Draw.Countdown(actEnd)}", Theme.Small, new Rectangle(rect.X + pad, rect.Y + D(14), rect.Width - pad * 2, D(16)), Theme.Muted, Theme.RightMiddle);
        TextRenderer.DrawText(g, done ? $"Tier {levels.Count} · complete" : $"Tier {reached} of {levels.Count}", Draw.Big,
            new Rectangle(rect.X + pad, rect.Y + D(36), rect.Width - pad * 2, D(26)), Theme.Text, Theme.LeftMiddle);
        if (!done)
            TextRenderer.DrawText(g, $"{Draw.Number(progress.towardsNext)} / {Draw.Number(nextXp)} XP", Theme.Small,
                new Rectangle(rect.X + pad, rect.Y + D(36), rect.Width - pad * 2, D(26)), Theme.Muted, Theme.RightMiddle);

        var bar = new RectangleF(rect.X + pad, rect.Y + D(68), rect.Width - pad * 2, D(8));
        Theme.FillRound(g, Theme.Raised, bar, D(4));
        var fraction = done ? 1f : nextXp > 0 ? Math.Min(1f, (float)progress.towardsNext / nextXp) : 0f;
        if (fraction > 0) Theme.FillRound(g, Theme.Accent, new RectangleF(bar.X, bar.Y, Math.Max(bar.Height, bar.Width * fraction), bar.Height), D(4));

        var info = done ? "You've finished this battle pass." : $"{Draw.Number(Math.Max(0, remaining))} XP to finish";
        if (!done && actEnd > DateTime.UtcNow)
        {
            var weeks = Math.Max(1, (actEnd - DateTime.UtcNow).TotalDays / 7);
            info += $" · about {Draw.Number((int)Math.Ceiling(Math.Max(0, remaining) / weeks))} XP a week";
        }

        TextRenderer.DrawText(g, info, Theme.Small, new Rectangle(rect.X + pad, rect.Y + D(84), rect.Width - pad * 2, D(18)), Theme.Muted, Theme.LeftMiddle);
        y = rect.Bottom + D(20);

        if (done) return y;

        // Upcoming rewards
        y = Section(g, y, width, "COMING UP");
        foreach (var (level, index) in levels.Select((l, i) => (l, i)).Skip(reached).Take(8))
        {
            var row = new Rectangle(0, y, width, D(52));
            Card(g, row, D(9));
            var name = RewardName(level, out var icon);
            var iconBox = new RectangleF(row.X + D(10), row.Y + D(8), D(64), row.Height - D(16));
            Theme.FillRound(g, Theme.Raised, iconBox, D(6));
            if (icon.Length > 0) Draw.Fit(g, ImageCache.Get(icon), RectangleF.Inflate(iconBox, -D(3), -D(3)));
            var left = (int)iconBox.Right + D(12);
            TextRenderer.DrawText(g, name, Theme.BodyBold, new Rectangle(left, row.Y + D(8), row.Right - D(120) - left, D(20)), Theme.Text, Theme.LeftMiddle);
            TextRenderer.DrawText(g, $"Tier {index + 1}" + (level.Highlighted ? " · featured" : ""), Theme.Small,
                new Rectangle(left, row.Y + D(28), row.Right - D(120) - left, D(18)), Theme.Muted, Theme.LeftMiddle);
            var xpText = index == reached ? $"{Draw.Number(Math.Max(0, level.Xp - progress.towardsNext))} XP to go" : $"{Draw.Number(level.Xp)} XP";
            TextRenderer.DrawText(g, xpText, Theme.Small, new Rectangle(row.Right - D(130), row.Y, D(118), row.Height), Theme.Muted, Theme.RightMiddle);
            y = row.Bottom + D(6);
        }

        return y + D(14);
    }

    // ---- Missions

    private int PaintMissions(Graphics g, int y, int width)
    {
        var catalog = _catalog;
        var contracts = _contracts;
        if (catalog is null || contracts is null)
            return PaintMessage(g, width, "Missions unavailable", _loading ? "Loading…" : "Couldn't load your missions.", "Try again");

        var now = DateTime.UtcNow;
        var missions = contracts.Missions
            .Where(m => m.ExpiresUtc == DateTime.MinValue || m.ExpiresUtc > now)
            .Select(m => (m, info: catalog.Missions.TryGetValue(m.Id, out var i) ? i : null))
            .Where(x => x.info is not null && x.info.Type is not ("NPE" or "Tutorial"))
            .OrderBy(x => x.m.Complete)
            .ThenBy(x => x.info!.Type)
            .ToList();

        if (missions.Count == 0)
            return PaintMessage(g, width, "No missions right now", "New weekly missions arrive every week.", null);

        var right = contracts.WeeklyRefillUtc > now ? "New weeklies in " + Draw.Countdown(contracts.WeeklyRefillUtc) : "";
        y = Section(g, y, width, "YOUR MISSIONS", right);

        foreach (var (m, info) in missions)
        {
            var row = new Rectangle(0, y, width, D(74));
            Card(g, row, D(10));
            var pad = D(14);
            var goal = Math.Max(1, info!.ProgressToComplete);
            var progress = m.Complete ? goal : Math.Min(goal, m.Progress);
            TextRenderer.DrawText(g, info.Title, Theme.BodyBold, new Rectangle(row.X + pad, row.Y + D(10), row.Width - pad * 2 - D(110), D(20)),
                m.Complete ? Theme.Muted : Theme.Text, Theme.LeftMiddle);
            var xp = info.Xp > 0 ? $"{Draw.Number(info.Xp)} XP" : "";
            TextRenderer.DrawText(g, m.Complete ? "Done" : xp, Theme.Small, new Rectangle(row.Right - pad - D(110), row.Y + D(10), D(110), D(20)),
                m.Complete ? Theme.Online : Theme.Muted, Theme.RightMiddle);

            var bar = new RectangleF(row.X + pad, row.Y + D(36), row.Width - pad * 2, D(8));
            Theme.FillRound(g, Theme.Raised, bar, D(4));
            var fraction = (float)progress / goal;
            if (fraction > 0) Theme.FillRound(g, m.Complete ? Theme.Online : Theme.Accent, new RectangleF(bar.X, bar.Y, Math.Max(bar.Height, bar.Width * fraction), bar.Height), D(4));
            var under = new Rectangle(row.X + pad, row.Y + D(48), row.Width - pad * 2, D(18));
            TextRenderer.DrawText(g, $"{Draw.Number(progress)} / {Draw.Number(goal)}", Theme.Small, under, Theme.Muted, Theme.LeftMiddle);
            if (info.Type.Length > 0)
                TextRenderer.DrawText(g, info.Type, Theme.Small, under, Theme.Faint, Theme.RightMiddle);
            y = row.Bottom + D(6);
        }

        return y + D(14);
    }
}
