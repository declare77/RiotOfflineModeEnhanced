using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Deceive;

/// <summary>What the app window needs from the app (implemented by MainController).</summary>
internal interface IAppHost
{
    string Status { get; }
    bool Enabled { get; }
    bool IsConnected { get; }
    event EventHandler? StateChanged;
    Task SetStatusAsync(string status);
    Task SetEnabledAsync(bool enabled);
    Task ApplySettingsQuietlyAsync();
}

internal static class Theme
{
    public static readonly Color Bg = Hex("#0E1014");
    public static readonly Color Surface = Hex("#161920");
    public static readonly Color Hover = Hex("#1E222B");
    public static readonly Color Raised = Hex("#232833");
    public static readonly Color Border = Hex("#262B35");
    public static readonly Color Text = Hex("#ECEEF2");
    public static readonly Color Muted = Hex("#8C95A6");
    public static readonly Color Faint = Hex("#596173");
    public static readonly Color Accent = Hex("#FF4F5E");
    public static readonly Color AccentHover = Hex("#FF6674");
    public static readonly Color Online = Hex("#3CCB7F");
    public static readonly Color InGame = Hex("#4AA3FF");
    public static readonly Color Away = Hex("#F2B63C");
    public static readonly Color Mobile = Hex("#9AA3B5");
    public static readonly Color Warn = Hex("#F2B63C");

    public static readonly Font Title = new("Segoe UI Semibold", 15f);
    public static readonly Font Body = new("Segoe UI", 9.75f);
    public static readonly Font BodyBold = new("Segoe UI Semibold", 9.75f);
    public static readonly Font Small = new("Segoe UI", 8.75f);
    public static readonly Font Section = new("Segoe UI Semibold", 8f);

    private static Color Hex(string h) => ColorTranslator.FromHtml(h);

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0)
        {
            path.AddRectangle(r);
            return path;
        }

        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRound(Graphics g, Color color, RectangleF r, float radius)
    {
        using var path = Round(r, radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    public static void StrokeRound(Graphics g, Color color, RectangleF r, float radius, float width = 1f)
    {
        using var path = Round(r, radius);
        using var pen = new Pen(color, width);
        g.DrawPath(pen, path);
    }

    public static void Dot(Graphics g, Color color, float cx, float cy, float size)
    {
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, cx - size / 2, cy - size / 2, size, size);
    }

    public static Color StateColor(FriendDirectory.OnlineState s) => s switch
    {
        FriendDirectory.OnlineState.Online => Online,
        FriendDirectory.OnlineState.InGame => InGame,
        FriendDirectory.OnlineState.Away => Away,
        FriendDirectory.OnlineState.Mobile => Mobile,
        _ => Faint
    };

    public static string StateText(FriendDirectory.OnlineState s) => s switch
    {
        FriendDirectory.OnlineState.Online => "Online",
        FriendDirectory.OnlineState.InGame => "In game",
        FriendDirectory.OnlineState.Away => "Away",
        FriendDirectory.OnlineState.Mobile => "Mobile",
        _ => ""
    };

    public const TextFormatFlags LeftMiddle = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
    /// <summary>Flags for measuring text. Never include ellipsis flags here: on Windows they make MeasureText return a truncated width.</summary>
    public const TextFormatFlags Measure = TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
    public const TextFormatFlags CenterMiddle = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
    public const TextFormatFlags RightMiddle = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
}

/// <summary>The main window: status and who-can-see-you list, plus the Store and Agent select tabs.</summary>
internal sealed class MainWindow : Form
{
    private readonly IAppHost _host;
    private readonly float _s;

    private readonly ToggleSwitch _masking;
    private readonly SegmentedControl _status;
    private readonly SearchBox _search;
    private readonly ChipBar _chips;
    private readonly FriendList _list;
    private readonly Label _empty;
    private readonly CheckLabel _keepOnTop;
    private readonly LinkText _settingsLink;
    private readonly TabBar _tabs;
    private readonly StorePanel _storePanel;
    private readonly MatchPanel _agentPanel;
    private readonly CareerPanel _careerPanel;
    private readonly FriendsPanel _friendsPanel;
    private Control[] _statusControls = Array.Empty<Control>();

    private const int StatusTab = 0, StoreTab = 1, AgentTab = 2, CareerTab = 3, FriendsTab = 4;

    private readonly Timer _refreshTimer = new() { Interval = 350 };
    private readonly Timer _applyTimer = new() { Interval = 600 };

    // Layout results used by OnPaint.
    private Rectangle _titleRect, _subtitleRect, _maskingLabelRect, _statusLabelRect, _visibleLabelRect, _visibleCountRect, _helpRect, _footerLine, _hintRect;

    private static readonly string[] StatusKeys = { "chat", "offline", "mobile" };

