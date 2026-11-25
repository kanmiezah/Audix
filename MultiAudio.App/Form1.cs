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

    // ---- type ramp (Windows 11: Caption / Body / Body Strong / Subtitle / Title) ----
    internal static readonly Font FCaption = new("Segoe UI", 9f);
    internal static readonly Font FBody = new("Segoe UI", 10.5f);
    internal static readonly Font FBodyStrong = new("Segoe UI Semibold", 10.5f);
    internal static readonly Font FSubtitle = new("Segoe UI Semibold", 15f);
    internal static readonly Font FTitle = new("Segoe UI Semibold", 21f);
    private static readonly Font FIconSm = new("Segoe Fluent Icons", 10f);
    private static readonly Font FIconLg = new("Segoe Fluent Icons", 16f);

    // ---- palette (dynamic) ----
    internal static bool Dark;
    internal static Color ContentBg, CardBg, CardBorder, ControlBorder, Ink, Muted, Accent, OnAccent, HoverBg;

    private static void UpdatePalette()
    {
        Dark = !ThemeInfo.IsLight();
        if (Dark)
        {
            ContentBg = Color.FromArgb(32, 32, 32);
            CardBg = Color.FromArgb(43, 43, 43);
            CardBorder = Color.FromArgb(58, 58, 58);
            ControlBorder = Color.FromArgb(84, 84, 84);
            Ink = Color.White;
            Muted = Color.FromArgb(200, 200, 200);
            HoverBg = Color.FromArgb(52, 52, 52);
        }
        else
        {
            ContentBg = Color.FromArgb(243, 243, 243);
            CardBg = Color.FromArgb(251, 251, 251);
            CardBorder = Color.FromArgb(229, 229, 229);
            ControlBorder = Color.FromArgb(209, 209, 209);
            Ink = Color.FromArgb(26, 26, 26);
            Muted = Color.FromArgb(96, 96, 96);
            HoverBg = Color.FromArgb(245, 245, 245);
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

    private FlowLayoutPanel _outputRow = null!, _appRow = null!, _allChips = null!;
    private Panel _allHost = null!;
    private RoundedPanel _allCard = null!;
    private readonly List<(RoundedPanel Card, FlowLayoutPanel Chips)> _outputCards = new();
    private readonly Dictionary<string, FlowLayoutPanel> _outputChips = new();
    private Bitmap? _genericApp;

    private readonly List<Panel> _pages = new();
    private int _page;
    private Label _title = null!, _subtitle = null!;
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
        _outputCards.Clear();
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
            Text = text, Font = FBody, ForeColor = Ink, BackColor = CardBg, HoverColor = HoverBg, BorderColor = ControlBorder,
            FocusColor = Ink, Radius = S(4), Margin = Pad(8, 0, 0, 0),
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
        var l = Lbl(text, FSubtitle, Ink);
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

        var header = new Panel { Dock = DockStyle.Top, Height = S(84) };
        _title = Lbl("Routing", FTitle, Ink); _title.Location = TextAt(_title, 0, 0);
        _subtitle = Lbl("Drag an app onto a device to play it there, or use the app's ⋯ menu.", FBody, Muted); _subtitle.Location = TextAt(_subtitle, 0, 44);
        var headBtns = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = Pad(0, 8, 0, 0) };
        _settingsBtn = Btn("Settings", (_, _) => ShowPage(_page == 0 ? 1 : 0), "Settings");
        _refreshBtn = Btn("Refresh", Refresh_Click);
        headBtns.Controls.Add(_settingsBtn); headBtns.Controls.Add(_refreshBtn);
        header.Controls.Add(headBtns); header.Controls.Add(_title); header.Controls.Add(_subtitle);

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
        _subtitle.Visible = i == 0;
        _refreshBtn.Visible = i == 0;
        _settingsBtn.Text = i == 0 ? "Settings" : "Done";
    }

    private Panel BuildRouting()
    {
        // The whole page scrolls (not just the app grid), so nothing gets clipped when routes make the top taller.
        var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = Pad(0, 0, 0, 16) };
        page.HandleCreated += (_, _) => ThemeInfo.ApplyScrollbars(page.Handle, Dark);

        // ===== Outputs =====
        var outHead = new Panel { Dock = DockStyle.Top, Height = S(44) };
        outHead.Controls.Add(Lbl("Outputs", FSubtitle, Ink).Also(l => l.Location = TextAt(l, 0, 4)));

        _outputRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
        _outputRow.SizeChanged += (_, _) => FitOutputCards();

        _allHost = new Panel { Dock = DockStyle.Top, Height = S(72 + 24), Padding = Pad(0, 0, 12, 24) };
        _allCard = new RoundedPanel { Dock = DockStyle.Fill, Radius = S(8), BackColor = CardBg, BorderColor = ControlBorder, Dashed = true };
        var allIcon = Glyph(GGlobe, FIconLg, Ink); allIcon.Location = P(16, 22);
        var allTitle = Lbl("All outputs", FBodyStrong, Ink); allTitle.Location = P(52, 14);
        var allSub = Lbl("Plays on every device at once", FCaption, Muted); allSub.Location = P(52, 38);
        // Chips wrap onto rows under the title; FitAllCard grows the card to fit them.
        _allChips = new FlowLayoutPanel { Location = P(52, 68), FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
        _allCard.Controls.Add(_allChips); _allCard.Controls.Add(allSub); _allCard.Controls.Add(allTitle); _allCard.Controls.Add(allIcon);
        _allCard.Resize += (_, _) => FitAllCard();
        _allHost.Controls.Add(_allCard);
        foreach (Control c in new Control[] { _allCard, _allChips, allTitle, allSub, allIcon }) WireDrop(c, AllScope);

        // ===== Apps =====
        var appHead = new Panel { Dock = DockStyle.Top, Height = S(52) };
        appHead.Controls.Add(Lbl("Apps", FSubtitle, Ink).Also(l => l.Location = TextAt(l, 0, 4)));
        var tools = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = Pad(0, 4, 12, 0) };

        var sortMenu = new ContextMenuStrip();
        FluentButton sortBtn = null!;
        sortBtn = Btn(SortLabel(), (_, _) => sortMenu.Show(sortBtn, new Point(0, sortBtn.Height)), "Sort: Name Z–A");
        sortBtn.AccessibleName = "Sort apps";
        void SetSort(int i) { _sortIndex = i; sortBtn.Text = SortLabel(); RebuildAppCards(); }
        sortMenu.Items.Add("Name A–Z", null, (_, _) => SetSort(0));
        sortMenu.Items.Add("Name Z–A", null, (_, _) => SetSort(1));

        var searchBox = new RoundedPanel { Size = Sz(240, 32), Radius = S(4), BackColor = CardBg, BorderColor = ControlBorder, Margin = Pad(8, 0, 0, 0) };
        searchBox.Controls.Add(Glyph(GSearch, FIconSm, Muted).Also(l => l.Location = P(10, 8)));
        _search = new TextBox
        {
            BorderStyle = BorderStyle.None, Location = P(34, 7), Width = S(196), Font = FBody, BackColor = CardBg, ForeColor = Ink,
            PlaceholderText = "Search apps", AccessibleName = "Search apps",
        };
        _search.TextChanged += (_, _) => RebuildAppCards();
        _search.GotFocus += (_, _) => { searchBox.BorderColor = Accent; searchBox.BorderThickness = 2; searchBox.Invalidate(); };
        _search.LostFocus += (_, _) => { searchBox.BorderColor = ControlBorder; searchBox.BorderThickness = 1; searchBox.Invalidate(); };
        searchBox.Controls.Add(_search);

        tools.Controls.Add(sortBtn); tools.Controls.Add(searchBox);
        appHead.Controls.Add(tools);

        _appRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true };

        page.Controls.Add(_appRow);
        page.Controls.Add(appHead);
        page.Controls.Add(_allHost);
        page.Controls.Add(_outputRow);
        page.Controls.Add(outHead);
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

    // ---------------- output / app / chip building ----------------

    private void FitAllCard()
    {
        int n = _allChips.Controls.Count, step = S(220 + 8);
        _allChips.Width = Math.Max(step, _allCard.ClientSize.Width - S(52 + 16));
        int rows = n == 0 ? 0 : (int)Math.Ceiling(n / (double)Math.Max(1, _allChips.Width / step));
        _allChips.Height = rows * S(46);
        int h = S(72 + 24) + (rows == 0 ? 0 : _allChips.Height + S(4));
        if (_allHost.Height != h) _allHost.Height = h;
    }

    private bool _fitting;
    private void FitOutputCards()
    {
        if (_fitting || _outputCards.Count == 0) return;
        _fitting = true;
        try
        {
            int gap = S(12);
            int avail = _outputRow.ClientSize.Width;
            // Consistent card width (~3 per row); clamp so 1-2 devices don't stretch to fill.
            int w = Math.Clamp((avail - 3 * gap) / 3, S(240), S(360));

            // Common height sized to the busiest card (min 1 chip slot), so cards aren't tall+empty.
            int maxRows = 1;
            foreach (var (_, chips) in _outputCards)
                maxRows = Math.Max(maxRows, chips.Controls.OfType<RoundedPanel>().Count());
            int h = S(12 + 52 + 12) + maxRows * S(46);

            foreach (var (card, chips) in _outputCards)
            {
                card.Width = w; card.Height = h;
                card.Margin = new Padding(0, 0, gap, gap);
                foreach (Control c in chips.Controls) if (c is RoundedPanel) c.Width = w - S(16 + 12);
            }
        }
        finally { _fitting = false; }
    }

    private RoundedPanel BuildOutputCard(OutputDevice dev)
    {
        var card = new RoundedPanel { Size = Sz(300, 160), Radius = S(8), BackColor = CardBg, BorderColor = CardBorder, Padding = Pad(16, 12, 12, 12) };

        var head = new Panel { Dock = DockStyle.Top, Height = S(52), Width = S(272) };
        var icon = Glyph(DeviceGlyph(dev.Name), FIconLg, dev.IsDefault ? Accent : Ink); icon.Location = P(0, 6);
        var name = new Label
        {
            Text = ShortName(dev.Name), Font = FBodyStrong, ForeColor = Ink, AutoEllipsis = true, UseMnemonic = false,
            Location = P(36, 2), Size = new Size(head.Width - S(36 + 36), S(22)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        var sub = new Label
        {
            Text = (dev.IsDefault ? "Default · " : "") + SubName(dev.Name), Font = FCaption, ForeColor = Muted, AutoEllipsis = true, UseMnemonic = false,
            Location = P(36, 26), Size = new Size(head.Width - S(36 + 36), S(18)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        var more = GlyphBtn(GMore, $"More options for {dev.Name}");
        more.Location = new Point(head.Width - S(32), 0); more.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Remove all apps from this device", null, (_, _) => ClearScope(dev.Id));
        more.Click += (_, _) => menu.Show(more, new Point(0, more.Height));
        more.Disposed += (_, _) => menu.Dispose();
        head.Controls.Add(more); head.Controls.Add(name); head.Controls.Add(sub); head.Controls.Add(icon);

        var chips = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        _outputChips[dev.Id] = chips;
        _outputCards.Add((card, chips));

        card.Controls.Add(chips); card.Controls.Add(head);
        foreach (Control c in new Control[] { card, chips, head, icon, name, sub }) WireDrop(c, dev.Id);
        return card;
    }

    private static string ShortName(string name) { int i = name.IndexOf('('); return (i > 0 ? name[..i] : name).Trim(); }
    private static string SubName(string name) { int i = name.IndexOf('('), j = name.IndexOf(')'); return i >= 0 && j > i ? name.Substring(i + 1, j - i - 1) : "Audio device"; }

    private RoundedPanel BuildChip(Mapping m, int width)
    {
        string dest = m.Scope == AllScope ? "all outputs" : ShortName(m.Routes.FirstOrDefault()?.Sink.DeviceName ?? "output");
        var chip = new RoundedPanel { Size = new Size(width, S(40)), Radius = S(4), BackColor = CardBg, BorderColor = ControlBorder, Margin = Pad(0, 0, 0, 6) };
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

    private RoundedPanel BuildAppCard(AudioApp a)
    {
        var card = new RoundedPanel { Size = Sz(148, 160), Radius = S(8), BackColor = CardBg, HoverBackColor = HoverBg, BorderColor = CardBorder, Margin = Pad(0, 0, 12, 12), Cursor = Cursors.Hand };
        var pic = new PictureBox { Image = AppIcon(a.ProcessId, S(40)), SizeMode = PictureBoxSizeMode.Zoom, Size = Sz(40, 40), Location = P(54, 22) };
        var name = new Label { Text = AppName(a), Location = P(8, 72), Size = Sz(132, 40), TextAlign = ContentAlignment.TopCenter, ForeColor = Ink, Font = FBody, AutoEllipsis = true, UseMnemonic = false };

        var menu = SendToMenu(a);
        card.ContextMenuStrip = menu;
        var send = GlyphBtn(GMore, $"Send {AppName(a)} to an output");
        send.Location = P(148 - 36, 4);
        send.Click += (_, _) => menu.Show(send, new Point(0, send.Height));
        send.Disposed += (_, _) => menu.Dispose();

        bool muted; try { muted = AudioInventory.GetAppMuted(a.ProcessId); } catch { muted = false; }
        var spk = Glyph(muted ? GMute : GVolume, FIconSm, muted ? Muted : Accent); spk.Location = P(12, 128);
        card.Controls.Add(Lbl("Speaker", FCaption, Muted).Also(l => l.Location = P(30, 127)));
        var toggle = new ToggleSwitch
        {
            OnColor = Accent, OffColor = Muted, KnobColor = OnAccent, FocusColor = Ink, Size = Sz(44, 22), Location = P(148 - 58, 124), On = !muted,
            AccessibleName = $"Play {AppName(a)} on the default speaker",
        };
        toggle.Toggled += (_, _) => { try { AudioInventory.SetAppMuted(a.ProcessId, !toggle.On); } catch { } spk.Text = toggle.On ? GVolume : GMute; spk.ForeColor = toggle.On ? Accent : Muted; SetStatus($"{AppName(a)}: system speaker {(toggle.On ? "on" : "off")}"); };
        _tips.SetToolTip(toggle, "Also play on the system default speaker");

        card.Controls.Add(send); card.Controls.Add(spk); card.Controls.Add(toggle); card.Controls.Add(name); card.Controls.Add(pic);
        WireDrag(card, a, card, pic, name);
        return card;
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
        var normal = card.BorderColor;
        void Down(object? s, MouseEventArgs e) { down = e.Location; dragging = false; }
        void Move(object? s, MouseEventArgs e) { if (e.Button == MouseButtons.Left && !dragging && (Math.Abs(e.X - down.X) > 4 || Math.Abs(e.Y - down.Y) > 4)) { dragging = true; _dragging = true; try { card.DoDragDrop(app, DragDropEffects.Copy); } finally { _dragging = false; } } }
        void Enter(object? s, EventArgs e) { card.BorderColor = Accent; card.Invalidate(); }
        void Leave(object? s, EventArgs e) { if (!card.ClientRectangle.Contains(card.PointToClient(Cursor.Position))) { card.BorderColor = normal; card.Invalidate(); } }
        foreach (var h in handles) { h.MouseDown += Down; h.MouseMove += Move; h.MouseEnter += Enter; h.MouseLeave += Leave; }
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
        foreach (var m in _mappings)
        {
            if (m.Scope == AllScope) { _allChips.Controls.Add(BuildChip(m, S(220)).Also(c => c.Margin = Pad(0, 0, 8, 6))); continue; }
            if (_outputChips.TryGetValue(m.Scope, out var container)) container.Controls.Add(BuildChip(m, S(272)));
        }
        foreach (var (id, panel) in _outputChips)
            if (panel.Controls.Count == 0) panel.Controls.Add(DropHint(id));
        FitOutputCards();
        FitAllCard();
        SetStatus($"{Plural(_outputChips.Count, "output")} · {Plural(_allApps.Count, "app")} · {Plural(_mappings.Count, "route")}");
    }

    /// <summary>Dashed chip-sized slot shown in a device card with nothing routed to it.</summary>
    private RoundedPanel DropHint(string scope)
    {
        var slot = new RoundedPanel { Size = new Size(S(272), S(40)), Radius = S(4), BackColor = CardBg, BorderColor = ControlBorder, Dashed = true, Margin = Pad(0, 0, 0, 6), Padding = new Padding(2) };
        var text = new Label { Text = "Drop an app here", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = FCaption, ForeColor = Muted, UseMnemonic = false };
        slot.Controls.Add(text);
        WireDrop(slot, scope); WireDrop(text, scope);
        return slot;
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

            _outputRow.SuspendLayout();
            DisposeChildren(_outputRow); _outputChips.Clear(); _outputCards.Clear();
            foreach (var d in _devices) _outputRow.Controls.Add(BuildOutputCard(d));
            _outputRow.ResumeLayout();
            FitOutputCards();

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
            static string AppKey(IEnumerable<AudioApp> xs) => string.Join("|", xs.Select(a => a.ProcessId).OrderBy(p => p));
            if (DevKey(AudioInventory.GetOutputDevices()) != DevKey(_devices) || AppKey(AudioInventory.GetActiveAudioApps()) != AppKey(_allApps))
                Refresh_Click(this, EventArgs.Empty);
        }
        catch { } // a device vanishing mid-enumeration: try again next tick
    }

    private void RebuildAppCards()
    {
        if (_appRow == null) return;
        var q = (_search?.Text ?? "").Trim();
        IEnumerable<AudioApp> apps = _allApps;
        if (q.Length > 0) apps = apps.Where(a => AppName(a).Contains(q, StringComparison.OrdinalIgnoreCase) || a.ProcessName.Contains(q, StringComparison.OrdinalIgnoreCase));
        apps = _sortIndex == 1 ? apps.OrderByDescending(a => AppName(a)) : apps.OrderBy(a => AppName(a));

        _appRow.SuspendLayout();
        DisposeChildren(_appRow);
        var list = apps.ToList();
        foreach (var a in list) _appRow.Controls.Add(BuildAppCard(a));
        if (list.Count == 0) _appRow.Controls.Add(new Label { Text = q.Length > 0 ? "No apps match your search." : "No apps are playing sound yet. They'll appear here when they start.", AutoSize = true, ForeColor = Muted, Font = FBody, Margin = Pad(0, 8, 0, 0), UseMnemonic = false });
        _appRow.ResumeLayout();
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
