using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Deceive;

/// <summary>Shared drawing bits for the Store and Agent select tabs.</summary>
internal static class Draw
{
    public static readonly Font Price = new("Segoe UI Semibold", 10.5f);
    public static readonly Font Big = new("Segoe UI Semibold", 11f);

    /// <summary>Draws an image scaled to fit inside the box, centered, keeping its shape.</summary>
    public static void Fit(Graphics g, Image? image, RectangleF box)
    {
        if (image is null || image.Width <= 0 || image.Height <= 0 || box.Width <= 0 || box.Height <= 0) return;
        var scale = Math.Min(box.Width / image.Width, box.Height / image.Height);
        var w = image.Width * scale;
        var h = image.Height * scale;
        var old = g.InterpolationMode;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(image, box.X + (box.Width - w) / 2f, box.Y + (box.Height - h) / 2f, w, h);
        g.InterpolationMode = old;
    }

    public static string Countdown(DateTime endsUtc)
    {
        var left = endsUtc - DateTime.UtcNow;
        if (left <= TimeSpan.Zero) return "now";
        if (left.TotalDays >= 1) return $"{(int)left.TotalDays}d {left.Hours}h";
        if (left.TotalHours >= 1) return $"{(int)left.TotalHours}h {left.Minutes:00}m";
        return $"{left.Minutes}m {left.Seconds:00}s";
    }

    public static string Number(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

    public static int TextWidth(string text, Font font) =>
        text.Length == 0 ? 0 : TextRenderer.MeasureText(text, font, Size.Empty, Theme.Measure).Width;

    public static void Text(Graphics g, string text, Font font, Rectangle rect, Color color, TextFormatFlags flags) =>
        TextRenderer.DrawText(g, text, font, rect, color, flags);

    /// <summary>A soft rounded button drawn in place; returns its bounds for hit-testing.</summary>
    public static void Button(Graphics g, Rectangle rect, string text, bool hover, bool enabled, float s)
    {
        var rf = new RectangleF(rect.X + 0.5f, rect.Y + 0.5f, rect.Width - 1, rect.Height - 1);
        Theme.FillRound(g, !enabled ? Theme.Surface : hover ? Theme.Raised : Theme.Surface, rf, 7 * s);
        Theme.StrokeRound(g, hover && enabled ? ColorTranslator.FromHtml("#3A4250") : Theme.Border, rf, 7 * s);
        TextRenderer.DrawText(g, text, Theme.Small, rect, enabled ? (hover ? Theme.Text : Theme.Muted) : Theme.Faint, Theme.CenterMiddle);
    }
}

/// <summary>
/// Base for the hand-drawn tab pages. Scrolls with the mouse wheel or a slim dark scrollbar
/// drawn on the right, so it matches the rest of the window.
/// </summary>
internal abstract class PagePanel : Control
{
    protected readonly float S;
    private string _hoverKey = "";
    private readonly List<(Rectangle rect, string key)> _hits = new();

    private int _scroll;
    private int _contentHeight;
    private bool _thumbHover;
    private bool _dragging;
    private int _dragOffset;

    protected PagePanel(float scale)
    {
        S = scale;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        TabStop = false;
    }

    protected int D(float px) => (int)Math.Round(px * S);

    protected string HoverKey => _hoverKey;

    /// <summary>Registers a clickable area during painting (in content coordinates).</summary>
    protected void Hit(Rectangle rect, string key) => _hits.Add((rect, key));

    protected virtual void OnHitClicked(string key) { }

    /// <summary>Paints the content at (0,0) and returns its total height.</summary>
    protected abstract int PaintContent(Graphics g, int width);

    private bool Overflows => _contentHeight > Height;
    private int MaxScroll => Math.Max(0, _contentHeight - Height);
    private int Gutter => Overflows ? D(14) : 0;

    private Rectangle ThumbRect()
    {
        if (!Overflows || Height <= 0) return Rectangle.Empty;
        var trackH = Height;
        var thumbH = Math.Max(D(36), (int)((long)trackH * Height / Math.Max(1, _contentHeight)));
        var y = MaxScroll == 0 ? 0 : (int)((long)(trackH - thumbH) * _scroll / MaxScroll);
        return new Rectangle(Width - D(8), y, D(6), thumbH);
    }

    public void ScrollBy(int pixels) => ScrollTo(_scroll + pixels);

    private void ScrollTo(int value)
    {
        var clamped = Math.Max(0, Math.Min(MaxScroll, value));
        if (clamped == _scroll) return;
        _scroll = clamped;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var bg = new SolidBrush(BackColor))
            g.FillRectangle(bg, ClientRectangle);