    internal MainWindow(IAppHost host)
    {
        _host = host;
        using (var screen = Graphics.FromHwnd(IntPtr.Zero))
            _s = Math.Max(1f, screen.DpiX / 96f);

        SuspendLayout();
        AutoScaleMode = AutoScaleMode.None;
        Text = AppInfo.Name;
        Font = Theme.Body;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        // Roomy enough for the store grid and five teammates, but never taller than the screen.
        var work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1040);
        var chrome = SystemInformation.CaptionHeight + SystemInformation.FrameBorderSize.Height * 2 + D(16);
        ClientSize = new Size(Math.Min(D(580), work.Width - D(40)), Math.Max(D(540), Math.Min(D(780), work.Height - chrome)));
        MinimumSize = new Size(D(480), D(560));
        KeyPreview = true;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);

        _masking = new ToggleSwitch(_s);
        _masking.Toggled += (_, _) => Fire(() => _host.SetEnabledAsync(!_host.Enabled));

        _status = new SegmentedControl(_s, new[] { "Online", "Offline", "Mobile" }, new[] { Theme.Online, Theme.Faint, Theme.Mobile });
        _status.SegmentClicked += (_, i) => Fire(() => _host.SetStatusAsync(StatusKeys[i]));

        _search = new SearchBox(_s);
        _search.TextChanged += (_, _) => RefreshRows();

        _chips = new ChipBar(_s, new[] { "All", "Online", "Selected" });
        _chips.SelectedChanged += (_, _) => RefreshRows();

        _list = new FriendList(_s);
        _list.RowClicked += (_, row) => ToggleRow(row);

        _empty = new Label
        {
            ForeColor = Theme.Muted,
            BackColor = Theme.Surface,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = Theme.Body,
            Visible = false
        };

        _keepOnTop = new CheckLabel(_s, "Keep on top");
        _keepOnTop.Toggled += (_, _) =>
        {
            var s = AppSettings.Current.Clone();
            s.KeepOnTop = !s.KeepOnTop;
            AppSettings.Save(s);
            ApplyKeepOnTop();
        };

        _settingsLink = new LinkText(_s, "Edit settings file");
        _settingsLink.Click += (_, _) => AppSettings.OpenInEditor();

        _tabs = new TabBar(_s, new[] { "Status", "Store", "Match", "Career", "Friends" });
        _tabs.SelectedChanged += (_, _) => OnTabChanged();

        _storePanel = new StorePanel(_s) { Visible = false };
        _agentPanel = new MatchPanel(_s) { Visible = false };
        _careerPanel = new CareerPanel(_s) { Visible = false };
        _friendsPanel = new FriendsPanel(_s) { Visible = false };

        _statusControls = new Control[] { _status, _search, _chips, _empty, _list };
        Controls.AddRange(new Control[] { _masking, _tabs, _status, _search, _chips, _empty, _list, _storePanel, _agentPanel, _careerPanel, _friendsPanel, _keepOnTop, _settingsLink });
        ResumeLayout(false);

        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            RefreshAll();
        };
        _applyTimer.Tick += (_, _) =>
        {
            _applyTimer.Stop();
            Fire(() => _host.ApplySettingsQuietlyAsync());
        };

        _host.StateChanged += OnHostStateChanged;
        FriendDirectory.Changed += OnFriendsChanged;

        ApplyKeepOnTop();
        LayoutAll();
        RefreshAll();
        MatchData.Warm();
    }

    private int D(float px) => (int)Math.Round(px * _s);

    private bool _showQuietly;
    protected override bool ShowWithoutActivation => _showQuietly;

    internal void ShowAndFocus(bool activate = true)
    {
        if (!Visible)
        {
            _showQuietly = !activate;
            Show();
            _showQuietly = false;
        }
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        RefreshAll();
        if (!activate)
            return;
        Activate();
        BringToFront();
    }



    // ------------------------------------------------------------------ events

    private void OnHostStateChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(new Action(RefreshAll));
    }

    private void OnFriendsChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || !IsHandleCreated) return;
        // Presence updates can be frequent; batch them.
        BeginInvoke(new Action(() =>
        {
            if (!_refreshTimer.Enabled) _refreshTimer.Start();
        }));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.UseDarkTitleBar(Handle, Theme.Bg);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutAll();
        UpdatePageActivity();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        // Scroll the open page even when the wheel event lands on the window itself.
        PagePanel? page = _tabs.SelectedIndex switch
        {
            StoreTab => _storePanel,
            AgentTab => _agentPanel,
            CareerTab => _careerPanel,
            FriendsTab => _friendsPanel,
            _ => null
        };
        page?.ScrollBy(-e.Delta * D(60) / 120);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        UpdatePageActivity();
    }

    private void OnTabChanged()
    {
        var tab = _tabs.SelectedIndex;
        foreach (var c in _statusControls)
            c.Visible = tab == StatusTab && (c != _empty || _emptyWanted);
        _storePanel.Visible = tab == StoreTab;
        _agentPanel.Visible = tab == AgentTab;
        _careerPanel.Visible = tab == CareerTab;
        _friendsPanel.Visible = tab == FriendsTab;
        _tabs.Invalidate();
        if (tab == StatusTab && _empty.Visible) _empty.BringToFront();
        UpdatePageActivity();
        Invalidate();
    }

    /// <summary>The Store and Agent select tabs only talk to Riot while you can see them.</summary>
    private void UpdatePageActivity()
    {
        if (_tabs is null || _storePanel is null || _agentPanel is null || _careerPanel is null || _friendsPanel is null) return;
        var showing = Visible && WindowState != FormWindowState.Minimized;
        _storePanel.SetActive(showing && _tabs.SelectedIndex == StoreTab);
        _agentPanel.SetActive(showing && _tabs.SelectedIndex == AgentTab);
        _careerPanel.SetActive(showing && _tabs.SelectedIndex == CareerTab);
        _friendsPanel.SetActive(showing && _tabs.SelectedIndex == FriendsTab);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            // Closing the window doesn't quit the app; it keeps running in the tray.
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnFormClosing(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && _search.Text.Length > 0)
        {
            _search.Text = string.Empty;
            return true;
        }

        if (keyData == (Keys.Control | Keys.F) && _tabs.SelectedIndex == StatusTab)
        {
            _search.FocusInput();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _host.StateChanged -= OnHostStateChanged;
            FriendDirectory.Changed -= OnFriendsChanged;
            _refreshTimer.Dispose();
            _applyTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Fire(Func<Task> action)
    {
        async void Run()
        {
            try { await action(); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"{AppInfo.Name} window: " + ex); }
        }

        Run();
    }

    // ------------------------------------------------------------------ state

    private void RefreshAll()
    {
        if (IsDisposed) return;
        _masking.Checked = _host.Enabled;
        _status.SelectedIndex = Array.IndexOf(StatusKeys, _host.Status);
        _status.Dimmed = !_host.Enabled;
        _keepOnTop.Checked = AppSettings.Current.KeepOnTop;
        RefreshRows();
        Invalidate();
    }

    private void ApplyKeepOnTop()
    {
        TopMost = AppSettings.Current.KeepOnTop;
        _keepOnTop.Checked = TopMost;
    }

    private bool _emptyWanted;
    private int _selectedCount;
    private int _visibleCount; // ticked friends that are actually on the friends list

    private void RefreshRows()
    {
        var settings = AppSettings.Current;
        var friends = FriendDirectory.All();
        var checkedJids = new HashSet<string>(FriendDirectory.Resolve(settings.VisibleTo, out _, out var notFound), StringComparer.OrdinalIgnoreCase);

        var rows = friends.Select(f => new FriendList.Row
        {
            Jid = f.Jid,
            Name = f.Name,
            Tag = f.Tag,
            State = FriendDirectory.GetState(f.Jid),
            Checked = checkedJids.Contains(f.Jid)
        }).ToList();

        // Entries in the settings that aren't on the friends list (typo, or list not loaded yet).
        foreach (var entry in notFound)
        {
            var hash = entry.IndexOf('#');
            rows.Add(new FriendList.Row
            {
                Entry = entry,
                Name = hash < 0 ? entry : entry.Substring(0, hash).Trim(),
                Tag = hash < 0 ? "" : entry.Substring(hash + 1).Trim(),
                Checked = true,
                Missing = true
            });
        }

        _selectedCount = rows.Count(r => r.Checked);
        _visibleCount = rows.Count(r => r.Checked && !r.Missing);
        _chips.SetLabel(2, _selectedCount > 0 ? $"Selected ({_selectedCount})" : "Selected");

        var query = _search.Text.Trim();
        IEnumerable<FriendList.Row> visible = rows;
        if (query.Length > 0)
            visible = visible.Where(r => (r.Name + "#" + r.Tag).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
        visible = _chips.SelectedIndex switch
        {
            1 => visible.Where(r => r.State != FriendDirectory.OnlineState.Offline),
            2 => visible.Where(r => r.Checked),
            _ => visible
        };

        var list = visible
            .OrderBy(r => r.Missing)
            .ThenByDescending(r => r.State != FriendDirectory.OnlineState.Offline)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Tag, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _list.SetRows(list);

        if (list.Count == 0)
        {
            _empty.Text = friends.Count == 0 && notFound.Count == 0
                ? (_host.IsConnected ? "Loading your friends list…" : "Your friends will show up here once\nyou're signed in to the Riot Client.")
                : query.Length > 0
                    ? $"No friends match \"{query}\"."
                    : _chips.SelectedIndex == 1 ? "None of your friends are online."
                    : _chips.SelectedIndex == 2 ? "Nobody selected yet.\nTick friends who should still see you."
                    : "No friends to show.";
        }

        _emptyWanted = list.Count == 0;
        _empty.Visible = _emptyWanted && _tabs.SelectedIndex == StatusTab;
        if (_empty.Visible) _empty.BringToFront();
        Invalidate(Rectangle.Union(_subtitleRect, _visibleCountRect));
        Invalidate(_helpRect);
        Invalidate(_hintRect);
    }

    private void ToggleRow(FriendList.Row row)
    {
        var s = AppSettings.Current.Clone();

        if (row.Missing)
        {
            s.VisibleTo.RemoveAll(v => string.Equals(v.Trim(), row.Entry, StringComparison.OrdinalIgnoreCase));
        }
        else if (!row.Checked)
        {
            s.VisibleTo.Add(row.Name + "#" + row.Tag);
        }
        else
        {
            // Remove every entry that covers this friend, but keep any other friends those entries matched
            // (e.g. a name without a #tag that matched two people).
            var friends = FriendDirectory.All();
            var keep = new List<string>();
            foreach (var entry in s.VisibleTo)
            {
                var hits = FriendDirectory.Match(entry, friends, out var unknownJid);
                if (unknownJid is not null)
                {
                    if (!unknownJid.Equals(row.Jid, StringComparison.OrdinalIgnoreCase)) keep.Add(entry);
                    continue;
                }

                if (!hits.Any(h => h.Jid.Equals(row.Jid, StringComparison.OrdinalIgnoreCase)))
                {
                    keep.Add(entry);
                    continue;
                }

                keep.AddRange(hits.Where(h => !h.Jid.Equals(row.Jid, StringComparison.OrdinalIgnoreCase)).Select(h => h.RiotId));
            }

            s.VisibleTo = keep;
        }

        AppSettings.Save(s);
        RefreshRows();

        // Batch quick clicks into one presence update.
        _applyTimer.Stop();
        _applyTimer.Start();
    }

    // ------------------------------------------------------------------ layout & paint

    private void LayoutAll()
    {
        if (_list is null || _tabs is null || _agentPanel is null || _careerPanel is null || _friendsPanel is null) return;
        var w = ClientSize.Width;
        var h = ClientSize.Height;
        var pad = D(20);
        var inner = w - pad * 2;

        _titleRect = new Rectangle(pad, D(16), inner - D(110), D(32));
        _subtitleRect = new Rectangle(pad, D(48), inner, D(20));

        var switchSize = new Size(D(40), D(22));
        _masking.Bounds = new Rectangle(w - pad - switchSize.Width, D(21), switchSize.Width, switchSize.Height);
        _maskingLabelRect = new Rectangle(_masking.Left - D(70), D(21), D(62), switchSize.Height);

        _tabs.Bounds = new Rectangle(pad, D(80), inner, D(34));

        var footerH = D(70);
        var contentTop = D(80) + D(34) + D(18);
        var page = new Rectangle(pad, contentTop, inner, Math.Max(D(80), h - footerH - D(8) - contentTop));
        _storePanel.Bounds = page;
        _agentPanel.Bounds = page;
        _careerPanel.Bounds = page;
        _friendsPanel.Bounds = page;

        var y = contentTop;
        _statusLabelRect = new Rectangle(pad, y, inner, D(16));
        y += D(22);
        _status.Bounds = new Rectangle(pad, y, inner, D(42));
        y += D(42) + D(26);

        _visibleLabelRect = new Rectangle(pad, y, inner, D(16));
        _visibleCountRect = new Rectangle(pad, y, inner, D(16));
        y += D(22);
        _helpRect = new Rectangle(pad, y, inner, D(36));
        y += D(36) + D(8);

        _search.Bounds = new Rectangle(pad, y, inner, D(36));
        y += D(36) + D(10);
        _chips.Bounds = new Rectangle(pad, y, inner, D(28));
        y += D(28) + D(10);

        var listBottom = h - footerH - D(4);
        var listRect = new Rectangle(pad, y, inner, Math.Max(D(60), listBottom - y));
        _list.Bounds = listRect;
        _empty.Bounds = Rectangle.Inflate(listRect, -D(1), -D(1));

        _footerLine = new Rectangle(0, h - footerH, w, 1);
        _keepOnTop.Bounds = new Rectangle(pad, h - footerH + D(14), D(130), D(22));
        var linkSize = _settingsLink.PreferredSize;
        _settingsLink.Bounds = new Rectangle(w - pad - linkSize.Width, h - footerH + D(14), linkSize.Width, D(22));
        _hintRect = new Rectangle(pad, h - footerH + D(40), inner, D(18));

        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        TextRenderer.DrawText(g, AppInfo.Name, TitleFont(), _titleRect, Theme.Text, Theme.LeftMiddle);

        // Subtitle: what friends currently see.
        var (dotColor, subtitle) = Subtitle();
        var dot = D(8);
        Theme.Dot(g, dotColor, _subtitleRect.X + dot / 2f, _subtitleRect.Y + _subtitleRect.Height / 2f, dot);
        var subRect = new Rectangle(_subtitleRect.X + dot + D(8), _subtitleRect.Y, _subtitleRect.Width - dot - D(8), _subtitleRect.Height);
        TextRenderer.DrawText(g, subtitle, Theme.Body, subRect, Theme.Muted, Theme.LeftMiddle);

        TextRenderer.DrawText(g, _host.Enabled ? "Masking" : "Masking off", Theme.Small, _maskingLabelRect,
            _host.Enabled ? Theme.Muted : Theme.Warn, Theme.RightMiddle);

        using (var pen = new Pen(Theme.Border))
            g.DrawLine(pen, _footerLine.Left, _footerLine.Top, _footerLine.Right, _footerLine.Top);
        TextRenderer.DrawText(g, "Closing this window keeps the app running in the tray.", Theme.Small, _hintRect, Theme.Faint, Theme.LeftMiddle);

        if (_tabs.SelectedIndex != StatusTab)
            return;

        TextRenderer.DrawText(g, "YOUR STATUS", Theme.Section, _statusLabelRect, Theme.Faint, Theme.LeftMiddle);
        TextRenderer.DrawText(g, "WHO CAN STILL SEE YOU", Theme.Section, _visibleLabelRect, Theme.Faint, Theme.LeftMiddle);
        if (_visibleCount > 0)
            TextRenderer.DrawText(g, _visibleCount == 1 ? "1 friend" : $"{_visibleCount} friends", Theme.Small, _visibleCountRect, Theme.Muted, Theme.RightMiddle);

        var masked = _host.Enabled && _host.Status != "chat";
        var help = masked
            ? $"Ticked friends see your real status. Everyone else sees you {(_host.Status == "mobile" ? "on mobile" : "as offline")}."
            : "You're visible to everyone right now. Ticked friends will still see you when you switch to Offline or Mobile.";
        TextRenderer.DrawText(g, help, Theme.Small, _helpRect, Theme.Muted,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }

    private Font? _titleFont;
    private int _titleFontWidth = -1;

    /// <summary>The title font, shrunk if needed so a long app name fits next to the Masking switch.</summary>
    private Font TitleFont()
    {
        if (_titleFont is not null && _titleFontWidth == _titleRect.Width)
            return _titleFont;

        var size = Theme.Title.Size;
        Font font = Theme.Title;
        while (size > 10f && TextRenderer.MeasureText(AppInfo.Name, font, Size.Empty, Theme.Measure).Width > _titleRect.Width)
        {
            size -= 0.5f;
            if (!ReferenceEquals(font, Theme.Title)) font.Dispose();
            font = new Font(Theme.Title.FontFamily, size, Theme.Title.Style);
        }

        if (_titleFont is not null && !ReferenceEquals(_titleFont, Theme.Title)) _titleFont.Dispose();
        _titleFont = font;
        _titleFontWidth = _titleRect.Width;
        return font;
    }

    private (Color, string) Subtitle()
    {
        if (!_host.IsConnected)
            return (Theme.Faint, "Waiting for the Riot Client to connect…");
        if (!_host.Enabled)
            return (Theme.Online, "Masking is off. Everyone sees you online.");

        var who = _visibleCount switch
        {
            0 => "",
            1 => " · 1 friend can see you",
            _ => $" · {_visibleCount} friends can see you"
        };

        return _host.Status switch
        {
            "chat" => (Theme.Online, "Appearing online to everyone"),
            "mobile" => (Theme.Mobile, "Appearing on mobile" + who),
            _ => (Theme.Faint, "Appearing offline" + who)
        };
    }
}

// ====================================================================== controls

internal abstract class DrawnControl : Control
{
    protected readonly float S;

    protected DrawnControl(float scale)
    {
        S = scale;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Theme.Bg;
        Cursor = Cursors.Hand;
    }

    protected int D(float px) => (int)Math.Round(px * S);

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using var brush = new SolidBrush(Parent?.BackColor ?? Theme.Bg);
        e.Graphics.FillRectangle(brush, ClientRectangle);
    }
}

internal sealed class ToggleSwitch : DrawnControl
{
    private bool _checked;
    private bool _hover;

    public ToggleSwitch(float scale) : base(scale) { }

    public event EventHandler? Toggled;

    public bool Checked
    {
        get => _checked;
        set { if (_checked != value) { _checked = value; Invalidate(); } }
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Toggled?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        var track = _checked ? (_hover ? Theme.AccentHover : Theme.Accent) : (_hover ? Hex("#465062") : Hex("#3A4250"));
        Theme.FillRound(g, track, r, r.Height / 2);

        var knob = r.Height - D(6);
        var x = _checked ? r.Right - D(3) - knob : r.X + D(3);
        using var brush = new SolidBrush(Color.White);
        g.FillEllipse(brush, x, r.Y + D(3), knob, knob);
    }

    private static Color Hex(string h) => ColorTranslator.FromHtml(h);
}

internal sealed class SegmentedControl : DrawnControl
{
    private readonly string[] _items;
    private readonly Color[] _dots;
    private int _selected = -1;
    private int _hover = -1;
    private bool _dimmed;

    public SegmentedControl(float scale, string[] items, Color[] dots) : base(scale)
    {
        _items = items;
        _dots = dots;
    }

    public event EventHandler<int>? SegmentClicked;

    public int SelectedIndex
    {
        get => _selected;
        set { if (_selected != value) { _selected = value; Invalidate(); } }
    }

    public bool Dimmed
    {
        get => _dimmed;
        set { if (_dimmed != value) { _dimmed = value; Invalidate(); } }
    }

    private int IndexAt(Point p) => Width <= 0 ? -1 : Math.Max(0, Math.Min(_items.Length - 1, p.X * _items.Length / Width));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var i = IndexAt(e.Location);
        if (i != _hover) { _hover = i; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left)
            SegmentClicked?.Invoke(this, IndexAt(e.Location));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var outer = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Theme.FillRound(g, Theme.Surface, outer, D(10));
        Theme.StrokeRound(g, Theme.Border, outer, D(10));

        var segW = (Width - D(8)) / (float)_items.Length;
        for (var i = 0; i < _items.Length; i++)
        {
            var seg = new RectangleF(D(4) + i * segW, D(4), segW, Height - D(8));
            var selected = i == _selected;

            if (selected)
                Theme.FillRound(g, _dimmed ? Theme.Raised : Theme.Accent, seg, D(7));
            else if (i == _hover)
                Theme.FillRound(g, Theme.Hover, seg, D(7));

            var font = selected ? Theme.BodyBold : Theme.Body;
            var textSize = TextRenderer.MeasureText(g, _items[i], font, Size.Empty, Theme.Measure);
            var dot = D(7);
            var gap = D(7);
            var total = dot + gap + textSize.Width;
            var startX = seg.X + (seg.Width - total) / 2f;

            var dotColor = selected && !_dimmed ? Color.White : _dots[i];
            Theme.Dot(g, dotColor, startX + dot / 2f, seg.Y + seg.Height / 2f, dot);

            var textRect = new Rectangle((int)(startX + dot + gap), (int)seg.Y, textSize.Width + D(2), (int)seg.Height);
            var color = selected ? Color.White : (i == _hover ? Theme.Text : Theme.Muted);
            TextRenderer.DrawText(g, _items[i], font, textRect, color, Theme.LeftMiddle);
        }
    }
}

