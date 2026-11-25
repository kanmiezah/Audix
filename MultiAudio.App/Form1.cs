using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MultiAudio.App;

/// <summary>A live route = one sink attached to one process capture.</summary>
public sealed record RouteHandle(ProcessCapture Capture, OutputSink Sink, string AppName);

public partial class Form1 : Form
{
    private sealed class Mapping
    {
        public required AudioApp App;
        public required string Scope; // device Id or AllScope
        public List<RouteHandle> Routes = new();
    }

    private const string AllScope = "*ALL*";

    // Segoe Fluent Icons glyphs
    private const string GVolume = "", GMute = "", GSearch = "", GGlobe = "", GHeadphone = "",
        GBluetooth = "", GMonitor = "", GMore = "", GCancel = "";

    private const string GChevron = ""; // ChevronRight, as on the Run dialog's rows
    private const string GSettings = "", GRefresh = "", GBack = "";

    // ---- type ramp (Windows 11: Caption / Body / Body Strong / Subtitle / Title) ----
    internal static readonly Font FCaption = new("Segoe UI", 9f);
    internal static readonly Font FBody = new("Segoe UI", 10.5f);
    internal static readonly Font FBodyStrong = new("Segoe UI Semibold", 10.5f);
    internal static readonly Font FSubtitle = new("Segoe UI Semibold", 15f);
    internal static readonly Font FTitle = new("Segoe UI Semibold", 18f);
    private static readonly Font FIconSm = new("Segoe Fluent Icons", 10f);
    private static readonly Font FIconMd = new("Segoe Fluent Icons", 12f);
    private static readonly Font FIconLg = new("Segoe Fluent Icons", 16f);

    // ---- palette (dynamic) ----
    internal static bool Dark;
    internal static Color ContentBg, CardBg, CardBorder, ControlBorder, Ink, Muted, Accent, OnAccent, HoverBg, InputBg;

    // Frosted, cool-tinted surfaces after the Windows 11 Run dialog. (WinForms can't do real acrylic blur
    // on this OS build, so the "glass" is approximated with tinted solids.)
    private static void UpdatePalette()
    {
        Dark = !ThemeInfo.IsLight();
        if (Dark)
        {
            ContentBg = Color.FromArgb(30, 32, 36);
            CardBg = Color.FromArgb(42, 45, 50);
            CardBorder = Color.FromArgb(55, 59, 66);
            ControlBorder = Color.FromArgb(76, 81, 90);
            Ink = Color.White;
            Muted = Color.FromArgb(198, 203, 211);
            HoverBg = Color.FromArgb(51, 55, 61);
            InputBg = Color.FromArgb(36, 39, 44);
        }
        else
        {
            ContentBg = Color.FromArgb(232, 238, 246);
            CardBg = Color.FromArgb(245, 248, 252);
            CardBorder = Color.FromArgb(222, 229, 239);
            ControlBorder = Color.FromArgb(206, 215, 228);
            Ink = Color.FromArgb(24, 28, 34);
            Muted = Color.FromArgb(82, 90, 102);
            HoverBg = Color.FromArgb(235, 240, 247);
            InputBg = Color.FromArgb(221, 229, 240);
        }
        Accent = ThemeInfo.AccentColor(Dark, CardBg);
        OnAccent = ThemeInfo.OnColor(Accent);
        ToolStripManager.Renderer = new MenuRenderer();
    }

    // ---- state ----
    private readonly Label _status = new();
    private int _sortIndex = 0;
    private TextBox _search = null!;
    private readonly ToolTip _tips = new() { AutoPopDelay = 15000, InitialDelay = 400, ReshowDelay = 200 };
    private NotifyIcon? _tray;

    private RoundedPanel _outputGroup = null!, _appGroup = null!;
    private FlowLayoutPanel _allChips = null!;
    private readonly List<(RoundedPanel Row, FlowLayoutPanel Chips)> _rows = new(); // device rows + the All outputs row
    private readonly Dictionary<string, FlowLayoutPanel> _outputChips = new();
    private Bitmap? _genericApp;

    private readonly List<Panel> _pages = new();
    private int _page;
    private Label _title = null!;
    private FluentButton _settingsBtn = null!, _refreshBtn = null!;

    private readonly Dictionary<int, ProcessCapture> _captures = new();
    private readonly List<Mapping> _mappings = new();
    private List<AudioApp> _allApps = new();
    private List<OutputDevice> _devices = new();

    private int S(int val) => LogicalToDeviceUnits(val);
    private Point P(int x, int y) => new(S(x), S(y));
    private Size Sz(int w, int h) => new(S(w), S(h));
    private Padding Pad(int l, int t, int r, int b) => new(S(l), S(t), S(r), S(b));
    // GDI pads label text by ~1/6 em on the left; pull it back so text lines up with card edges.
    private Point TextAt(Label l, int x, int y) => new(S(x) - l.Font.Height / 6, S(y));