        var overflowed = Overflows;
        _scroll = Math.Max(0, Math.Min(MaxScroll, _scroll));

        _hits.Clear();
        var state = g.Save();
        g.TranslateTransform(0, -_scroll);
        var height = PaintContent(g, Math.Max(0, Width - Gutter));
        g.Restore(state);

        var changed = height != _contentHeight;
        _contentHeight = height;
        if (Overflows != overflowed)
        {
            // The scrollbar appeared or went away, which changes the content width; paint again.
            Invalidate();
            return;
        }

        if (changed)
            _scroll = Math.Max(0, Math.Min(MaxScroll, _scroll));

        var thumb = ThumbRect();
        if (!thumb.IsEmpty)
        {
            var color = _dragging ? Theme.Muted : _thumbHover ? Theme.Faint : Theme.Raised;
            Theme.FillRound(g, color, thumb, thumb.Width / 2f);
        }
    }

    private string KeyAt(Point clientPoint)
    {
        var p = new Point(clientPoint.X, clientPoint.Y + _scroll);
        foreach (var (rect, key) in _hits)
            if (rect.Contains(p)) return key;
        return "";
    }

    private bool OverScrollbar(Point p) => Overflows && p.X >= Width - D(14);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_dragging)
        {
            var thumb = ThumbRect();
            var travel = Math.Max(1, Height - thumb.Height);
            ScrollTo((int)((long)(e.Y - _dragOffset) * MaxScroll / travel));
            return;
        }

        var overThumb = OverScrollbar(e.Location);
        if (overThumb != _thumbHover)
        {
            _thumbHover = overThumb;
            Invalidate();
        }

        var key = overThumb ? "" : KeyAt(e.Location);
        Cursor = key.Length > 0 ? Cursors.Hand : Cursors.Default;
        if (key == _hoverKey) return;
        _hoverKey = key;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverKey.Length == 0 && !_thumbHover) return;
        _hoverKey = "";
        _thumbHover = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus(); // so the mouse wheel scrolls this page
        if (e.Button != MouseButtons.Left) return;

        if (OverScrollbar(e.Location))
        {
            var thumb = ThumbRect();
            if (!thumb.Contains(new Point(thumb.X, e.Y)))
            {
                // Clicked the track: jump so the thumb centers on the pointer.
                var travel = Math.Max(1, Height - thumb.Height);
                ScrollTo((int)((long)(e.Y - thumb.Height / 2) * MaxScroll / travel));
                thumb = ThumbRect();
            }

            _dragging = true;
            _dragOffset = e.Y - thumb.Y;
            Capture = true;
            Invalidate();
            return;
        }

        var key = KeyAt(e.Location);
        if (key.Length > 0) OnHitClicked(key);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging) return;
        _dragging = false;
        Capture = false;
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        ScrollBy(-e.Delta * D(60) / 120);
    }

    /// <summary>Draws a centered message with an optional button (key "action").</summary>
    protected int PaintMessage(Graphics g, int width, string title, string detail, string? button, bool busy = false)
    {
        var top = Math.Max(D(40), Height / 2 - D(70));
        var rect = new Rectangle(0, top, width, D(24));
        TextRenderer.DrawText(g, title, Theme.BodyBold, rect, Theme.Text, Theme.CenterMiddle);
        var detailRect = new Rectangle(D(24), rect.Bottom + D(4), Math.Max(0, width - D(48)), D(40));
        TextRenderer.DrawText(g, detail, Theme.Small, detailRect, Theme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        var bottom = detailRect.Bottom;
        if (button is not null)
        {
            var bw = Draw.TextWidth(button, Theme.Small) + D(32);
            var b = new Rectangle((width - bw) / 2, bottom + D(10), bw, D(30));
            Draw.Button(g, b, button, HoverKey == "action", !busy, S);
            if (!busy) Hit(b, "action");
            bottom = b.Bottom;
        }

        return bottom + D(20);
    }
}

/// <summary>Store tab: today's four skins, the Night Market and your wallet.</summary>
internal sealed class StorePanel : PagePanel
{
    private const string VpIcon = "https://media.valorant-api.com/currencies/85ad13f7-3d1b-5128-9eb2-7cd8ee0b5741/displayicon.png";
    private const string RpIcon = "https://media.valorant-api.com/currencies/e59aa87c-4cbf-517a-5983-6e81511be9b7/displayicon.png";
    private const string KcIcon = "https://media.valorant-api.com/currencies/85ca954a-41f2-ce94-9b45-8ca3dd39a00d/displayicon.png";