internal sealed class TabBar : DrawnControl
{
    private readonly string[] _labels;
    private readonly List<Rectangle> _rects = new();
    private int _selected;
    private int _hover = -1;

    public TabBar(float scale, string[] labels) : base(scale)
    {
        _labels = labels;
        Cursor = Cursors.Default;
    }

    public event EventHandler? SelectedChanged;

    public int SelectedIndex => _selected;

    private int IndexAt(Point p) => _rects.FindIndex(r => r.Contains(p));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var i = IndexAt(e.Location);
        Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
        if (i != _hover) { _hover = i; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        var i = IndexAt(e.Location);
        if (i < 0 || i == _selected) return;
        _selected = i;
        Invalidate();
        SelectedChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        _rects.Clear();

        using (var line = new Pen(Theme.Border))
            g.DrawLine(line, 0, Height - 1, Width, Height - 1);

        var x = 0;
        for (var i = 0; i < _labels.Length; i++)
        {
            var font = i == _selected ? Theme.BodyBold : Theme.Body;
            var w = TextRenderer.MeasureText(g, _labels[i], Theme.BodyBold, Size.Empty, Theme.Measure).Width + D(4);
            var rect = new Rectangle(x, 0, w, Height);
            _rects.Add(new Rectangle(x - D(8), 0, w + D(16), Height));

            var color = i == _selected ? Theme.Text : i == _hover ? Theme.Text : Theme.Muted;
            TextRenderer.DrawText(g, _labels[i], font, new Rectangle(rect.X, 0, rect.Width, Height - D(4)), color, Theme.CenterMiddle);

            if (i == _selected)
                Theme.FillRound(g, Theme.Accent, new RectangleF(rect.X, Height - D(3), rect.Width, D(3)), D(1.5f));

            x = rect.Right + D(22);
        }
    }
}

