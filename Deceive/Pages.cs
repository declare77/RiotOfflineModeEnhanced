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
        TextRenderer.DrawText(g, text, font, rect, color, flags | TextFormatFlags.PreserveGraphicsTranslateTransform | TextFormatFlags.PreserveGraphicsClipping);

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
/// Base for the hand-drawn tab pages: an optional fixed header (sub-tabs, search) and content
/// below it that scrolls with the mouse wheel or a slim dark scrollbar drawn on the right.
/// </summary>
internal abstract class PagePanel : Control
{
    protected readonly float S;
    private string _hoverKey = "";
    private readonly List<(Rectangle rect, string key, bool fixedArea)> _hits = new();
    private bool _paintingHeader;

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

    /// <summary>Registers a clickable area while painting (in header or content coordinates).</summary>
    protected void Hit(Rectangle rect, string key) => _hits.Add((rect, key, _paintingHeader));

    protected virtual void OnHitClicked(string key) { }

    /// <summary>Height of the fixed header; 0 for none.</summary>
    protected virtual int HeaderHeight => 0;

    /// <summary>Paints the fixed header at (0,0).</summary>
    protected virtual void PaintHeader(Graphics g, int width) { }

    /// <summary>Paints the scrolling content at (0,0) and returns its total height.</summary>
    protected abstract int PaintContent(Graphics g, int width);

    /// <summary>Height of the scrolling area.</summary>
    protected int ViewHeight => Math.Max(0, Height - HeaderHeight);

    private bool Overflows => _contentHeight > ViewHeight;
    private int MaxScroll => Math.Max(0, _contentHeight - ViewHeight);
    private int Gutter => Overflows ? D(14) : 0;

    private Rectangle ThumbRect()
    {
        if (!Overflows || ViewHeight <= 0) return Rectangle.Empty;
        var trackH = ViewHeight;
        var thumbH = Math.Max(D(36), (int)((long)trackH * ViewHeight / Math.Max(1, _contentHeight)));
        var y = MaxScroll == 0 ? 0 : (int)((long)(trackH - thumbH) * _scroll / MaxScroll);
        return new Rectangle(Width - D(8), HeaderHeight + y, D(6), thumbH);
    }

    public void ScrollBy(int pixels) => ScrollTo(_scroll + pixels);

    protected void ScrollToTop() => ScrollTo(0);

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

        var header = HeaderHeight;
        if (header > 0)
        {
            _paintingHeader = true;
            var hs = g.Save();
            g.SetClip(new Rectangle(0, 0, Width, header));
            PaintHeader(g, Width);
            g.Restore(hs);
            _paintingHeader = false;
        }

        var state = g.Save();
        g.SetClip(new Rectangle(0, header, Width, ViewHeight));
        g.TranslateTransform(0, header - _scroll);
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
        var header = HeaderHeight;
        var inHeader = clientPoint.Y < header;
        var content = new Point(clientPoint.X, clientPoint.Y - header + _scroll);
        foreach (var (rect, key, fixedArea) in _hits)
        {
            if (fixedArea != inHeader) continue;
            if (rect.Contains(fixedArea ? clientPoint : content)) return key;
        }

        return "";
    }

    private bool OverScrollbar(Point p) => Overflows && p.X >= Width - D(14) && p.Y >= HeaderHeight;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_dragging)
        {
            var thumb = ThumbRect();
            var travel = Math.Max(1, ViewHeight - thumb.Height);
            ScrollTo((int)((long)(e.Y - HeaderHeight - _dragOffset) * MaxScroll / travel));
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
        if (!ContainsFocus) Focus(); // so the mouse wheel scrolls this page
        if (e.Button != MouseButtons.Left) return;

        if (OverScrollbar(e.Location))
        {
            var thumb = ThumbRect();
            if (e.Y < thumb.Top || e.Y >= thumb.Bottom)
            {
                // Clicked the track: jump so the thumb centers on the pointer.
                var travel = Math.Max(1, ViewHeight - thumb.Height);
                ScrollTo((int)((long)(e.Y - HeaderHeight - thumb.Height / 2) * MaxScroll / travel));
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
        var top = Math.Max(D(40), ViewHeight / 2 - D(70));
        var rect = new Rectangle(0, top, width, D(24));
        TextRenderer.DrawText(g, title, Theme.BodyBold, rect, Theme.Text, Theme.CenterMiddle);
        var detailRect = new Rectangle(D(24), rect.Bottom + D(4), Math.Max(0, width - D(48)), D(40));
        TextRenderer.DrawText(g, detail, Theme.Small, detailRect, Theme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsTranslateTransform | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.NoPadding);
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

    /// <summary>Pill-style sub-tabs, drawn in place. Keys are "sub:0", "sub:1", ...</summary>
    protected int PaintSubTabs(Graphics g, int y, string[] labels, int selected)
    {
        var x = 0;
        for (var i = 0; i < labels.Length; i++)
        {
            var w = Draw.TextWidth(labels[i], Theme.Small) + D(24);
            var rect = new Rectangle(x, y, w, D(28));
            var rf = new RectangleF(rect.X + 0.5f, rect.Y + 0.5f, rect.Width - 1, rect.Height - 1);
            var hover = HoverKey == "sub:" + i;
            if (i == selected)
                Theme.FillRound(g, Theme.Raised, rf, rf.Height / 2);
            else if (hover)
                Theme.FillRound(g, Theme.Hover, rf, rf.Height / 2);
            Theme.StrokeRound(g, i == selected ? ColorTranslator.FromHtml("#3A4250") : Theme.Border, rf, rf.Height / 2);
            TextRenderer.DrawText(g, labels[i], Theme.Small, rect, i == selected || hover ? Theme.Text : Theme.Muted, Theme.CenterMiddle);
            Hit(rect, "sub:" + i);
            x = rect.Right + D(8);
        }

        return y + D(28);
    }

    /// <summary>A small star, filled when on the wishlist.</summary>
    protected static void PaintStar(Graphics g, RectangleF box, bool filled, bool hover)
    {
        var cx = box.X + box.Width / 2f;
        var cy = box.Y + box.Height / 2f + box.Height * 0.04f;
        var outer = box.Width / 2f;
        var inner = outer * 0.45f;
        var points = new PointF[10];
        for (var i = 0; i < 10; i++)
        {
            var r = i % 2 == 0 ? outer : inner;
            var angle = Math.PI / 5 * i - Math.PI / 2;
            points[i] = new PointF(cx + (float)(r * Math.Cos(angle)), cy + (float)(r * Math.Sin(angle)));
        }

        var gold = ColorTranslator.FromHtml("#F2C14E");
        if (filled)
        {
            using var brush = new SolidBrush(hover ? ColorTranslator.FromHtml("#F7D27A") : gold);
            g.FillPolygon(brush, points);
        }
        else
        {
            using var pen = new Pen(hover ? gold : Theme.Muted, Math.Max(1.2f, box.Width / 12f)) { LineJoin = LineJoin.Round };
            g.DrawPolygon(pen, points);
        }
    }
}