    private ValorantApi.Store? _store;
    private ValorantContent.Catalog? _catalog;
    private string _error = "";
    private bool _loading;
    private DateTime _loadedAt = DateTime.MinValue;
    private DateTime _attemptedAt = DateTime.MinValue;
    private bool _active;
    private readonly Timer _clock = new() { Interval = 1000 };

    public StorePanel(float scale) : base(scale)
    {
        _clock.Tick += (_, _) =>
        {
            if (!_active) return;
            // The daily store rolled over while you were looking: fetch the new one (once).
            if (_store is not null && !_loading && DateTime.UtcNow > _store.DailyEndsUtc.AddSeconds(5) && DateTime.UtcNow - _attemptedAt > TimeSpan.FromSeconds(30))
                _ = LoadAsync();
            Invalidate();
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
            var ids = store.Daily.Select(o => o.ItemId).Concat(store.NightMarket?.Select(o => o.ItemId) ?? Enumerable.Empty<string>());
            ValorantContent.Catalog? catalog = null;
            try { catalog = await ValorantContent.GetAsync(ids); }
            catch (Exception e) { System.Diagnostics.Trace.WriteLine("Catalog failed: " + e.Message); }

            _store = store;
            _catalog = catalog ?? ValorantContent.Loaded;
            _loadedAt = DateTime.UtcNow;
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

    protected override void OnHitClicked(string key)
    {
        if (key is "action" or "refresh")
            _ = LoadAsync();
    }

    protected override int PaintContent(Graphics g, int width)
    {
        if (_store is null)
        {
            if (_loading)
                return PaintMessage(g, width, "Loading your store…", "This takes a second the first time.", null);
            return PaintMessage(g, width, "Couldn't load your store", _error.Length > 0 ? _error : "Open this tab while the Riot Client is running.", "Try again");
        }

        var y = 0;

        // Wallet + refresh
        var bar = new Rectangle(0, y, width, D(30));
        var x = 0;
        x = PaintBalance(g, x, bar, VpIcon, _store.Vp);
        x = PaintBalance(g, x, bar, RpIcon, _store.Radianite);
        PaintBalance(g, x, bar, KcIcon, _store.KingdomCredits);

        var refreshText = _loading ? "Refreshing…" : "Refresh";
        var rw = Draw.TextWidth("Refreshing…", Theme.Small) + D(28);
        var refresh = new Rectangle(width - rw, y + D(1), rw, D(28));
        Draw.Button(g, refresh, refreshText, HoverKey == "refresh", !_loading, S);
        if (!_loading) Hit(refresh, "refresh");
        y += D(30);

        if (_error.Length > 0)
        {
            var err = new Rectangle(0, y + D(6), width, D(18));
            TextRenderer.DrawText(g, _error, Theme.Small, err, Theme.Warn, Theme.LeftMiddle);
            y += D(22);
        }

        y += D(16);

        // Daily store
        y = PaintSectionHeader(g, y, width, "DAILY STORE", "Resets in " + Draw.Countdown(_store.DailyEndsUtc));
        y = PaintGrid(g, y, width, _store.Daily, nightMarket: false);

        y += D(22);

        // Night Market
        if (_store.NightMarket is { Count: > 0 } night)
        {
            y = PaintSectionHeader(g, y, width, "NIGHT MARKET", "Ends in " + Draw.Countdown(_store.NightMarketEndsUtc));
            y = PaintGrid(g, y, width, night, nightMarket: true);
        }
        else
        {
            y = PaintSectionHeader(g, y, width, "NIGHT MARKET", "");
            var box = new Rectangle(0, y, width, D(64));
            Theme.FillRound(g, Theme.Surface, new RectangleF(box.X + 0.5f, box.Y + 0.5f, box.Width - 1, box.Height - 1), D(10));
            TextRenderer.DrawText(g, "The Night Market isn't open right now.", Theme.Body, box, Theme.Muted, Theme.CenterMiddle);
            y = box.Bottom;
        }

        return y + D(16);
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
        {
            var box = new Rectangle(0, y, width, D(64));
            Theme.FillRound(g, Theme.Surface, new RectangleF(box.X + 0.5f, box.Y + 0.5f, box.Width - 1, box.Height - 1), D(10));
            TextRenderer.DrawText(g, "Nothing here right now.", Theme.Body, box, Theme.Muted, Theme.CenterMiddle);
            return box.Bottom;
        }

        // Two columns normally; three when the window is wide enough.
        var columns = nightMarket && width >= D(560) ? 3 : 2;
        var gap = D(10);
        var cardW = (width - gap * (columns - 1)) / columns;
        var cardH = D(138);

        for (var i = 0; i < offers.Count; i++)
        {
            var col = i % columns;
            var row = i / columns;
            var rect = new Rectangle(col * (cardW + gap), y + row * (cardH + gap), cardW, cardH);
            PaintCard(g, rect, offers[i], nightMarket);
        }

        var rows = (offers.Count + columns - 1) / columns;
        return y + rows * cardH + (rows - 1) * gap;
    }

    private void PaintCard(Graphics g, Rectangle rect, ValorantApi.StoreOffer offer, bool nightMarket)
    {
        ValorantContent.Skin? skin = null;
        _catalog?.Skins.TryGetValue(offer.ItemId, out skin);

        var rf = new RectangleF(rect.X + 0.5f, rect.Y + 0.5f, rect.Width - 1, rect.Height - 1);
        var radius = D(10);
        Theme.FillRound(g, Theme.Surface, rf, radius);

        // A soft wash of the rarity color behind the gun.
        var rarity = skin?.Rarity ?? Color.Empty;
        if (rarity != Color.Empty)
        {
            using var path = Theme.Round(rf, radius);
            using var brush = new LinearGradientBrush(rect, Color.FromArgb(46, rarity), Color.FromArgb(0, rarity), LinearGradientMode.Vertical);
            g.FillPath(brush, path);
        }

        Theme.StrokeRound(g, rarity != Color.Empty ? Color.FromArgb(90, rarity) : Theme.Border, rf, radius);

        var pad = D(12);
        // Below the rarity icon and discount badge, above the name.
        var imageBox = new RectangleF(rect.X + pad, rect.Y + D(30), rect.Width - pad * 2, D(54));
        var image = skin is null ? null : ImageCache.Get(skin.Icon);
        if (image is not null)
            Draw.Fit(g, image, imageBox);
        else
            TextRenderer.DrawText(g, skin is null ? "" : "Loading…", Theme.Small, Rectangle.Round(imageBox), Theme.Faint, Theme.CenterMiddle);

        if (skin?.RarityIcon is { Length: > 0 } rarityIcon)
            Draw.Fit(g, ImageCache.Get(rarityIcon), new RectangleF(rect.X + D(10), rect.Y + D(10), D(16), D(16)));

        if (nightMarket && offer.DiscountPercent > 0)
        {
            var badgeText = $"-{offer.DiscountPercent}%";
            var bw = Draw.TextWidth(badgeText, Theme.Section) + D(14);
            var badge = new Rectangle(rect.Right - D(10) - bw, rect.Y + D(10), bw, D(20));
            Theme.FillRound(g, Theme.Accent, badge, D(10));
            TextRenderer.DrawText(g, badgeText, Theme.Section, badge, Color.White, Theme.CenterMiddle);
        }

        // Name
        var name = skin?.Name is { Length: > 0 } n ? n : "Unknown item";
        var nameRect = new Rectangle(rect.X + pad, rect.Bottom - D(52), rect.Width - pad * 2, D(20));
        TextRenderer.DrawText(g, name, Theme.BodyBold, nameRect, Theme.Text, Theme.LeftMiddle);

        // Price
        var priceRow = new Rectangle(rect.X + pad, rect.Bottom - D(30), rect.Width - pad * 2, D(20));
        var icon = D(15);
        var px = priceRow.X;
        if (offer.Cost > 0)
        {
            Draw.Fit(g, ImageCache.Get(VpIcon), new RectangleF(px, priceRow.Y + (priceRow.Height - icon) / 2f, icon, icon));
            px += icon + D(6);
            var price = Draw.Number(offer.Cost);
            var w = Draw.TextWidth(price, Draw.Price);
            TextRenderer.DrawText(g, price, Draw.Price, new Rectangle(px, priceRow.Y, w + D(2), priceRow.Height), Theme.Text, Theme.LeftMiddle);
            px += w + D(10);

            if (nightMarket && offer.BaseCost > offer.Cost)
            {
                var was = Draw.Number(offer.BaseCost);
                var ww = Draw.TextWidth(was, Theme.Small);
                var wasRect = new Rectangle(px, priceRow.Y, ww + D(2), priceRow.Height);
                TextRenderer.DrawText(g, was, Theme.Small, wasRect, Theme.Faint, Theme.LeftMiddle);
                using var pen = new Pen(Theme.Faint, Math.Max(1f, S));
                var mid = priceRow.Y + priceRow.Height / 2f + D(1);
                g.DrawLine(pen, px - D(1), mid, px + ww + D(1), mid);
            }
        }
        else
        {
            TextRenderer.DrawText(g, "Price unavailable", Theme.Small, priceRow, Theme.Faint, Theme.LeftMiddle);
        }
    }
}