internal sealed class ChipBar : DrawnControl
{
    private readonly string[] _labels;
    private readonly List<Rectangle> _chipRects = new();
    private int _selected;
    private int _hover = -1;

    public ChipBar(float scale, string[] labels) : base(scale)
    {
        _labels = labels.ToArray();
        Cursor = Cursors.Default;
    }

    public event EventHandler? SelectedChanged;

    public int SelectedIndex => _selected;

    public void SetLabel(int index, string label)
    {
        if (_labels[index] == label) return;
        _labels[index] = label;
        Invalidate();
    }

    private int IndexAt(Point p) => _chipRects.FindIndex(r => r.Contains(p));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var i = IndexAt(e.Location);
        Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
        if (i != _hover) { _hover = i; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        var i = IndexAt(e.Location);
        if (i < 0 || i == _selected) return;
        _selected = i;
        Invalidate();
        SelectedChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        _chipRects.Clear();

        var x = 0;
        for (var i = 0; i < _labels.Length; i++)
        {
            var size = TextRenderer.MeasureText(g, _labels[i], Theme.Small, Size.Empty, Theme.Measure);
            var rect = new Rectangle(x, 0, size.Width + D(24), Height - 1);
            _chipRects.Add(rect);

            var rf = new RectangleF(rect.X + 0.5f, rect.Y + 0.5f, rect.Width - 1, rect.Height - 1);
            if (i == _selected)
                Theme.FillRound(g, Theme.Raised, rf, rf.Height / 2);
            else if (i == _hover)
                Theme.FillRound(g, Theme.Hover, rf, rf.Height / 2);
            Theme.StrokeRound(g, i == _selected ? Hex("#3A4250") : Theme.Border, rf, rf.Height / 2);

            TextRenderer.DrawText(g, _labels[i], Theme.Small, rect, i == _selected ? Theme.Text : Theme.Muted, Theme.CenterMiddle);
            x = rect.Right + D(8);
        }
    }

