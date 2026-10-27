using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Deceive;

/// <summary>
/// Store tab: today's skins and the Night Market, featured bundles and the Kingdom Credits store,
/// your wishlist, and the history of stores the app has seen.
/// </summary>
internal sealed class StorePanel : PagePanel
{
    private const string VpIcon = "https://media.valorant-api.com/currencies/85ad13f7-3d1b-5128-9eb2-7cd8ee0b5741/displayicon.png";
    private const string RpIcon = "https://media.valorant-api.com/currencies/e59aa87c-4cbf-517a-5983-6e81511be9b7/displayicon.png";
    private const string KcIcon = "https://media.valorant-api.com/currencies/85ca954a-41f2-ce94-9b45-8ca3dd39a00d/displayicon.png";

    private static readonly string[] Views = { "Today", "Bundles", "Wishlist", "History" };
    private const int TodayView = 0, BundlesView = 1, WishlistView = 2, HistoryView = 3;
    private int _view;

    private ValorantApi.Store? _store;
    private ValorantContent.Catalog? _catalog;
    private List<StoreHistory.Day> _history = new();
    private string _error = "";
    private bool _loading;
    private DateTime _loadedAt = DateTime.MinValue;
    private DateTime _attemptedAt = DateTime.MinValue;
    private bool _active;
    private readonly Timer _clock = new() { Interval = 1000 };
    private readonly SearchBox _search;

    public StorePanel(float scale) : base(scale)
    {
        _search = new SearchBox(scale, "Search skins to add, e.g. \"Prime Vandal\"") { Visible = false };
        _search.TextChanged += (_, _) => { ScrollToTop(); Invalidate(); };
        Controls.Add(_search);

        _clock.Tick += (_, _) =>
        {
            if (!_active) return;
            // The daily store rolled over while you were looking: fetch the new one (once).
            if (_store is not null && !_loading && DateTime.UtcNow > _store.DailyEndsUtc.AddSeconds(5) && DateTime.UtcNow - _attemptedAt > TimeSpan.FromSeconds(30))
                _ = LoadAsync();
            if (_view is TodayView or BundlesView) Invalidate();
        };
        ImageCache.Loaded += OnImageLoaded;
    }

    /// <summary>Called when the tab is shown or hidden (and when the window minimizes).</summary>
    public void SetActive(bool active)
    {
        _active = active;
        _clock.Enabled = active;
        if (active && !_loading && (_store is null || DateTime.UtcNow - _loadedAt > TimeSpan.FromMinutes(10)))
            _ = LoadAsync();
        if (active && _catalog is null)
            _ = LoadCatalogOnlyAsync();
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
            _clock.Dispose();
        }