    public Form1()
    {
        InitializeComponent();
        UpdatePalette();
        ClientSize = Sz(960, 640); MinimumSize = Sz(740, 540);
        // Fires for many unrelated settings (mouse, wallpaper…); OnThemeChanged ignores those that don't change the palette.
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, _) => { if (IsHandleCreated) BeginInvoke(new Action(OnThemeChanged)); };
        BuildUi();
        SetupTray();
        Load += (_, _) => Refresh_Click(this, EventArgs.Empty);
        var poll = new System.Windows.Forms.Timer { Interval = 3000 };
        poll.Tick += (_, _) => PollAudio();
        poll.Start();
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) HideToTray(); };
        FormClosing += OnClosing;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ThemeInfo.ApplyTitleBar(Handle, Dark);
    }

    private void OnThemeChanged()
    {
        var (wasDark, oldAccent) = (Dark, Accent);
        UpdatePalette();
        if (Dark == wasDark && Accent == oldAccent) return; // nothing visible changed: skip the rebuild (and its flicker)

        ThemeInfo.ApplyTitleBar(Handle, Dark);
        int oldPage = _page;
        string oldSearch = _search?.Text ?? "";

        SuspendLayout();
        _status.Parent?.Controls.Remove(_status); // reused across rebuilds; keep it out of the disposal below
        DisposeChildren(this);
        _pages.Clear();
        _rows.Clear();
        _outputChips.Clear();

        BuildUi();
        ShowPage(oldPage);
        _search!.Text = oldSearch;
        ResumeLayout();
        Refresh_Click(this, EventArgs.Empty);
    }

    // ---------------- small builders ----------------

    private static Label Lbl(string text, Font font, Color color) => new()
    {
        Text = text, Font = font, ForeColor = color, AutoSize = true, UseMnemonic = false,
    };

    private static Label Glyph(string g, Font font, Color color) => Lbl(g, font, color);

    private FluentButton Btn(string text, EventHandler onClick, string? widestText = null)
    {
        var b = new FluentButton
        {
            Text = text, Font = FBody, ForeColor = Ink, BackColor = CardBg, HoverColor = HoverBg, BorderColor = CardBorder,
            FocusColor = Ink, Radius = S(6), Margin = Pad(8, 0, 0, 0),
        };
        b.Size = new Size(TextRenderer.MeasureText(widestText ?? text, FBody).Width + S(28), S(32));
        b.Click += onClick;
        return b;
    }

    /// <summary>Borderless icon-only button; <paramref name="name"/> is what screen readers announce.</summary>
    private FluentButton GlyphBtn(string glyph, string name)
    {
        var b = new FluentButton
        {
            Text = glyph, Font = FIconSm, ForeColor = Ink, HoverColor = HoverBg, NoBorder = true, FocusColor = Ink,
            Radius = S(4), Size = Sz(32, 32), AccessibleName = name,
        };
        _tips.SetToolTip(b, name);
        return b;
    }

    private Label SectionHeading(string text, int top = 16)
    {
        var l = Lbl(text, FBodyStrong, Ink);
        l.Margin = Pad(0, top, 0, 4);
        return l;
    }

    private static string DeviceGlyph(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("head")) return GHeadphone;
        if (n.Contains("blue") || n.Contains(" bt")) return GBluetooth;
        if (n.Contains("monitor") || n.Contains("hdmi") || n.Contains("display") || n.Contains("nvidia") || n.Contains("tv")) return GMonitor;
        return GVolume;
    }

    private static Bitmap MakeGenericAppIcon()
    {
        var bmp = new Bitmap(40, 40); using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.Transparent);
        using var br = new SolidBrush(Color.FromArgb(110, Muted));
        using var path = RoundedPanel.RoundedRect(new Rectangle(6, 6, 28, 28), 6);
        g.FillPath(br, path);
        return bmp;
    }

    private readonly Dictionary<int, string> _appNames = new();

    /// <summary>"Google Chrome" rather than "chrome": the exe's FileDescription, else the process name.</summary>
    private string AppName(AudioApp a)
    {
        if (_appNames.TryGetValue(a.ProcessId, out var cached)) return cached;
        string name = a.ProcessName;
        try
        {
            var desc = Process.GetProcessById(a.ProcessId).MainModule?.FileVersionInfo.FileDescription?.Trim();
            if (!string.IsNullOrEmpty(desc)) name = desc;
        }
        catch { } // elevated or exited process: keep the process name
        return _appNames[a.ProcessId] = name;
    }

    private Bitmap AppIcon(int pid, int size)
    {
        try
        {
            var file = Process.GetProcessById(pid).MainModule?.FileName;
            if (file != null) { using var ic = Icon.ExtractAssociatedIcon(file); if (ic != null) return new Bitmap(ic.ToBitmap(), size, size); }
        }
        catch { }
        return new Bitmap(_genericApp!, size, size);
    }

    // ---------------- layout ----------------

    private void BuildUi()
    {
        Font = FBody;
        BackColor = ContentBg; Text = "Audio Router";
        _genericApp = MakeGenericAppIcon();

        var content = new Panel { Dock = DockStyle.Fill, BackColor = ContentBg, Padding = Pad(32, 20, 32, 0) };

        // Run-dialog style header: just the title, with quiet icon buttons on the right.
        var header = new Panel { Dock = DockStyle.Top, Height = S(52) };
        _title = Lbl("Routing", FTitle, Ink); _title.Location = TextAt(_title, 0, 0);
        var headBtns = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        FluentButton HeadBtn(string glyph, string name, EventHandler onClick)
        {
            var b = GlyphBtn(glyph, name);
            b.Font = FIconMd; b.Size = Sz(36, 36); b.Margin = Pad(4, 0, 0, 0);
            b.Click += onClick;
            return b;
        }
        _settingsBtn = HeadBtn(GSettings, "Settings", (_, _) => ShowPage(_page == 0 ? 1 : 0));
        _refreshBtn = HeadBtn(GRefresh, "Refresh", Refresh_Click);
        headBtns.Controls.Add(_settingsBtn); headBtns.Controls.Add(_refreshBtn);
        header.Controls.Add(headBtns); header.Controls.Add(_title);

        var pageHost = new Panel { Dock = DockStyle.Fill };
        var routing = BuildRouting();
        var settings = BuildSettings();
        _pages.Add(routing); _pages.Add(settings);
        pageHost.Controls.Add(routing); pageHost.Controls.Add(settings);

        // Docking is laid out last-added-outermost, so the Fill control goes in first.
        content.Controls.Add(pageHost);
        content.Controls.Add(header);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = S(32), BackColor = ContentBg, Padding = Pad(32, 0, 32, 0) };
        _status.Dock = DockStyle.Fill; _status.AutoSize = false; _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Font = FCaption; _status.ForeColor = Muted; _status.BackColor = ContentBg; _status.UseMnemonic = false; _status.AutoEllipsis = true;
        footer.Controls.Add(_status);

        Controls.Add(content);
        Controls.Add(footer);

        ShowPage(0);
    }

    private void ShowPage(int i)
    {
        _page = i;
        for (int j = 0; j < _pages.Count; j++) _pages[j].Visible = j == i;
        _title.Text = i == 0 ? "Routing" : "Settings";
        _refreshBtn.Visible = i == 0;
        // On the Settings page the gear becomes a back arrow.
        _settingsBtn.Text = i == 0 ? GSettings : GBack;
        _settingsBtn.AccessibleName = i == 0 ? "Settings" : "Back to routing";
        _tips.SetToolTip(_settingsBtn, _settingsBtn.AccessibleName);
    }

    private Panel BuildRouting()
    {
        // The whole page scrolls (not just the app list), so nothing gets clipped when routes make the top taller.
        var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = Pad(0, 0, 12, 16) };
        page.HandleCreated += (_, _) => ThemeInfo.ApplyScrollbars(page.Handle, Dark);

        // Small section labels over grouped lists, like "Recent commands" in the Run dialog.
        Panel Label(string text, int height, int top)
        {
            var p = new Panel { Dock = DockStyle.Top, Height = S(height) };
            p.Controls.Add(Lbl(text, FBody, Ink).Also(l => l.Location = TextAt(l, 0, top)));
            return p;
        }

        // ===== Outputs =====
        var outLabel = Label("Outputs", 32, 4);
        // The how-to that used to be a subtitle under the title, now a quiet caption beside the section it explains.
        var hint = Lbl("Drag an app onto a device to play it there, or use an app's › menu", FCaption, Muted);
        hint.Location = TextAt(hint, 0, 6);
        hint.Left += TextRenderer.MeasureText("Outputs", FBody).Width + S(10);
        outLabel.Controls.Add(hint);
        _outputGroup = Group();
        _outputGroup.SizeChanged += (_, _) => FitGroups();

        // ===== Apps =====
        var appHead = Label("Apps", 64, 26); // vertically centred on the search box beside it
        var tools = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = Pad(0, 20, 0, 0) };

        var sortMenu = new ContextMenuStrip();
        FluentButton sortBtn = null!;
        sortBtn = Btn(SortLabel(), (_, _) => sortMenu.Show(sortBtn, new Point(0, sortBtn.Height)), "Sort: Name Z–A");
        sortBtn.AccessibleName = "Sort apps";
        void SetSort(int i) { _sortIndex = i; sortBtn.Text = SortLabel(); RebuildAppCards(); }
        sortMenu.Items.Add("Name A–Z", null, (_, _) => SetSort(0));
        sortMenu.Items.Add("Name Z–A", null, (_, _) => SetSort(1));

        // Soft filled field with no outline, like the Run dialog's input; an accent ring appears on focus.
        var searchBox = new RoundedPanel { Size = Sz(240, 32), Radius = S(6), BackColor = InputBg, BorderColor = InputBg, Margin = Pad(8, 0, 0, 0) };
        searchBox.Controls.Add(Glyph(GSearch, FIconSm, Muted).Also(l => l.Location = P(10, 8)));
        _search = new TextBox
        {
            BorderStyle = BorderStyle.None, Location = P(34, 7), Width = S(196), Font = FBody, BackColor = InputBg, ForeColor = Ink,
            PlaceholderText = "Search apps", AccessibleName = "Search apps",
        };
        _search.TextChanged += (_, _) => RebuildAppCards();
        _search.GotFocus += (_, _) => { searchBox.BorderColor = Accent; searchBox.BorderThickness = 2; searchBox.Invalidate(); };
        _search.LostFocus += (_, _) => { searchBox.BorderColor = InputBg; searchBox.BorderThickness = 1; searchBox.Invalidate(); };
        searchBox.Controls.Add(_search);

        tools.Controls.Add(sortBtn); tools.Controls.Add(searchBox);
        appHead.Controls.Add(tools);

        _appGroup = Group();
        _appGroup.SizeChanged += (_, _) => FitGroups();

        page.Controls.Add(_appGroup);
        page.Controls.Add(appHead);
        page.Controls.Add(_outputGroup);
        page.Controls.Add(outLabel);
        return page;
    }

    private string SortLabel() => _sortIndex == 1 ? "Sort: Name Z–A" : "Sort: Name A–Z";

    private Panel BuildSettings()
    {
        var page = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Visible = false };
        Label Para(string text) { var l = Lbl(text, FBody, Muted); l.MaximumSize = Sz(600, 0); l.Margin = Pad(0, 0, 0, 12); return l; }

        page.Controls.Add(SectionHeading("App audio", 0));
        page.Controls.Add(Para("Sends every app back to the Windows default output. Use this if an app seems stuck on a device from an earlier override."));
        page.Controls.Add(Btn("Reset all apps to Windows default", (_, _) => ResetAllOverrides()).Also(b => b.Margin = new Padding(FBody.Height / 6, 0, 0, 0)));

        page.Controls.Add(SectionHeading("Saved profiles", 28));
        page.Controls.Add(Para("Routing profiles saved on this PC."));
        page.Controls.Add(Btn("Clear saved profiles", (_, _) => ClearProfiles()).Also(b => b.Margin = new Padding(FBody.Height / 6, 0, 0, 0)));

        page.Controls.Add(SectionHeading("About", 28));
        page.Controls.Add(Para("Audio Router sends each app's sound to the devices you choose. Closing the window keeps it running in the notification area, and your routes stay active."));
        return page;
    }

    // ---------------- grouped lists (styled after the Windows 11 Run dialog) ----------------

    /// <summary>One rounded surface holding stacked rows separated by hairline dividers.</summary>
    private RoundedPanel Group() => new() { Dock = DockStyle.Top, Radius = S(8), BackColor = CardBg, BorderColor = CardBorder, Height = S(64) };

    private void DividerPaint(object? s, PaintEventArgs e)
    {
        var row = (Control)s!;
        using var pen = new Pen(CardBorder);
        e.Graphics.DrawLine(pen, S(16), 1, row.Width - S(16), 1); // y=1: the row's own (hidden) border is painted along y=0
    }

    /// <summary>Stacks a group's rows top to bottom and sizes the group to fit them.</summary>
    private void StackRows(RoundedPanel group)
    {
        // The group draws its 1px outline at x=0, x=W-2, y=0, y=H-2; keep rows strictly inside it, or the
        // outline only shows through at each row's rounded corners (stray marks along the right edge).
        int y = 1, w = group.ClientSize.Width - 3;
        foreach (Control row in group.Controls) { row.SetBounds(1, y, w, row.Height); y += row.Height; }
        group.Height = Math.Max(y + 2, S(16));
    }

    private bool _fitting;
    /// <summary>Wraps each device row's chips to the current width, then restacks both groups.</summary>
    private void FitGroups()
    {
        if (_fitting || _outputGroup == null) return;
        _fitting = true;
        try
        {
            int w = _outputGroup.ClientSize.Width - 3, step = S(220 + 8);
            foreach (var (row, chips) in _rows)
            {
                chips.Width = Math.Max(step, w - S(52 + 16));
                int n = chips.Controls.Count, perLine = Math.Max(1, chips.Width / step);
                int lines = (n + perLine - 1) / perLine;
                chips.Height = lines * S(46);
                row.Height = S(64) + (lines == 0 ? 0 : chips.Height + S(4));
            }
            StackRows(_outputGroup);
            StackRows(_appGroup);
        }
        finally { _fitting = false; }
    }

    /// <summary>A device (or All outputs) row: icon, name, detail line, chevron menu, and the chips playing/routed there.</summary>
    private FlowLayoutPanel BuildDeviceRow(string glyph, Color glyphColor, string title, string sub, string scope, string removeText, bool divider)
    {
        var row = new RoundedPanel { Width = S(640), Height = S(64), Radius = S(6), BackColor = CardBg, BorderColor = CardBg };
        if (divider) row.Paint += DividerPaint;
        var icon = Glyph(glyph, FIconLg, glyphColor); icon.Location = P(16, 18);
        var name = new Label
        {
            Text = title, Font = FBodyStrong, ForeColor = Ink, AutoEllipsis = true, UseMnemonic = false,
            Location = P(52, 12), Size = new Size(row.Width - S(52 + 56), S(22)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        var detail = new Label
        {
            Text = sub, Font = FCaption, ForeColor = Muted, AutoEllipsis = true, UseMnemonic = false,
            Location = P(52, 34), Size = new Size(row.Width - S(52 + 56), S(18)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add(removeText, null, (_, _) => ClearScope(scope));
        row.ContextMenuStrip = menu;
        var more = GlyphBtn(GChevron, $"Options for {title}");
        more.Location = new Point(row.Width - S(44), S(16)); more.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        more.Click += (_, _) => menu.Show(more, new Point(0, more.Height));
        more.Disposed += (_, _) => menu.Dispose();

        var chips = new FlowLayoutPanel { Location = P(52, 60), Size = new Size(row.Width - S(68), 0), FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
        row.Controls.Add(chips); row.Controls.Add(more); row.Controls.Add(detail); row.Controls.Add(name); row.Controls.Add(icon);
        foreach (Control c in new Control[] { row, chips, icon, name, detail }) WireDrop(c, scope);

        _outputGroup.Controls.Add(row);
        _rows.Add((row, chips));
        return chips;
    }

    private static string ShortName(string name) { int i = name.IndexOf('('); return (i > 0 ? name[..i] : name).Trim(); }
    private static string SubName(string name) { int i = name.IndexOf('('), j = name.IndexOf(')'); return i >= 0 && j > i ? name.Substring(i + 1, j - i - 1) : "Audio device"; }

    private RoundedPanel BuildChip(Mapping m, int width)
    {
        string dest = m.Scope == AllScope ? "all outputs" : ShortName(m.Routes.FirstOrDefault()?.Sink.DeviceName ?? "output");
        var chip = new RoundedPanel { Size = new Size(width, S(40)), Radius = S(6), BackColor = InputBg, BorderColor = InputBg, Margin = Pad(0, 0, 8, 6) };
        chip.Controls.Add(new PictureBox { Image = AppIcon(m.App.ProcessId, S(20)), SizeMode = PictureBoxSizeMode.Zoom, Size = Sz(20, 20), Location = P(10, 10) });

        var name = new FluentButton
        {
            Text = AppName(m.App), Font = FBody, ForeColor = Ink, HoverColor = HoverBg, NoBorder = true, FocusColor = Ink, Radius = S(4),
            TextAlign = ContentAlignment.MiddleLeft, Padding = Pad(4, 0, 0, 0),
            Location = P(36, 4), Size = new Size(width - S(36 + 40), S(32)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            AccessibleName = $"{AppName(m.App)} on {dest}. Set delay",
        };
        name.Click += (_, _) => ShowDelayPopup(m, chip);
        _tips.SetToolTip(name, "Set delay to sync this app with your other outputs");

        var x = GlyphBtn(GCancel, $"Remove {AppName(m.App)} from {dest}");
        x.Location = new Point(width - S(36), S(4)); x.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        x.Click += (_, _) => RemoveMapping(m);

        chip.Controls.Add(x); chip.Controls.Add(name);
        return chip;
    }

    private void ShowDelayPopup(Mapping m, Control anchor)
    {
        int cur = m.Routes.FirstOrDefault()?.Sink.DelayMs ?? 0;
        string dest = m.Scope == AllScope ? "All outputs" : (m.Routes.FirstOrDefault()?.Sink.DeviceName ?? "output");

        var pop = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Size = Sz(320, 104), ShowInTaskbar = false, BackColor = CardBg, Font = FBody, KeyPreview = true };
        int rad = S(8);
        using (var clip = RoundedPanel.RoundedRect(new Rectangle(0, 0, pop.Width, pop.Height), rad)) pop.Region = new Region(clip);
        pop.Paint += (s, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(ControlBorder);
            using var path = RoundedPanel.RoundedRect(new Rectangle(0, 0, pop.Width - 1, pop.Height - 1), rad);
            e.Graphics.DrawPath(pen, path);
        };

        // Two rows: who/where on top, slider + live value below, so a long name never collides with the value.
        var title = new Label { Text = $"Delay for {AppName(m.App)} → {dest}", Location = P(14, 12), Size = Sz(292, 22), AutoEllipsis = true, ForeColor = Ink, Font = FBodyStrong, UseMnemonic = false };
        var tb = new TrackBar
        {
            Minimum = 0, Maximum = 500, TickFrequency = 50, SmallChange = 5, LargeChange = 50, Value = Math.Clamp(cur, 0, 500),
            Location = P(8, 44), Size = Sz(232, 45), BackColor = CardBg, AccessibleName = $"Delay for {AppName(m.App)} in milliseconds",
        };
        var val = new Label { Text = cur + " ms", Location = P(240, 50), Size = Sz(66, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = Accent, Font = FBodyStrong, UseMnemonic = false };
        tb.ValueChanged += (_, _) => { val.Text = tb.Value + " ms"; foreach (var h in m.Routes) h.Sink.DelayMs = tb.Value; SetStatus($"{AppName(m.App)} → {dest}: delay {tb.Value} ms"); };
        pop.Controls.Add(val); pop.Controls.Add(title); pop.Controls.Add(tb);

        pop.Location = anchor.PointToScreen(new Point(0, anchor.Height + S(2)));
        pop.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) pop.Close(); };
        pop.Deactivate += (_, _) => pop.Close();
        pop.Shown += (_, _) => tb.Focus();
        pop.Show(this);
    }

    private ContextMenuStrip SendToMenu(AudioApp a)
    {
        var menu = new ContextMenuStrip();
        foreach (var d in _devices) { var id = d.Id; menu.Items.Add(d.Name, null, (_, _) => CreateMapping(a, id)); }
        if (_devices.Count > 0) menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("All outputs", null, (_, _) => CreateMapping(a, AllScope));
        return menu;
    }

    /// <summary>Read-only chip in a device card: this app is playing on this device right now (its own output, not a route we added).</summary>
    private RoundedPanel BuildPlayingChip(AudioApp a, int width)
    {
        var chip = new RoundedPanel { Size = new Size(width, S(40)), Radius = S(6), BackColor = InputBg, BorderColor = InputBg, Margin = Pad(0, 0, 8, 6) };
        var pic = new PictureBox { Image = AppIcon(a.ProcessId, S(20)), SizeMode = PictureBoxSizeMode.Zoom, Size = Sz(20, 20), Location = P(10, 10) };
        bool active = a.State.EndsWith("Active");
        var state = Lbl(active ? "Playing" : "Idle", FCaption, active ? Accent : Muted);
        state.Location = new Point(width - S(12 + 56), S(12)); state.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        var name = new Label
        {
            Text = AppName(a), Font = FBody, ForeColor = Ink, AutoEllipsis = true, UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft,
            Location = P(38, 4), Size = new Size(width - S(38 + 12 + 60), S(32)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, // inset so it doesn't cover the chip border
        };
        chip.Controls.Add(state); chip.Controls.Add(name); chip.Controls.Add(pic);
        return chip;
    }

    /// <summary>One app as a list row: icon, name, the default-speaker toggle, and a chevron for "send to".</summary>
    private RoundedPanel BuildAppRow(AudioApp a, bool divider)
    {
        int w = S(640);
        var row = new RoundedPanel { Width = w, Height = S(56), Radius = S(6), BackColor = CardBg, HoverBackColor = HoverBg, BorderColor = CardBg, Cursor = Cursors.Hand };
        if (divider) row.Paint += DividerPaint;
        var pic = new PictureBox { Image = AppIcon(a.ProcessId, S(24)), SizeMode = PictureBoxSizeMode.Zoom, Size = Sz(24, 24), Location = P(16, 16) };
        var name = new Label
        {
            Text = AppName(a), Font = FBody, ForeColor = Ink, AutoEllipsis = true, UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft,
            Location = P(52, 8), Size = new Size(w - S(52 + 230), S(40)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        var menu = SendToMenu(a);
        row.ContextMenuStrip = menu;
        var send = GlyphBtn(GChevron, $"Send {AppName(a)} to an output");
        send.Location = new Point(w - S(44), S(12)); send.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        send.Click += (_, _) => menu.Show(send, new Point(0, send.Height));
        send.Disposed += (_, _) => menu.Dispose();

        bool muted; try { muted = AudioInventory.GetAppMuted(a.ProcessId); } catch { muted = false; }
        var toggle = new ToggleSwitch
        {
            OnColor = Accent, OffColor = Muted, KnobColor = OnAccent, FocusColor = Ink, Size = Sz(44, 22),
            Location = new Point(w - S(44 + 60), S(17)), Anchor = AnchorStyles.Top | AnchorStyles.Right, On = !muted,
            AccessibleName = $"Play {AppName(a)} on the default speaker",
        };
        var caption = Lbl("Speaker", FCaption, Muted); caption.Location = new Point(w - S(44 + 60 + 66), S(19)); caption.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        var spk = Glyph(muted ? GMute : GVolume, FIconSm, muted ? Muted : Accent); spk.Location = new Point(w - S(44 + 60 + 66 + 22), S(20)); spk.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        toggle.Toggled += (_, _) => { try { AudioInventory.SetAppMuted(a.ProcessId, !toggle.On); } catch { } spk.Text = toggle.On ? GVolume : GMute; spk.ForeColor = toggle.On ? Accent : Muted; SetStatus($"{AppName(a)}: system speaker {(toggle.On ? "on" : "off")}"); };
        _tips.SetToolTip(toggle, "Also play on the system default speaker");

        row.Controls.Add(send); row.Controls.Add(toggle); row.Controls.Add(caption); row.Controls.Add(spk); row.Controls.Add(name); row.Controls.Add(pic);
        WireDrag(row, a, row, pic, name);
        return row;
    }

    private void WireDrop(Control target, string scope)
    {
        target.AllowDrop = true;
        var host = target as RoundedPanel ?? target.Parent as RoundedPanel ?? target.Parent?.Parent as RoundedPanel;
        if (host == null) return;
        var normal = host.BorderColor;
        void Hi(bool on) { host.BorderThickness = on ? 2 : 1; host.BorderColor = on ? Accent : normal; host.Invalidate(); }
        target.DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(typeof(AudioApp)) == true ? DragDropEffects.Copy : DragDropEffects.None;
        target.DragOver += (_, e) => { if (e.Data?.GetDataPresent(typeof(AudioApp)) == true) { e.Effect = DragDropEffects.Copy; Hi(true); } };
        target.DragLeave += (_, _) => Hi(false);
        target.DragDrop += (_, e) => { Hi(false); if (e.Data?.GetData(typeof(AudioApp)) is AudioApp app) CreateMapping(app, scope); };
    }

    private void WireDrag(RoundedPanel card, AudioApp app, params Control[] handles)
    {
        Point down = Point.Empty; bool dragging = false;
        void Down(object? s, MouseEventArgs e) { down = e.Location; dragging = false; }
        void Move(object? s, MouseEventArgs e) { if (e.Button == MouseButtons.Left && !dragging && (Math.Abs(e.X - down.X) > 4 || Math.Abs(e.Y - down.Y) > 4)) { dragging = true; _dragging = true; try { card.DoDragDrop(app, DragDropEffects.Copy); } finally { _dragging = false; } } }
        foreach (var h in handles) { h.MouseDown += Down; h.MouseMove += Move; }
    }

    // ---------------- mapping model ----------------

    private void CreateMapping(AudioApp app, string scope, int volPct = 100, int delayMs = 0)
    {
        if (_mappings.Any(m => m.App.ProcessId == app.ProcessId && m.Scope == scope)) { SetStatus($"{AppName(app)} is already on that output."); return; }
        var m = new Mapping { App = app, Scope = scope };
        if (scope == AllScope)
            foreach (var d in AudioInventory.GetOutputDevices()) m.Routes.Add(AddRoute(app.ProcessId, AppName(app), d.Id, d.Name, volPct, delayMs));
        else
        {
            var dev = AudioInventory.GetOutputDevices().FirstOrDefault(d => d.Id == scope);
            if (dev == null) { SetStatus("That device is no longer available."); return; }
            m.Routes.Add(AddRoute(app.ProcessId, AppName(app), dev.Id, dev.Name, volPct, delayMs));
        }
        _mappings.Add(m);
        RenderChips();
        SetStatus($"{AppName(app)} now plays on {(scope == AllScope ? "all outputs" : m.Routes[0].Sink.DeviceName)}");
    }

    private void RemoveMapping(Mapping m)
    {
        foreach (var h in m.Routes) StopRouteHandle(h);
        _mappings.Remove(m);
        RenderChips();
        SetStatus($"Removed {AppName(m.App)}");
    }

    private void ClearScope(string scope)
    {
        foreach (var m in _mappings.Where(m => m.Scope == scope).ToArray()) { foreach (var h in m.Routes) StopRouteHandle(h); _mappings.Remove(m); }
        RenderChips();
    }

    private void RenderChips()
    {
        foreach (var panel in _outputChips.Values) DisposeChildren(panel);
        DisposeChildren(_allChips);
        foreach (var a in _allApps.OrderBy(AppName))
            if (_outputChips.TryGetValue(a.DeviceId, out var here)) here.Controls.Add(BuildPlayingChip(a, S(220)));
        foreach (var m in _mappings)
        {
            var container = m.Scope == AllScope ? _allChips : _outputChips.GetValueOrDefault(m.Scope);
            container?.Controls.Add(BuildChip(m, S(220)));
        }
        FitGroups();
        SetStatus($"{Plural(_outputChips.Count, "output")} · {Plural(_allApps.Count, "app")} · {Plural(_mappings.Count, "route")}");
    }

    /// <summary>Clears <paramref name="parent"/> and frees what Controls.Clear() leaks: window handles and icon bitmaps.
    /// Disposal waits one message so a button can remove itself from its own Click handler safely.</summary>
    private void DisposeChildren(Control parent)
    {
        var old = parent.Controls.Cast<Control>().ToArray();
        parent.Controls.Clear();
        if (old.Length == 0) return;
        void Free(Control c)
        {
            foreach (Control child in c.Controls) Free(child);
            if (c is PictureBox { Image: { } img }) { c.Disposed += (_, _) => img.Dispose(); }
        }
        foreach (var c in old) Free(c);
        BeginInvoke(new Action(() => { foreach (var c in old) c.Dispose(); }));
    }

    private static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";

    private RouteHandle AddRoute(int pid, string appName, string deviceId, string deviceName, int volPct, int delayMs)
    {
        if (!_captures.TryGetValue(pid, out var cap)) { cap = new ProcessCapture(pid, appName, SetStatus); cap.Start(); _captures[pid] = cap; }
        var sink = new OutputSink(deviceId, deviceName);
        cap.AddSink(sink);
        sink.Volume = volPct / 100f; sink.DelayMs = delayMs;
        return new RouteHandle(cap, sink, appName);
    }

    private void StopRouteHandle(RouteHandle h)
    {
        h.Capture.RemoveSink(h.Sink);
        if (h.Capture.SinkCount == 0) { h.Capture.Stop(); _captures.Remove(h.Capture.ProcessId); }
    }

    private void StopAll()
    {
        foreach (var cap in _captures.Values.ToArray()) cap.Stop();
        _captures.Clear(); _mappings.Clear();
        if (IsHandleCreated && _allChips != null) RenderChips();
    }

    // ---------------- discovery ----------------

    private void Refresh_Click(object? sender, EventArgs e)
    {
        try
        {
            _devices = AudioInventory.GetOutputDevices();

            DisposeChildren(_outputGroup); _outputChips.Clear(); _rows.Clear();
            for (int i = 0; i < _devices.Count; i++)
            {
                var d = _devices[i];
                _outputChips[d.Id] = BuildDeviceRow(DeviceGlyph(d.Name), d.IsDefault ? Accent : Ink, ShortName(d.Name),
                    (d.IsDefault ? "Default · " : "") + SubName(d.Name), d.Id, "Remove all apps from this device", divider: i > 0);
            }
            _allChips = BuildDeviceRow(GGlobe, Ink, "All outputs", "Plays on every device at once", AllScope, "Remove all apps from all outputs", divider: _devices.Count > 0);

            _allApps = AudioInventory.GetActiveAudioApps();
            RebuildAppCards();
            RenderChips();
        }
        catch (Exception ex) { SetStatus("Error: " + ex.Message); }
    }

    private bool _dragging;

    /// <summary>Picks up new/closed apps and plugged/unplugged devices without a manual Refresh.
    /// Rebuilds only when something changed, and never mid-drag (that would dispose the drag source).</summary>
    private void PollAudio()
    {
        if (!Visible || _page != 0 || _dragging) return;
        try
        {
            static string DevKey(IEnumerable<OutputDevice> ds) => string.Join("|", ds.Select(d => d.Id + (d.IsDefault ? "*" : "")));
            static string AppKey(IEnumerable<AudioApp> xs) => string.Join("|", xs.Select(a => $"{a.ProcessId}@{a.DeviceId}:{a.State}").OrderBy(k => k));
            if (DevKey(AudioInventory.GetOutputDevices()) != DevKey(_devices) || AppKey(AudioInventory.GetActiveAudioApps()) != AppKey(_allApps))
                Refresh_Click(this, EventArgs.Empty);
        }
        catch { } // a device vanishing mid-enumeration: try again next tick
    }

    private void RebuildAppCards()
    {
        if (_appGroup == null) return;
        var q = (_search?.Text ?? "").Trim();
        IEnumerable<AudioApp> apps = _allApps;
        if (q.Length > 0) apps = apps.Where(a => AppName(a).Contains(q, StringComparison.OrdinalIgnoreCase) || a.ProcessName.Contains(q, StringComparison.OrdinalIgnoreCase));
        apps = _sortIndex == 1 ? apps.OrderByDescending(a => AppName(a)) : apps.OrderBy(a => AppName(a));

        DisposeChildren(_appGroup);
        var list = apps.ToList();
        for (int i = 0; i < list.Count; i++) _appGroup.Controls.Add(BuildAppRow(list[i], divider: i > 0));
        if (list.Count == 0)
            _appGroup.Controls.Add(new Label
            {
                Text = q.Length > 0 ? "No apps match your search." : "No apps are playing sound yet. They'll appear here when they start.",
                AutoSize = false, Height = S(56), TextAlign = ContentAlignment.MiddleLeft, Padding = Pad(16, 0, 16, 0), ForeColor = Muted, Font = FBody, UseMnemonic = false,
            });
        FitGroups();
    }

    // ---------------- tray ----------------

    private void SetupTray()
    {
        Icon? ico = null;
        try { ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        if (ico != null) Icon = ico;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show Audio Router", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Stop all routes", null, (_, _) => StopAll());
        menu.Items.Add("Reset app audio to Windows default", null, (_, _) => ResetAllOverrides());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { _forceExit = true; Close(); });
        _tray = new NotifyIcon { Icon = ico ?? SystemIcons.Application, Text = "Audio Router", Visible = true, ContextMenuStrip = menu };
        _tray.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void HideToTray() { Hide(); ShowInTaskbar = false; _tray?.ShowBalloonTip(1500, "Audio Router", "Still running — routes stay active. Double-click to reopen.", ToolTipIcon.Info); }
    private void RestoreFromTray() { Show(); WindowState = FormWindowState.Normal; ShowInTaskbar = true; Activate(); }

    private bool _forceExit;
    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_forceExit && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; HideToTray(); return; }
        StopAll();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
    }

    private void ResetAllOverrides()
    {
        if (!Prompt.Confirm(this, "Reset app audio",
                "Send every app back to the Windows default output?\n\nApps that are playing may need their playback restarted to pick up the change.",
                "Reset")) return;
        try
        {
            var diag = AppEndpointRouter.ClearAll();
            SetStatus("All apps reset to the Windows default output. " + diag);
        }
        catch (Exception ex) { SetStatus("Reset failed: " + ex.Message); }
    }

    private void ClearProfiles()
    {
        int n = ProfileStore.LoadAll().Count;
        if (n == 0) { SetStatus("There are no saved profiles to clear."); return; }
        if (!Prompt.Confirm(this, "Clear saved profiles", $"Clear {Plural(n, "saved profile")}? This can't be undone.", "Clear")) return;
        ProfileStore.SaveAll(new());
        SetStatus($"Cleared {Plural(n, "saved profile")}.");
    }

    private void SetStatus(string text)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => _status.Text = text)); return; }
        _status.Text = text;
    }
}

/// <summary>Themes every ContextMenuStrip (they use ToolStripManager.Renderer by default).</summary>
internal sealed class MenuRenderer : ToolStripProfessionalRenderer
{
    public MenuRenderer() : base(new MenuColors()) { RoundedEdges = false; }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Form1.Ink : Form1.Muted;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Form1.Ink;
        base.OnRenderArrow(e);
    }

    private sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Form1.CardBg;
        public override Color ImageMarginGradientBegin => Form1.CardBg;
        public override Color ImageMarginGradientMiddle => Form1.CardBg;
        public override Color ImageMarginGradientEnd => Form1.CardBg;
        public override Color MenuBorder => Form1.CardBorder;
        public override Color MenuItemBorder => Form1.HoverBg;
        public override Color MenuItemSelected => Form1.HoverBg;
        public override Color MenuItemSelectedGradientBegin => Form1.HoverBg;
        public override Color MenuItemSelectedGradientEnd => Form1.HoverBg;
        public override Color SeparatorDark => Form1.CardBorder;
        public override Color SeparatorLight => Form1.CardBorder;
    }
}

internal static class ControlExt
{
    public static T Also<T>(this T c, Action<T> f) where T : Control { f(c); return c; }
}