    private static Color Hex(string h) => ColorTranslator.FromHtml(h);
}

internal sealed class SearchBox : DrawnControl
{
    private readonly TextBox _input;
    private bool _focused;

    public SearchBox(float scale, string placeholder = "Search friends") : base(scale)
    {
        Cursor = Cursors.IBeam;
        _input = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Surface,
            ForeColor = Theme.Text,
            Font = Theme.Body
        };
        _input.TextChanged += (_, _) => OnTextChanged(EventArgs.Empty);
        _input.GotFocus += (_, _) => { _focused = true; Invalidate(); };
        _input.LostFocus += (_, _) => { _focused = false; Invalidate(); };
        _input.HandleCreated += (_, _) => Native.SetCueBanner(_input.Handle, placeholder);
        Controls.Add(_input);
    }

    public override string Text
    {
        get => _input?.Text ?? string.Empty;
        set { if (_input is not null) _input.Text = value; }
    }

    public void FocusInput() => _input.Focus();

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        _input.Focus();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var left = D(36);
        var h = _input.PreferredHeight;
        _input.Bounds = new Rectangle(left, (Height - h) / 2, Width - left - D(12), h);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Theme.FillRound(g, Theme.Surface, r, D(9));
        Theme.StrokeRound(g, _focused ? Theme.Accent : Theme.Border, r, D(9), _focused ? 1.5f : 1f);

        // Magnifying glass
        var cx = D(18);
        var cy = Height / 2f;
        var radius = D(5.5f);
        using var pen = new Pen(Theme.Faint, Math.Max(1.5f, 1.6f * S));
        g.DrawEllipse(pen, cx - radius - D(1), cy - radius - D(1), radius * 2, radius * 2);
        g.DrawLine(pen, cx + radius * 0.55f, cy + radius * 0.55f, cx + radius * 1.25f, cy + radius * 1.25f);
    }
}