        base.Dispose(disposing);
    }

    // ------------------------------------------------------------------ loading

    private async Task LoadCatalogOnlyAsync()
    {
        try
        {
            _catalog ??= await ValorantContent.GetAsync();
            if (!IsDisposed) Invalidate();
        }
        catch (Exception e)
        {
            System.Diagnostics.Trace.WriteLine("Catalog failed: " + e.Message);
        }
    }

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        _attemptedAt = DateTime.UtcNow;
        _error = "";
        Invalidate();
        try
        {
            var store = await ValorantApi.GetStoreAsync();
            var ids = store.Daily.Select(o => o.ItemId)
                .Concat(store.NightMarket?.Select(o => o.ItemId) ?? Enumerable.Empty<string>());
            ValorantContent.Catalog? catalog = null;
            try { catalog = await ValorantContent.GetAsync(ids); }
            catch (Exception e) { System.Diagnostics.Trace.WriteLine("Catalog failed: " + e.Message); }

            _store = store;
            _catalog = catalog ?? ValorantContent.Loaded;
            _loadedAt = DateTime.UtcNow;
            if (_catalog is not null)
            {
                var c = _catalog;
                await Task.Run(() => StoreWatcher.Observe(store, c, notify: false));
            }

            _history = await Task.Run(() => StoreHistory.Load(store.Puuid));
        }
        catch (ValorantApiException e)
        {
            _error = e.Message;
        }
        catch (Exception e)
        {
            System.Diagnostics.Trace.WriteLine("Store failed: " + e);
            _error = "Something went wrong loading your store. Try again in a moment.";
        }
        finally
        {
            _loading = false;
            if (!IsDisposed) Invalidate();
        }
    }

    // ------------------------------------------------------------------ wishlist

    private static bool OnWishlist(string skinId) =>
        AppSettings.Current.Wishlist.Contains(skinId.ToLowerInvariant());

    private void ToggleWishlist(string skinId)
    {
        skinId = skinId.ToLowerInvariant();
        var settings = AppSettings.Current.Clone();
        if (!settings.Wishlist.Remove(skinId))
            settings.Wishlist.Add(skinId);
        AppSettings.Save(settings);
        Invalidate();
    }

    private string SkinIdFor(string itemId) =>
        _catalog is null ? itemId.ToLowerInvariant() : StoreHistory.SkinId(itemId, _catalog);

    // ------------------------------------------------------------------ input

    protected override void OnHitClicked(string key)
    {
        if (key.StartsWith("sub:", StringComparison.Ordinal) && int.TryParse(key.Substring(4), out var view))
        {
            if (view == _view) return;
            _view = view;
            _search.Visible = view == WishlistView;
            LayoutSearch();
            if (view == WishlistView) _search.FocusInput();
            ScrollToTop();
            Invalidate();
            return;
        }

        if (key.StartsWith("star:", StringComparison.Ordinal))
        {
            ToggleWishlist(key.Substring(5));
            return;
        }

        if (key is "action" or "refresh")
            _ = LoadAsync();
    }

    protected override int HeaderHeight => _view == WishlistView ? D(92) : D(46);

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutSearch();
    }

    private void LayoutSearch()
    {
        if (_search is null) return;
        _search.Bounds = new Rectangle(0, D(42), Math.Max(0, Width - D(2)), D(36));
    }

    protected override void PaintHeader(Graphics g, int width)
    {
        var labels = Views.ToArray();
        var wished = AppSettings.Current.Wishlist.Count;
        if (wished > 0) labels[WishlistView] = $"Wishlist ({wished})";
        PaintSubTabs(g, 0, labels, _view);

        if (_view is TodayView or BundlesView && _store is not null)
        {
            var text = _loading ? "Refreshing…" : "Refresh";
            var rw = Draw.TextWidth("Refreshing…", Theme.Small) + D(28);
            var refresh = new Rectangle(width - rw - D(2), 0, rw, D(28));
            Draw.Button(g, refresh, text, HoverKey == "refresh", !_loading, S);
            if (!_loading) Hit(refresh, "refresh");
        }
    }

    // ------------------------------------------------------------------ painting

    protected override int PaintContent(Graphics g, int width)
    {
        switch (_view)
        {
            case WishlistView:
                return PaintWishlist(g, width);
            case HistoryView:
                return PaintHistory(g, width);
        }

        if (_store is null)
        {
            if (_loading)
                return PaintMessage(g, width, "Loading your store…", "This takes a second the first time.", null);
            return PaintMessage(g, width, "Couldn't load your store", _error.Length > 0 ? _error : "Open this tab while the Riot Client is running.", "Try again");
        }

        return _view == BundlesView ? PaintBundles(g, width) : PaintToday(g, width);
    }

    private int PaintErrorLine(Graphics g, int y, int width)
    {
        if (_error.Length == 0) return y;
        TextRenderer.DrawText(g, _error, Theme.Small, new Rectangle(0, y, width, D(18)), Theme.Warn, Theme.LeftMiddle);
        return y + D(24);
    }

    private int PaintToday(Graphics g, int width)
    {
        var store = _store!;
        var y = PaintErrorLine(g, 0, width);

        // Wallet
        var bar = new Rectangle(0, y, width, D(26));
        var x = PaintBalance(g, 0, bar, VpIcon, store.Vp);
        x = PaintBalance(g, x, bar, RpIcon, store.Radianite);
        PaintBalance(g, x, bar, KcIcon, store.KingdomCredits);
        y += D(26) + D(14);

        y = PaintSectionHeader(g, y, width, "DAILY STORE", "Resets in " + Draw.Countdown(store.DailyEndsUtc));
        y = PaintGrid(g, y, width, store.Daily, nightMarket: false);
        y += D(22);

        if (store.NightMarket is { Count: > 0 } night)
        {
            y = PaintSectionHeader(g, y, width, "NIGHT MARKET", "Ends in " + Draw.Countdown(store.NightMarketEndsUtc));
            y = PaintGrid(g, y, width, night, nightMarket: true);
        }
        else
        {
            y = PaintSectionHeader(g, y, width, "NIGHT MARKET", "");
            y = PaintNote(g, y, width, "The Night Market isn't open right now.");
        }

        return y + D(16);
    }

    private int PaintNote(Graphics g, int y, int width, string text, int height = 64)
    {
        var box = new Rectangle(0, y, width, D(height));
        Theme.FillRound(g, Theme.Surface, new RectangleF(box.X + 0.5f, box.Y + 0.5f, box.Width - 1, box.Height - 1), D(10));
        TextRenderer.DrawText(g, text, Theme.Body, Rectangle.Inflate(box, -D(16), 0), Theme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        return box.Bottom;
    }

    private int PaintBalance(Graphics g, int x, Rectangle bar, string icon, int? amount)
    {
        if (amount is null) return x;
        var size = D(18);
        Draw.Fit(g, ImageCache.Get(icon), new RectangleF(x, bar.Y + (bar.Height - size) / 2f, size, size));
        x += size + D(6);
        var text = Draw.Number(amount.Value);
        var w = Draw.TextWidth(text, Theme.BodyBold);
        TextRenderer.DrawText(g, text, Theme.BodyBold, new Rectangle(x, bar.Y, w + D(2), bar.Height), Theme.Text, Theme.LeftMiddle);
        return x + w + D(18);
    }

    private int PaintSectionHeader(Graphics g, int y, int width, string title, string right)
    {
        var rect = new Rectangle(0, y, width, D(16));
        TextRenderer.DrawText(g, title, Theme.Section, rect, Theme.Faint, Theme.LeftMiddle);
        if (right.Length > 0)
            TextRenderer.DrawText(g, right, Theme.Small, rect, Theme.Muted, Theme.RightMiddle);
        return y + D(24);
    }

    private int PaintGrid(Graphics g, int y, int width, List<ValorantApi.StoreOffer> offers, bool nightMarket)
    {
        if (offers.Count == 0)
            return PaintNote(g, y, width, "Nothing here right now.");

        var columns = nightMarket && width >= D(560) ? 3 : 2;
        var gap = D(10);
        var cardW = (width - gap * (columns - 1)) / columns;
        var cardH = D(138);

        for (var i = 0; i < offers.Count; i++)
        {
            var rect = new Rectangle(i % columns * (cardW + gap), y + i / columns * (cardH + gap), cardW, cardH);
            PaintCard(g, rect, offers[i], nightMarket);
        }

        var rows = (offers.Count + columns - 1) / columns;
        return y + rows * cardH + (rows - 1) * gap;
    }

    private static void CardBackground(Graphics g, Rectangle rect, Color rarity, int radius, bool highlight)
    {
        var rf = new RectangleF(rect.X + 0.5f, rect.Y + 0.5f, rect.Width - 1, rect.Height - 1);
        Theme.FillRound(g, Theme.Surface, rf, radius);
        if (rarity != Color.Empty)
        {
            using var path = Theme.Round(rf, radius);
            using var brush = new LinearGradientBrush(rect, Color.FromArgb(46, rarity), Color.FromArgb(0, rarity), LinearGradientMode.Vertical);
            g.FillPath(brush, path);
        }

        var border = highlight ? ColorTranslator.FromHtml("#F2C14E") : rarity != Color.Empty ? Color.FromArgb(90, rarity) : Theme.Border;
        Theme.StrokeRound(g, border, rf, radius, highlight ? 1.6f : 1f);
    }

    private void PaintCard(Graphics g, Rectangle rect, ValorantApi.StoreOffer offer, bool nightMarket)
    {
        ValorantContent.Skin? skin = null;
        _catalog?.Skins.TryGetValue(offer.ItemId, out skin);
        var skinId = SkinIdFor(offer.ItemId);
        var wished = OnWishlist(skinId);

        CardBackground(g, rect, skin?.Rarity ?? Color.Empty, D(10), wished);

        var pad = D(12);
        var imageBox = new RectangleF(rect.X + pad, rect.Y + D(30), rect.Width - pad * 2, D(54));
        var image = skin is null ? null : ImageCache.Get(skin.Icon);
        if (image is not null)
            Draw.Fit(g, image, imageBox);
        else
            TextRenderer.DrawText(g, skin is null ? "" : "Loading…", Theme.Small, Rectangle.Round(imageBox), Theme.Faint, Theme.CenterMiddle);

        if (skin?.RarityIcon is { Length: > 0 } rarityIcon)
            Draw.Fit(g, ImageCache.Get(rarityIcon), new RectangleF(rect.X + D(10), rect.Y + D(10), D(16), D(16)));

        // Star (top right), with the Night Market discount badge to its left.
        var star = new Rectangle(rect.Right - D(32), rect.Y + D(6), D(26), D(26));
        if (skin is not null)
        {
            PaintStar(g, Rectangle.Inflate(star, -D(5), -D(5)), wished, HoverKey == "star:" + skinId);
            Hit(star, "star:" + skinId);
        }

        if (nightMarket && offer.DiscountPercent > 0)
        {
            var badgeText = $"-{offer.DiscountPercent}%";
            var bw = Draw.TextWidth(badgeText, Theme.Section) + D(14);
            var badge = new Rectangle(star.Left - D(4) - bw, rect.Y + D(9), bw, D(20));
            Theme.FillRound(g, Theme.Accent, badge, D(10));
            TextRenderer.DrawText(g, badgeText, Theme.Section, badge, Color.White, Theme.CenterMiddle);
        }

        var name = skin?.Name is { Length: > 0 } n ? n : "Unknown item";
        TextRenderer.DrawText(g, name, Theme.BodyBold, new Rectangle(rect.X + pad, rect.Bottom - D(52), rect.Width - pad * 2, D(20)), Theme.Text, Theme.LeftMiddle);

        var priceRow = new Rectangle(rect.X + pad, rect.Bottom - D(30), rect.Width - pad * 2, D(20));
        PaintPrice(g, priceRow, VpIcon, offer.Cost, nightMarket ? offer.BaseCost : 0);
    }

    private void PaintPrice(Graphics g, Rectangle row, string icon, int cost, int was)
    {
        if (cost <= 0)
        {
            TextRenderer.DrawText(g, "Price unavailable", Theme.Small, row, Theme.Faint, Theme.LeftMiddle);
            return;
        }

        var size = D(15);
        var x = row.X;
        Draw.Fit(g, ImageCache.Get(icon), new RectangleF(x, row.Y + (row.Height - size) / 2f, size, size));
        x += size + D(6);
        var price = Draw.Number(cost);
        var w = Draw.TextWidth(price, Draw.Price);
        TextRenderer.DrawText(g, price, Draw.Price, new Rectangle(x, row.Y, w + D(2), row.Height), Theme.Text, Theme.LeftMiddle);
        x += w + D(10);

        if (was > cost)
        {
            var text = Draw.Number(was);
            var ww = Draw.TextWidth(text, Theme.Small);
            TextRenderer.DrawText(g, text, Theme.Small, new Rectangle(x, row.Y, ww + D(2), row.Height), Theme.Faint, Theme.LeftMiddle);
            using var pen = new Pen(Theme.Faint, Math.Max(1f, S));
            var mid = row.Y + row.Height / 2f + D(1);
            g.DrawLine(pen, x - D(1), mid, x + ww + D(1), mid);
        }
    }

    // ---- Bundles + Kingdom Credits store

    private int PaintBundles(Graphics g, int width)
    {
        var store = _store!;
        var y = PaintErrorLine(g, 0, width);

        y = PaintSectionHeader(g, y, width, "FEATURED BUNDLES", "");
        if (store.Bundles.Count == 0)
            y = PaintNote(g, y, width, "No featured bundles right now.") + D(10);
        foreach (var bundle in store.Bundles)
        {
            ValorantContent.Item? info = null;
            _catalog?.Bundles.TryGetValue(bundle.BundleId, out info);
            var rect = new Rectangle(0, y, width, D(150));
            CardBackground(g, rect, Color.Empty, D(10), false);

            // Picture on the left, details on the right.
            var picture = new RectangleF(rect.X + 1, rect.Y + 1, rect.Width * 0.58f, rect.Height - 2);
            var image = info is null ? null : ImageCache.Get(info.Icon);
            if (image is not null)
            {
                using var clip = Theme.Round(picture, D(9));
                var state = g.Save();
                g.SetClip(clip, CombineMode.Intersect);
                Cover(g, image, picture);
                g.Restore(state);
            }

            var left = (int)picture.Right + D(16);
            var textW = Math.Max(0, rect.Right - D(14) - left);
            TextRenderer.DrawText(g, info?.Name is { Length: > 0 } n ? n : "Bundle", Draw.Big,
                new Rectangle(left, rect.Y + D(16), textW, D(24)), Theme.Text, Theme.LeftMiddle);
            TextRenderer.DrawText(g, $"{bundle.ItemCount} items · ends in {Draw.Countdown(bundle.EndsUtc)}", Theme.Small,
                new Rectangle(left, rect.Y + D(42), textW, D(18)), Theme.Muted, Theme.LeftMiddle);
            if (bundle.BaseCost > bundle.Cost && bundle.Cost > 0)
                TextRenderer.DrawText(g, $"Saves {Draw.Number(bundle.BaseCost - bundle.Cost)} VP", Theme.Small,
                    new Rectangle(left, rect.Bottom - D(62), textW, D(18)), Theme.Online, Theme.LeftMiddle);
            PaintPrice(g, new Rectangle(left, rect.Bottom - D(38), textW, D(22)), VpIcon, bundle.Cost, bundle.BaseCost);
            y = rect.Bottom + D(10);
        }

        y += D(14);
        y = PaintSectionHeader(g, y, width, "KINGDOM CREDITS STORE",
            store.Accessories.Count > 0 ? "Resets in " + Draw.Countdown(store.AccessoriesEndsUtc) : "");
        if (store.Accessories.Count == 0)
            return PaintNote(g, y, width, "Nothing in the Kingdom Credits store right now.") + D(16);

        var gap = D(10);
        var cardW = (width - gap) / 2;
        var cardH = D(112);
        for (var i = 0; i < store.Accessories.Count; i++)
        {
            var offer = store.Accessories[i];
            var rect = new Rectangle(i % 2 * (cardW + gap), y + i / 2 * (cardH + gap), cardW, cardH);
            CardBackground(g, rect, Color.Empty, D(10), false);

            ValorantContent.Item? item = null;
            _catalog?.Items.TryGetValue(offer.ItemId, out item);
            var kind = item?.Kind ?? KindName(offer.ItemTypeId);
            var iconBox = new RectangleF(rect.X + D(12), rect.Y + D(12), D(52), D(52));
            Theme.FillRound(g, Theme.Raised, iconBox, D(8));
            if (item is not null && item.Icon.Length > 0)
                Draw.Fit(g, ImageCache.Get(item.Icon), RectangleF.Inflate(iconBox, -D(4), -D(4)));
            else
                TextRenderer.DrawText(g, kind == "Title" ? "Aa" : "?", Theme.BodyBold, Rectangle.Round(iconBox), Theme.Muted, Theme.CenterMiddle);

            var left = (int)iconBox.Right + D(12);
            var textW = Math.Max(0, rect.Right - D(12) - left);
            TextRenderer.DrawText(g, item?.Name is { Length: > 0 } n ? n : "Unknown item", Theme.BodyBold,
                new Rectangle(left, rect.Y + D(14), textW, D(20)), Theme.Text, Theme.LeftMiddle);
            TextRenderer.DrawText(g, kind, Theme.Small, new Rectangle(left, rect.Y + D(36), textW, D(18)), Theme.Muted, Theme.LeftMiddle);
            PaintPrice(g, new Rectangle(rect.X + D(12), rect.Bottom - D(32), rect.Width - D(24), D(20)), KcIcon, offer.Cost, 0);
        }

        var rows = (store.Accessories.Count + 1) / 2;
        return y + rows * cardH + (rows - 1) * gap + D(16);
    }

    private static string KindName(string itemTypeId) => itemTypeId.ToLowerInvariant() switch
    {
        ValorantApi.BuddyItemType => "Buddy",
        ValorantApi.SprayItemType => "Spray",
        ValorantApi.CardItemType => "Card",
        ValorantApi.TitleItemType => "Title",
        ValorantApi.FlexItemType => "Flex",
        _ => "Item"
    };

    /// <summary>Fills the box with the image (cropping the edges), like CSS object-fit: cover.</summary>
    private static void Cover(Graphics g, Image image, RectangleF box)
    {
        if (image.Width <= 0 || image.Height <= 0) return;
        var scale = Math.Max(box.Width / image.Width, box.Height / image.Height);
        var w = image.Width * scale;
        var h = image.Height * scale;
        var old = g.InterpolationMode;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(image, box.X + (box.Width - w) / 2f, box.Y + (box.Height - h) / 2f, w, h);
        g.InterpolationMode = old;
    }

    // ---- Wishlist

    private int PaintWishlist(Graphics g, int width)
    {
        var catalog = _catalog;
        if (catalog is null)
            return PaintMessage(g, width, "Loading skins…", "This takes a second the first time.", null);

        var y = 0;
        var query = _search.Text.Trim();
        if (query.Length > 0)
        {
            var words = query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var results = catalog.SkinList
                .Where(s => words.All(w => s.Name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderBy(s => s.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .Take(40)
                .ToList();

            y = PaintSectionHeader(g, y, width, "RESULTS", results.Count == 40 ? "Showing the first 40" : $"{results.Count} found");
            if (results.Count == 0)
                return PaintNote(g, y, width, $"No skins match \"{query}\".") + D(16);
            foreach (var skin in results)
                y = PaintSkinRow(g, y, width, skin, StatusFor(skin.Id)) + D(6);
            return y + D(10);
        }

        var wishlist = AppSettings.Current.Wishlist
            .Select(id => catalog.Skins.TryGetValue(id, out var s) ? s : null)
            .Where(s => s is not null)
            .Select(s => s!)
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        y = PaintSectionHeader(g, y, width, "YOUR WISHLIST", wishlist.Count > 0 ? $"{wishlist.Count} skins" : "");
        if (wishlist.Count == 0)
            return PaintNote(g, y, width,
                "Search above, or tap the star on a store card. You'll get a Windows notification on days a wishlist skin is in your store.", 84) + D(16);

        foreach (var skin in wishlist)
            y = PaintSkinRow(g, y, width, skin, StatusFor(skin.Id)) + D(6);

        y += D(8);
        TextRenderer.DrawText(g, "The app checks your store once a day while you have skins here, and notifies you when one shows up.",
            Theme.Small, new Rectangle(0, y, width, D(34)), Theme.Faint,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        return y + D(40);
    }

    /// <summary>Whether a skin is in the store now, or when it was last seen.</summary>
    private (string text, Color color) StatusFor(string skinId)
    {
        var id = skinId.ToLowerInvariant();
        if (_store is not null && _catalog is not null)
        {
            if (_store.Daily.Any(o => SkinIdFor(o.ItemId) == id))
                return ("In your store now", Theme.Online);
            if (_store.NightMarket?.Any(o => SkinIdFor(o.ItemId) == id) == true)
                return ("In your Night Market now", Theme.Online);
        }

        var seen = _history.FirstOrDefault(d => d.Daily.Contains(id) || d.Night.Contains(id));
        if (seen is not null)
            return ("Last seen " + StoreDay(seen.EndsUtc).ToString("MMM d"), Theme.Muted);
        return ("", Theme.Muted);
    }

    private int PaintSkinRow(Graphics g, int y, int width, ValorantContent.Skin skin, (string text, Color color) status)
    {
        var id = skin.Id.ToLowerInvariant();
        var rect = new Rectangle(0, y, width, D(54));
        var wished = OnWishlist(id);
        var hover = HoverKey == "star:" + id;
        CardBackground(g, rect, skin.Rarity, D(9), false);

        var picture = new RectangleF(rect.X + D(10), rect.Y + D(7), D(96), rect.Height - D(14));
        Draw.Fit(g, ImageCache.Get(skin.Icon), picture);

        var left = (int)picture.Right + D(14);
        var star = new Rectangle(rect.Right - D(40), rect.Y + (rect.Height - D(30)) / 2, D(30), D(30));
        var statusW = status.text.Length > 0 ? Draw.TextWidth(status.text, Theme.Small) + D(4) : 0;
        var textRight = star.Left - D(10) - statusW;
        TextRenderer.DrawText(g, skin.Name, Theme.BodyBold, new Rectangle(left, rect.Y + D(8), Math.Max(0, textRight - left), D(20)), Theme.Text, Theme.LeftMiddle);
        TextRenderer.DrawText(g, skin.Weapon, Theme.Small, new Rectangle(left, rect.Y + D(28), Math.Max(0, textRight - left), D(18)), Theme.Muted, Theme.LeftMiddle);
        if (status.text.Length > 0)
            TextRenderer.DrawText(g, status.text, Theme.Small, new Rectangle(star.Left - D(10) - statusW, rect.Y, statusW, rect.Height), status.color, Theme.RightMiddle);

        PaintStar(g, Rectangle.Inflate(star, -D(6), -D(6)), wished, hover);
        // The whole row toggles, which is an easier target than the star alone.
        Hit(rect, "star:" + id);
        return rect.Bottom;
    }

    // ---- History

    /// <summary>The store day an entry belongs to: stores reset at the same moment worldwide, so
    /// a store is named after the UTC day it was on sale, which matches the in-game store.</summary>
    private static DateTime StoreDay(DateTime endsUtc) => endsUtc.AddHours(-12).Date;

    private int PaintHistory(Graphics g, int width)
    {
        var catalog = _catalog;
        if (_history.Count == 0)
        {
            if (_store is null && _loading)
                return PaintMessage(g, width, "Loading…", "", null);
            return PaintMessage(g, width, "No store history yet",
                "Every daily store the app sees is saved here, so this fills up as you use it.", null);
        }

        var y = 0;
        var first = StoreDay(_history.Last().EndsUtc);
        TextRenderer.DrawText(g, $"{_history.Count} {(_history.Count == 1 ? "day" : "days")} recorded since {first:MMM d, yyyy}",
            Theme.Small, new Rectangle(0, y, width, D(18)), Theme.Muted, Theme.LeftMiddle);
        y += D(28);

        var gap = D(8);
        var cellW = (width - gap * 3) / 4;
        var cellH = D(74);
        foreach (var day in _history)
        {
            var date = StoreDay(day.EndsUtc);
            var today = StoreDay(_store?.DailyEndsUtc ?? DateTime.UtcNow.Date.AddDays(1));
            var label = date == today ? "Today" : date == today.AddDays(-1) ? "Yesterday" : date.ToString("ddd, MMM d");
            var right = day.Night.Count > 0 ? "Night Market open" : "";
            y = PaintSectionHeader(g, y, width, label.ToUpperInvariant(), right);
            for (var i = 0; i < day.Daily.Count; i++)
            {
                var rect = new Rectangle(i % 4 * (cellW + gap), y + i / 4 * (cellH + gap), cellW, cellH);
                ValorantContent.Skin? skin = null;
                catalog?.Skins.TryGetValue(day.Daily[i], out skin);
                CardBackground(g, rect, skin?.Rarity ?? Color.Empty, D(8), OnWishlist(day.Daily[i]));
                if (skin is not null)
                    Draw.Fit(g, ImageCache.Get(skin.Icon), new RectangleF(rect.X + D(8), rect.Y + D(8), rect.Width - D(16), D(34)));
                TextRenderer.DrawText(g, skin?.Name ?? "Unknown", Theme.Small, new Rectangle(rect.X + D(8), rect.Bottom - D(26), rect.Width - D(16), D(18)), Theme.Text, Theme.LeftMiddle);
            }

            var rows = (day.Daily.Count + 3) / 4;
            y += rows * cellH + Math.Max(0, rows - 1) * gap + D(18);
        }

        return y;
    }
}