internal sealed class CheckLabel : DrawnControl
{
    private readonly string _text;
    private bool _checked;
    private bool _hover;

    public CheckLabel(float scale, string text) : base(scale) => _text = text;

    public event EventHandler? Toggled;

    public bool Checked
    {
        get => _checked;
        set { if (_checked != value) { _checked = value; Invalidate(); } }
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Toggled?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var box = D(16);
        var rect = new RectangleF(0.5f, (Height - box) / 2f, box, box);
        FriendList.DrawCheckbox(g, rect, _checked, _hover, S);
        TextRenderer.DrawText(g, _text, Theme.Small, new Rectangle(box + D(8), 0, Width - box - D(8), Height),
            _hover ? Theme.Text : Theme.Muted, Theme.LeftMiddle);
    }
}

internal sealed class LinkText : DrawnControl
{
    private readonly string _text;
    private bool _hover;

    public LinkText(float scale, string text) : base(scale) => _text = text;

    public override Size GetPreferredSize(Size proposedSize)
    {
        var size = TextRenderer.MeasureText(_text, Theme.Small, Size.Empty, Theme.Measure);
        return new Size(size.Width + D(4), size.Height);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var font = _hover ? new Font(Theme.Small, FontStyle.Underline) : Theme.Small;
        TextRenderer.DrawText(e.Graphics, _text, font, ClientRectangle, _hover ? Theme.Text : Theme.Muted, Theme.RightMiddle);
        if (_hover) font.Dispose();
    }
}

internal sealed class FriendList : ListBox
{
    internal sealed class Row
    {
        public string Jid = "";
        public string Entry = "";
        public string Name = "";
        public string Tag = "";
        public FriendDirectory.OnlineState State;
        public bool Checked;
        public bool Missing;

        public string Key => Missing ? "missing:" + Entry : Jid;
    }

    private readonly float _s;
    private int _hover = -1;

    public FriendList(float scale)
    {
        _s = scale;
        DrawMode = DrawMode.OwnerDrawFixed;
        ItemHeight = Math.Min(255, D(46));
        BorderStyle = BorderStyle.None;
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        IntegralHeight = false;
        SelectionMode = SelectionMode.None;
        Font = Theme.Body;
    }

    public event EventHandler<Row>? RowClicked;

    private int D(float px) => (int)Math.Round(px * _s);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.UseDarkScrollbars(Handle);
    }

    public void SetRows(List<Row> rows)
    {
        var top = Items.Count > 0 ? TopIndex : 0;
        var same = rows.Count == Items.Count && rows.Select((r, i) => Same(r, (Row)Items[i])).All(x => x);
        if (same)
        {
            // Just refresh state so we don't jump around.
            for (var i = 0; i < rows.Count; i++)
                Items[i] = rows[i];
            Invalidate();
            return;
        }

        BeginUpdate();
        Items.Clear();
        foreach (var row in rows)
            Items.Add(row);
        if (Items.Count > 0)
            TopIndex = Math.Min(top, Items.Count - 1);
        EndUpdate();
    }

    private static bool Same(Row a, Row b) => a.Key == b.Key && a.State == b.State && a.Checked == b.Checked && a.Name == b.Name && a.Tag == b.Tag;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var i = IndexFromPoint(e.Location);
        if (i == NoMatches) i = -1;
        if (i == _hover) return;
        InvalidateRow(_hover);
        _hover = i;
        InvalidateRow(_hover);
        Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        InvalidateRow(_hover);
        _hover = -1;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        var i = IndexFromPoint(e.Location);
        if (i >= 0 && i < Items.Count)
            RowClicked?.Invoke(this, (Row)Items[i]);
    }

    private void InvalidateRow(int i)
    {
        if (i >= 0 && i < Items.Count)
            Invalidate(GetItemRectangle(i));
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        using var brush = new SolidBrush(BackColor);
        pevent.Graphics.FillRectangle(brush, ClientRectangle);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count) return;
        var row = (Row)Items[e.Index];
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var bg = new SolidBrush(BackColor))
            g.FillRectangle(bg, e.Bounds);

        var inner = new Rectangle(e.Bounds.X + D(4), e.Bounds.Y + D(2), e.Bounds.Width - D(8), e.Bounds.Height - D(4));
        var hover = e.Index == _hover;
        if (hover)
            Theme.FillRound(g, Theme.Hover, inner, D(8));

        var boxSize = D(18);
        var box = new RectangleF(inner.X + D(10), inner.Y + (inner.Height - boxSize) / 2f, boxSize, boxSize);
        DrawCheckbox(g, box, row.Checked, hover, _s);

        // Status on the right
        var statusText = row.Missing ? "Not on friends list" : Theme.StateText(row.State);
        var statusColor = row.Missing ? Theme.Warn : Theme.StateColor(row.State);
        var right = inner.Right - D(12);
        if (statusText.Length > 0)
        {
            var size = TextRenderer.MeasureText(statusText, Theme.Small, Size.Empty, Theme.Measure);
            var textRect = new Rectangle(right - size.Width, inner.Y, size.Width, inner.Height);
            TextRenderer.DrawText(g, statusText, Theme.Small, textRect, row.Missing ? Theme.Warn : Theme.Muted, Theme.RightMiddle);
            right = textRect.Left - D(7);
            if (!row.Missing)
                Theme.Dot(g, statusColor, right - D(3.5f), inner.Y + inner.Height / 2f, D(7));
            right -= D(7) + D(12);
        }

        // Name + #tag
        var x = (int)box.Right + D(12);
        var available = Math.Max(0, right - x);
        var tag = row.Tag.Length > 0 ? "#" + row.Tag : "";
        var tagSize = tag.Length > 0 ? TextRenderer.MeasureText(tag, Theme.Body, Size.Empty, Theme.Measure) : Size.Empty;
        var nameSize = TextRenderer.MeasureText(row.Name, Theme.BodyBold, Size.Empty, Theme.Measure);
        var nameWidth = Math.Min(nameSize.Width + D(2), Math.Max(0, available - tagSize.Width - D(2)));

        var nameColor = row.State == FriendDirectory.OnlineState.Offline && !row.Missing ? Hex("#C5CAD4") : Theme.Text;
        TextRenderer.DrawText(g, row.Name, Theme.BodyBold, new Rectangle(x, inner.Y, nameWidth, inner.Height), nameColor, Theme.LeftMiddle);
        if (tag.Length > 0 && available - nameWidth > D(8))
            TextRenderer.DrawText(g, tag, Theme.Body, new Rectangle(x + nameWidth + D(2), inner.Y, available - nameWidth - D(2), inner.Height), Theme.Faint, Theme.LeftMiddle);
    }

    internal static void DrawCheckbox(Graphics g, RectangleF box, bool isChecked, bool hover, float s)
    {
        var radius = 5 * s;
        if (isChecked)
        {
            Theme.FillRound(g, hover ? Theme.AccentHover : Theme.Accent, box, radius);
            using var pen = new Pen(Color.White, Math.Max(1.8f, 2f * s)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, new[]
            {
                new PointF(box.X + box.Width * 0.26f, box.Y + box.Height * 0.53f),
                new PointF(box.X + box.Width * 0.44f, box.Y + box.Height * 0.70f),
                new PointF(box.X + box.Width * 0.76f, box.Y + box.Height * 0.32f)
            });
        }
        else
        {
            Theme.StrokeRound(g, hover ? Theme.Muted : Theme.Faint, box, radius, Math.Max(1.2f, 1.5f * s));
        }
    }

    private static Color Hex(string h) => ColorTranslator.FromHtml(h);
}

internal static class Native
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

    public static void UseDarkTitleBar(IntPtr hwnd, Color caption)
    {
        try
        {
            var on = 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0) // DWMWA_USE_IMMERSIVE_DARK_MODE
                DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int)); // older Windows 10 builds
            var colorRef = caption.R | (caption.G << 8) | (caption.B << 16);
            DwmSetWindowAttribute(hwnd, 35, ref colorRef, sizeof(int)); // DWMWA_CAPTION_COLOR (Windows 11)
        }
        catch
        {
            // Not supported on this Windows version.
        }
    }

    public static void UseDarkScrollbars(IntPtr hwnd)
    {
        try { SetWindowTheme(hwnd, "DarkMode_Explorer", null); }
        catch { /* not supported */ }
    }

    /// <summary>Flashes the taskbar button until the window is focused. Never steals focus.</summary>
    public static void FlashTaskbar(IntPtr hwnd)
    {
        try
        {
            var info = new NativeFlash.FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf(typeof(NativeFlash.FLASHWINFO)),
                hwnd = hwnd,
                dwFlags = 0x2 | 0xC, // FLASHW_TRAY | FLASHW_TIMERNOFG
                uCount = uint.MaxValue,
                dwTimeout = 0
            };
            NativeFlash.FlashWindowEx(ref info);
        }
        catch
        {
            // Not supported.
        }
    }

    public static void SetCueBanner(IntPtr hwnd, string text)
    {
        try { SendMessage(hwnd, 0x1501, (IntPtr)1, text); } // EM_SETCUEBANNER
        catch { /* not supported */ }
    }
}
