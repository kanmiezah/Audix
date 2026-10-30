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
    private const string IconFont = "Segoe Fluent Icons";

    // Segoe Fluent Icons glyphs
    private const string GHome = "", GVolume = "", GApps = "", GSettings = "", GKeyboard = "", GInfo = "", GRefresh = "", GSearch = "", GGlobe = "", GHeadphone = "", GBluetooth = "", GMonitor = "", GMore = "", GChevron = "", GMute = "";

    // ---- palette (dynamic) ----
    private static Color RailBg;
    private static Color ContentBg;
    private static Color CardBg;
    private static Color CardBorder;
    private static Color Ink;
    private static Color Muted;
    private static Color Accent;
    private static Color NavSelBg;
    private static Color ChipBorder;
    private static Color HoverBg;

    private static (Color Bg, Color Fg, Color Border)[] Tints = new (Color, Color, Color)[5];

    private void UpdatePalette()
    {
        bool dark = !ThemeInfo.IsLight();
        Accent = ThemeInfo.AccentColor(dark);
        if (dark)
        {
            RailBg = Color.FromArgb(32, 32, 32);
            ContentBg = Color.FromArgb(20, 20, 20);
            CardBg = Color.FromArgb(43, 43, 43);
            CardBorder = Color.FromArgb(65, 65, 65);
            Ink = Color.White;
            Muted = Color.FromArgb(170, 170, 170);
            NavSelBg = Color.FromArgb(40, Accent.R, Accent.G, Accent.B);
            ChipBorder = Color.FromArgb(70, 70, 70);
            HoverBg = Color.FromArgb(60, 60, 60);

            Tints = new[] {
                (Color.FromArgb(60, 40, 90), Color.FromArgb(180, 150, 255), Color.FromArgb(80, 60, 110)),
                (Color.FromArgb(30, 60, 90), Color.FromArgb(100, 200, 255), Color.FromArgb(50, 80, 110)),
                (Color.FromArgb(30, 70, 50), Color.FromArgb(100, 220, 130), Color.FromArgb(50, 90, 70)),
                (Color.FromArgb(80, 60, 30), Color.FromArgb(255, 180, 80),  Color.FromArgb(100, 80, 50)),
                (Color.FromArgb(80, 30, 60), Color.FromArgb(255, 120, 180), Color.FromArgb(100, 50, 80))
            };
        }
        else
        {
            RailBg = Color.White;
            ContentBg = Color.FromArgb(246, 247, 249);
            CardBg = Color.White;
            CardBorder = Color.FromArgb(236, 238, 241);
            Ink = Color.FromArgb(25, 28, 32);
            Muted = Color.FromArgb(107, 114, 128);
            NavSelBg = Color.FromArgb(234, 241, 254);
            ChipBorder = Color.FromArgb(229, 231, 235);
            HoverBg = Color.FromArgb(243, 244, 246);

            Tints = new[] {
                (Color.FromArgb(237, 233, 254), Color.FromArgb(124, 58, 237), Color.FromArgb(226, 219, 250)),
                (Color.FromArgb(224, 242, 254), Color.FromArgb(14, 165, 233), Color.FromArgb(211, 233, 251)),
                (Color.FromArgb(220, 252, 231), Color.FromArgb(22, 163, 74),  Color.FromArgb(206, 238, 217)),
                (Color.FromArgb(254, 243, 199), Color.FromArgb(217, 119, 6),  Color.FromArgb(243, 230, 196)),
                (Color.FromArgb(252, 231, 243), Color.FromArgb(219, 39, 119), Color.FromArgb(243, 217, 232))
            };
        }
    }

    // ---- state ----
    private readonly Label _status = new();
    private readonly Label _defaultSpeakerLbl = new();
    private int _sortIndex = 0;
    private TextBox _search = null!;
    private readonly ToolTip _tips = new() { AutoPopDelay = 15000, InitialDelay = 400, ReshowDelay = 200 };
    private NotifyIcon? _tray;

    private FlowLayoutPanel _outputRow = null!, _appRow = null!, _allChips = null!;
    private readonly List<(RoundedPanel Card, FlowLayoutPanel Chips)> _outputCards = new();
    private readonly Dictionary<string, FlowLayoutPanel> _outputChips = new();
    private Bitmap? _genericApp;

    private readonly List<Button> _nav = new();
    private readonly List<Panel> _pages = new();
    private int _navSel;
    private Panel _rail = null!;

    private readonly Dictionary<int, ProcessCapture> _captures = new();
    private readonly List<Mapping> _mappings = new();
    private List<AudioApp> _allApps = new();

    private int S(int val) => LogicalToDeviceUnits(val);
    private Point P(int x, int y) => new Point(S(x), S(y));
    private Size Sz(int w, int h) => new Size(S(w), S(h));
    private Padding Pad(int l, int t, int r, int b) => new Padding(S(l), S(t), S(r), S(b));

    public Form1()
    {
        InitializeComponent();
        UpdatePalette();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, _) => { if (IsHandleCreated) BeginInvoke(new Action(OnThemeChanged)); };
        BuildUi();
        SetupTray();
        Load += (_, _) => Refresh_Click(this, EventArgs.Empty);
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) HideToTray(); };
        FormClosing += OnClosing;
    }

    private void OnThemeChanged()
    {
        UpdatePalette();
        int oldNav = _navSel;
        string oldSearch = _search?.Text ?? "";
        
        Controls.Clear();
        _nav.Clear();
        _pages.Clear();
        _outputCards.Clear();
        _outputChips.Clear();
        
        BuildUi();
        SelectNav(oldNav);
        if (!string.IsNullOrEmpty(oldSearch) && _search != null) { _search.Text = oldSearch; _isPlaceholder = false; _search.ForeColor = Ink; }
        Refresh_Click(this, EventArgs.Empty);
    }

    // ---------------- glyph / icon helpers ----------------

    private static Label Glyph(string g, float size, Color color) => new()
    {
        Text = g, Font = new Font(IconFont, size), ForeColor = color, AutoSize = true, BackColor = Color.Transparent, UseMnemonic = false,
    };

    private static RoundedPanel Circle(int d, Color bg, string glyph, float glyphSize, Color glyphColor)
    {
        var c = new RoundedPanel { Width = d, Height = d, Radius = d / 2, BackColor = bg, NoBorder = true };
        c.Controls.Add(new Label { Text = glyph, Dock = DockStyle.Fill, Font = new Font(IconFont, glyphSize), ForeColor = glyphColor, TextAlign = ContentAlignment.MiddleCenter, BackColor = bg, UseMnemonic = false });
        return c;
    }

    private static string DeviceGlyph(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("head")) return GHeadphone;
        if (n.Contains("blue") || n.Contains(" bt")) return GBluetooth;
        if (n.Contains("monitor") || n.Contains("hdmi") || n.Contains("display") || n.Contains("nvidia") || n.Contains("tv")) return GMonitor;
        return GVolume;
    }

    private Bitmap MakeGenericAppIcon()
    {
        var bmp = new Bitmap(40, 40); using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.Transparent);
        using var br = new SolidBrush(Color.FromArgb(203, 213, 225)); using var path = new GraphicsPath();
        var r = new Rectangle(6, 6, 28, 28); int d = 12;
        path.AddArc(r.X, r.Y, d, d, 180, 90); path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure(); g.FillPath(br, path);
        return bmp;
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

    // ---------------- navigation rail ----------------

    private Button NavItem(string glyph, string text)
    {
        var b = new Button
        {
            Dock = DockStyle.Top, Height = 44, FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 10.5f), ForeColor = Ink, BackColor = RailBg, Cursor = Cursors.Hand,
            Margin = new Padding(0, 2, 0, 2), Text = "        " + text, UseMnemonic = false, Tag = glyph,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = HoverBg;
        b.Paint += (s, e) =>
        {
            bool sel = _nav.IndexOf(b) == _navSel;
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            if (sel)
            {
                using var br = new SolidBrush(NavSelBg);
                using var path = Rounded(new Rectangle(2, 4, b.Width - 6, b.Height - 8), 8);
                g.FillPath(br, path);
                using var bar = new SolidBrush(Accent);
                g.FillRectangle(bar, 2, (b.Height - 20) / 2, 3, 20);
            }
            using var f = new SolidBrush(sel ? Accent : Ink);
            using var iconFont = new Font(IconFont, 12f);
            g.DrawString(b.Tag as string, iconFont, f, 16, (b.Height - 22) / 2f);
        };
        return b;
    }

    private static GraphicsPath Rounded(Rectangle r, int rad)
    {
        int d = rad * 2; var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }

    private void SelectNav(int i)
    {
        _navSel = i;
        for (int j = 0; j < _nav.Count; j++) _nav[j].Invalidate();
        for (int j = 0; j < _pages.Count; j++) _pages[j].Visible = j == i;
    }

    // ---------------- layout ----------------

    private void BuildUi()
    {
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(1160, 900);
        BackColor = ContentBg; Text = "Audio Router"; MinimumSize = new Size(1000, 720);
        _genericApp = MakeGenericAppIcon();

        BuildRail();

        var content = new Panel { Dock = DockStyle.Fill, BackColor = ContentBg, Padding = new Padding(30, 22, 30, 0) };

        var header = new Panel { Dock = DockStyle.Top, Height = S(90), BackColor = ContentBg };
        header.Controls.Add(new Label { Text = "Dashboard", Location = P(0, 0), AutoSize = true, ForeColor = Ink, Font = new Font("Segoe UI Semibold", 22f, FontStyle.Bold), BackColor = ContentBg, UseMnemonic = false });
        header.Controls.Add(new Label { Text = "Drag apps to any output to route their audio.", Location = P(2, 44), AutoSize = true, ForeColor = Muted, Font = new Font("Segoe UI", 10f), BackColor = ContentBg, UseMnemonic = false });
        
        var targetBtn = new RoundedPanel { Size = Sz(44, 44), Radius = S(12), BackColor = CardBg, BorderColor = CardBorder, Anchor = AnchorStyles.Top | AnchorStyles.Right, Location = new Point(header.Width - S(44), S(6)) };
        targetBtn.Paint += (s, e) => { var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; using var pen = new Pen(Muted, 1.6f); g.DrawEllipse(pen, 14, 14, 16, 16); using var br = new SolidBrush(Muted); g.FillEllipse(br, 20, 20, 4, 4); g.DrawLine(pen, 22, 8, 22, 12); g.DrawLine(pen, 22, 32, 22, 36); g.DrawLine(pen, 8, 22, 12, 22); g.DrawLine(pen, 32, 22, 36, 22); };
        _tips.SetToolTip(targetBtn, "Focus mode");
        header.Controls.Add(targetBtn);
        
        content.Controls.Add(header);

        var pageHost = new Panel { Dock = DockStyle.Fill, BackColor = ContentBg };
        var dash = BuildDashboard();
        _pages.Add(dash);
        pageHost.Controls.Add(dash);
        foreach (var name in new[] { "Outputs", "Apps", "Settings", "Hotkeys", "About" })
        {
            var p = BuildPlaceholder(name);
            _pages.Add(p);
            pageHost.Controls.Add(p);
        }
        content.Controls.Add(pageHost);
        pageHost.BringToFront();

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 34, BackColor = ContentBg, Padding = new Padding(30, 6, 30, 6) };
        var legend = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, WrapContents = false, BackColor = ContentBg };
        legend.Controls.Add(Glyph(GVolume, 9f, Accent).Also(l => l.Margin = new Padding(0, 3, 0, 0)));
        legend.Controls.Add(new Label { Text = "Default speaker (system output)", AutoSize = true, ForeColor = Muted, Margin = new Padding(4, 3, 16, 0), BackColor = ContentBg, UseMnemonic = false });
        legend.Controls.Add(new Label { Text = "⬚", AutoSize = true, ForeColor = Accent, Margin = new Padding(4, 2, 0, 0), BackColor = ContentBg, Font = new Font("Segoe UI", 10f), UseMnemonic = false });
        legend.Controls.Add(new Label { Text = "Drag & drop to route", AutoSize = true, ForeColor = Muted, Margin = new Padding(4, 3, 16, 0), BackColor = ContentBg, UseMnemonic = false });
        legend.Controls.Add(new Label { Text = "✕", AutoSize = true, ForeColor = Muted, Margin = new Padding(4, 3, 0, 0), BackColor = ContentBg, Font = new Font("Segoe UI", 9f), UseMnemonic = false });
        legend.Controls.Add(new Label { Text = "Remove mapping", AutoSize = true, ForeColor = Muted, Margin = new Padding(4, 3, 0, 0), BackColor = ContentBg, UseMnemonic = false });
        _status.Dock = DockStyle.Right; _status.AutoSize = false; _status.Width = 440; _status.TextAlign = ContentAlignment.MiddleRight; _status.ForeColor = Muted; _status.BackColor = ContentBg; _status.UseMnemonic = false;
        footer.Controls.Add(legend); footer.Controls.Add(_status);

        Controls.Add(content);
        Controls.Add(footer);
        Controls.Add(_rail);
        content.BringToFront();

        SelectNav(0);
    }

    private void BuildRail()
    {
        _rail = new Panel { Dock = DockStyle.Left, Width = 236, BackColor = RailBg, Padding = new Padding(14, 14, 14, 12) };
        _rail.Paint += (s, e) => { using var pen = new Pen(CardBorder); e.Graphics.DrawLine(pen, _rail.Width - 1, 0, _rail.Width - 1, _rail.Height); };

        var brand = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = RailBg };
        var logo = new Panel { Size = new Size(30, 30), Location = new Point(2, 8), BackColor = RailBg };
        logo.Paint += (s, e) =>
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; using var br = new SolidBrush(Accent);
            int[] hs = { 12, 22, 30, 18, 24 };
            for (int i = 0; i < hs.Length; i++) g.FillRectangle(br, 2 + i * 6, 15 - hs[i] / 2, 3, hs[i]);
        };
        brand.Controls.Add(new Label { Text = "Audio Router", Location = new Point(40, 12), AutoSize = true, ForeColor = Ink, Font = new Font("Segoe UI Semibold", 12f, FontStyle.Bold), BackColor = RailBg, UseMnemonic = false });
        brand.Controls.Add(logo);

        var navHost = new Panel { Dock = DockStyle.Fill, BackColor = RailBg, Padding = new Padding(0, 14, 0, 0) };
        var items = new[] { (GHome, "Dashboard"), (GVolume, "Outputs"), (GApps, "Apps"), (GSettings, "Settings"), (GKeyboard, "Hotkeys"), (GInfo, "About") };
        for (int i = items.Length - 1; i >= 0; i--)
        {
            var b = NavItem(items[i].Item1, items[i].Item2);
            _nav.Insert(0, b);
            navHost.Controls.Add(b);
        }
        for (int i = 0; i < _nav.Count; i++) { int k = i; _nav[i].Click += (_, _) => SelectNav(k); }

        var bottomSettings = NavItem(GSettings, "Settings");
        bottomSettings.Dock = DockStyle.Bottom;
        bottomSettings.Click += (_, _) => SelectNav(3);

        var spk = new RoundedPanel { Dock = DockStyle.Bottom, Height = 62, Radius = 12, BackColor = CardBg, BorderColor = CardBorder, Padding = new Padding(10, 8, 8, 8), Margin = new Padding(0, 0, 0, 8) };
        var spkIcon = Glyph(GVolume, 12f, Accent); spkIcon.Location = new Point(4, 9); spkIcon.BackColor = CardBg;
        var spkTitle = new Label { Text = "System Default Speaker", Location = new Point(30, 6), AutoSize = true, ForeColor = Ink, Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold), BackColor = CardBg, UseMnemonic = false };
        _defaultSpeakerLbl.Text = "—"; _defaultSpeakerLbl.Location = new Point(30, 26); _defaultSpeakerLbl.AutoSize = false; _defaultSpeakerLbl.Size = new Size(150, 18); _defaultSpeakerLbl.ForeColor = Muted; _defaultSpeakerLbl.Font = new Font("Segoe UI", 8f); _defaultSpeakerLbl.BackColor = CardBg; _defaultSpeakerLbl.AutoEllipsis = true; _defaultSpeakerLbl.UseMnemonic = false;
        var chev = Glyph(GChevron, 8f, Muted); chev.Anchor = AnchorStyles.Right; chev.Location = new Point(spk.Width - 24, 24); chev.BackColor = CardBg;
        spk.Controls.Add(spkTitle); spk.Controls.Add(_defaultSpeakerLbl); spk.Controls.Add(spkIcon); spk.Controls.Add(chev);

        _rail.Controls.Add(navHost);
        _rail.Controls.Add(bottomSettings);
        _rail.Controls.Add(spk);
        _rail.Controls.Add(brand);
    }

    private RoundedPanel IconTextButton(string glyph, string text, int width, EventHandler onClick)
    {
        var p = new RoundedPanel { Size = new Size(width, 38), Radius = 10, BackColor = CardBg, HoverBackColor = HoverBg, BorderColor = ChipBorder, Cursor = Cursors.Hand };
        var gl = Glyph(glyph, 10f, Ink); gl.Location = new Point(16, 10); gl.BackColor = Color.Transparent;
        var tx = new Label { Text = text, Location = new Point(38, 0), Size = new Size(width - 40, 38), TextAlign = ContentAlignment.MiddleLeft, ForeColor = Ink, Font = new Font("Segoe UI", 9.5f), BackColor = Color.Transparent, Cursor = Cursors.Hand, UseMnemonic = false };
        p.Controls.Add(tx); p.Controls.Add(gl);
        p.Click += onClick; gl.Click += onClick; tx.Click += onClick;
        return p;
    }

    private Panel BuildDashboard()
    {
        var page = new Panel { Dock = DockStyle.Fill, BackColor = ContentBg }; // AutoScroll removed to let AppCard Fill

        // ===== Audio Outputs card =====
        var outCard = new RoundedPanel { Dock = DockStyle.Top, AutoSize = true, Radius = 16, BackColor = CardBg, BorderColor = CardBorder, Padding = new Padding(22, 18, 22, 20), Margin = new Padding(0, 0, 0, 18) };
        var outHead = new Panel { Dock = DockStyle.Top, Height = S(70), BackColor = CardBg };
        outHead.Controls.Add(new Label { Text = "Audio Outputs", Location = P(0, 0), AutoSize = true, ForeColor = Ink, Font = new Font("Segoe UI Semibold", 13f, FontStyle.Bold), BackColor = CardBg, UseMnemonic = false });
        outHead.Controls.Add(new Label { Text = "Connected outputs on this system", Location = P(2, 28), AutoSize = true, ForeColor = Muted, Font = new Font("Segoe UI", 9f), BackColor = CardBg, UseMnemonic = false });
        var refresh = IconTextButton(GRefresh, "Refresh", 116, Refresh_Click);
        refresh.Anchor = AnchorStyles.Top | AnchorStyles.Right; refresh.Location = new Point(outHead.Width - 116, 4);
        outHead.Controls.Add(refresh);
        outHead.Resize += (_, _) => refresh.Location = new Point(outHead.ClientSize.Width - 116, 4);

        _outputRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, MinimumSize = new Size(0, 250), FlowDirection = FlowDirection.LeftToRight, WrapContents = true, BackColor = CardBg, Margin = new Padding(0, 8, 0, 0) };
        _outputRow.SizeChanged += (_, _) => FitOutputCards();

        var allCard = new RoundedPanel { Dock = DockStyle.Bottom, Height = 84, Radius = 14, BackColor = !ThemeInfo.IsLight() ? Color.FromArgb(40, 40, 60) : Color.FromArgb(250, 251, 253), BorderColor = !ThemeInfo.IsLight() ? Color.FromArgb(70, 70, 100) : Color.FromArgb(191, 205, 251), Dashed = true, Padding = new Padding(16, 12, 16, 12) };
        var allIcon = Circle(46, !ThemeInfo.IsLight() ? Color.FromArgb(30, 40, 60) : Color.FromArgb(219, 234, 254), GGlobe, 15f, Accent); allIcon.Location = new Point(14, 18);
        var allTitle = new Label { Text = "All Outputs", Location = new Point(74, 18), AutoSize = true, ForeColor = Ink, Font = new Font("Segoe UI Semibold", 11f, FontStyle.Bold), BackColor = Color.Transparent, UseMnemonic = false };
        var allSub = new Label { Text = "Route audio to all connected outputs", Location = new Point(76, 42), AutoSize = true, ForeColor = Muted, Font = new Font("Segoe UI", 8.5f), BackColor = Color.Transparent, UseMnemonic = false };
        _allChips = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, WrapContents = false, AutoSize = true, BackColor = Color.Transparent, Anchor = AnchorStyles.Right | AnchorStyles.Top, Size = new Size(500, 46), Location = new Point(allCard.Width - 520, 18) };
        allCard.Controls.Add(_allChips); allCard.Controls.Add(allSub); allCard.Controls.Add(allTitle); allCard.Controls.Add(allIcon);
        allCard.Resize += (_, _) => _allChips.Location = new Point(allCard.ClientSize.Width - _allChips.Width - 16, 18);
        WireDrop(allCard, AllScope); WireDrop(_allChips, AllScope); WireDrop(allTitle, AllScope); WireDrop(allSub, AllScope);

        outCard.Controls.Add(_outputRow);
        outCard.Controls.Add(allCard);
        outCard.Controls.Add(outHead);

        // ===== Apps card =====
        var appCard = new RoundedPanel { Dock = DockStyle.Fill, Radius = 16, BackColor = CardBg, BorderColor = CardBorder, Padding = new Padding(22, 18, 22, 20) };
        var appHead = new Panel { Dock = DockStyle.Top, Height = S(70), BackColor = CardBg };
        appHead.Controls.Add(new Label { Text = "Apps", Location = P(0, 0), AutoSize = true, ForeColor = Ink, Font = new Font("Segoe UI Semibold", 13f, FontStyle.Bold), BackColor = CardBg, UseMnemonic = false });
        appHead.Controls.Add(new Label { Text = "Drag an app to an output above to route its audio", Location = P(2, 28), AutoSize = true, ForeColor = Muted, Font = new Font("Segoe UI", 9f), BackColor = CardBg, UseMnemonic = false });

        var searchBox = new RoundedPanel { Size = new Size(240, 38), Radius = 10, BackColor = CardBg, BorderColor = ChipBorder, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        searchBox.Controls.Add(Glyph(GSearch, 9f, Muted).Also(l => { l.Location = new Point(12, 11); l.BackColor = CardBg; }));
        _search = new TextBox { BorderStyle = BorderStyle.None, Location = new Point(36, 11), Width = 190, Font = new Font("Segoe UI", 9.5f), BackColor = CardBg, ForeColor = Ink };
        _search.TextChanged += (_, _) => RebuildAppCards();
        SetPlaceholder(_search, "Search apps");
        searchBox.Controls.Add(_search);
        appHead.Controls.Add(searchBox);

        var sortBtn = new RoundedPanel { Size = Sz(150, 38), Radius = S(10), BackColor = CardBg, BorderColor = ChipBorder, Anchor = AnchorStyles.Top | AnchorStyles.Right, Cursor = Cursors.Hand };
        var sortLbl = new Label { Text = "Name (A-Z)", Location = P(36, 0), Size = Sz(88, 38), TextAlign = ContentAlignment.MiddleLeft, ForeColor = Ink, Font = new Font("Segoe UI", 9.5f), BackColor = Color.Transparent, Cursor = Cursors.Hand, UseMnemonic = false };
        var sortIco = Glyph("⇅", 10f, Ink); sortIco.Location = P(12, 10); sortIco.BackColor = Color.Transparent; sortIco.Cursor = Cursors.Hand;
        var sortChev = Glyph(GChevron, 8f, Muted); sortChev.Location = P(126, 12); sortChev.BackColor = Color.Transparent; sortChev.Cursor = Cursors.Hand;
        sortBtn.Controls.Add(sortIco); sortBtn.Controls.Add(sortLbl); sortBtn.Controls.Add(sortChev);
        
        var sortMenu = new ContextMenuStrip();
        sortMenu.Items.Add("Name (A-Z)", null, (_, _) => { _sortIndex = 0; sortLbl.Text = "Name (A-Z)"; RebuildAppCards(); });
        sortMenu.Items.Add("Name (Z-A)", null, (_, _) => { _sortIndex = 1; sortLbl.Text = "Name (Z-A)"; RebuildAppCards(); });
        void showSort(object? s, EventArgs e) => sortMenu.Show(sortBtn, new Point(0, sortBtn.Height));
        sortBtn.Click += showSort; sortLbl.Click += showSort; sortIco.Click += showSort; sortChev.Click += showSort;
        appHead.Controls.Add(sortBtn);
        void PosHead() { sortBtn.Location = new Point(appHead.ClientSize.Width - S(150), S(6)); searchBox.Location = new Point(appHead.ClientSize.Width - S(150) - S(240) - S(12), S(6)); }
        appHead.Resize += (_, _) => PosHead();

        _appRow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoScroll = true, BackColor = CardBg, Margin = new Padding(0, 8, 0, 0) };
        appCard.Controls.Add(_appRow);
        appCard.Controls.Add(appHead);

        page.Controls.Add(appCard);
        page.Controls.Add(outCard);
        return page;
    }

    private Panel BuildPlaceholder(string name)
    {
        var page = new Panel { Dock = DockStyle.Fill, BackColor = ContentBg, Visible = false };
        var card = new RoundedPanel { Dock = DockStyle.Top, Height = 170, Radius = 16, BackColor = CardBg, BorderColor = CardBorder, Padding = new Padding(24, 20, 24, 20) };
        card.Controls.Add(new Label { Text = name, Dock = DockStyle.Top, Height = 30, ForeColor = Ink, Font = new Font("Segoe UI Semibold", 13f, FontStyle.Bold), BackColor = CardBg, UseMnemonic = false });
        card.Controls.Add(new Label
        {
            Dock = DockStyle.Fill, ForeColor = Muted, Font = new Font("Segoe UI", 9.5f), BackColor = CardBg, UseMnemonic = false,
            Text = name switch
            {
                "Outputs" => "All connected output devices are shown on the Dashboard. A dedicated per-device view lands here.",
                "Apps" => "Every audio-producing app appears on the Dashboard. A dedicated app manager lands here.",
                "Settings" => "Reset every app to the Windows system-default output. Use this if apps seem \"stuck\" on a device from an earlier per-app override.",
                "Hotkeys" => "Global keyboard shortcuts for switching routing profiles will live here.",
                _ => "Audio Router routes each app's sound to the devices you choose. Closing the window keeps it running in the system tray.",
            },
        });
        if (name == "Settings")
        {
            var reset = new RoundedPanel { Size = Sz(320, 40), Radius = S(10), BackColor = Accent, NoBorder = true, Location = P(24, 96), Cursor = Cursors.Hand };
            reset.Controls.Add(new Label { Text = "Reset all app → device overrides", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 9.5f), BackColor = Color.Transparent, Cursor = Cursors.Hand, UseMnemonic = false });
            void doReset(object? s, EventArgs e) => ResetAllOverrides();
            reset.Click += doReset; foreach (Control cc in reset.Controls) cc.Click += doReset;
            card.Controls.Add(reset);

            var clearProf = new RoundedPanel { Size = Sz(320, 40), Radius = S(10), BackColor = CardBg, HoverBackColor = HoverBg, BorderColor = CardBorder, Location = P(24, 146), Cursor = Cursors.Hand };
            clearProf.Controls.Add(new Label { Text = "Clear all saved profiles", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Ink, Font = new Font("Segoe UI Semibold", 9.5f), BackColor = Color.Transparent, Cursor = Cursors.Hand, UseMnemonic = false });
            void doClearProf(object? s, EventArgs e) { ProfileStore.SaveAll(new()); SetStatus("All saved routing profiles have been cleared."); }
            clearProf.Click += doClearProf; foreach (Control cc in clearProf.Controls) cc.Click += doClearProf;
            card.Controls.Add(clearProf);
            
            card.Height = S(210);
        }
        page.Controls.Add(card);
        return page;
    }

    // ---------------- output / app / chip building ----------------

    private bool _fitting;
    private void FitOutputCards()
    {
        if (_fitting || _outputCards.Count == 0) return;
        _fitting = true;
        try
        {
            int gap = S(16);
            int avail = _outputRow.ClientSize.Width;
            // Consistent card width (~3 per row); clamp so 1-2 devices don't stretch to fill.
            int w = Math.Clamp((avail - 2 * gap) / 3, S(248), S(360));

            // Common height sized to the busiest card (min 1 chip slot), so cards aren't tall+empty.
            int chipH = S(54);            // 46 chip + 8 margin
            int maxRows = 1;
            foreach (var (_, chips) in _outputCards)
                maxRows = Math.Max(maxRows, chips.Controls.OfType<RoundedPanel>().Count());
            int h = S(74) + maxRows * chipH + S(24);

            foreach (var (card, chips) in _outputCards)
            {
                card.Width = w; card.Height = h;
                card.Margin = new Padding(0, 0, gap, gap);
                foreach (Control c in chips.Controls) if (c is RoundedPanel) c.Width = card.Width - S(32);
            }
        }
        finally { _fitting = false; }
    }

    private RoundedPanel BuildOutputCard(OutputDevice dev, int index)
    {
        var t = Tints[index % Tints.Length];
        var card = new RoundedPanel { Width = 320, Height = 236, Radius = 14, BackColor = CardBg, HoverBackColor = HoverBg, BorderColor = t.Border, Margin = new Padding(0, 0, 16, 0), Padding = new Padding(16, 14, 16, 14) };

        var head = new Panel { Dock = DockStyle.Top, Height = S(72), BackColor = CardBg };
        var circle = Circle(S(46), t.Bg, DeviceGlyph(dev.Name), 15f, t.Fg); circle.Location = P(0, 4);
        head.Controls.Add(circle);
        head.Controls.Add(new Label { Text = "Output " + (index + 1), Location = P(58, 2), AutoSize = true, ForeColor = Muted, Font = new Font("Segoe UI Semibold", 8f, FontStyle.Bold), BackColor = CardBg, UseMnemonic = false });
        head.Controls.Add(new Label { Text = ShortName(dev.Name), Location = P(58, 18), Size = Sz(190, 24), ForeColor = Ink, Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold), BackColor = CardBg, AutoEllipsis = true, UseMnemonic = false });
        head.Controls.Add(new Label { Text = SubName(dev.Name), Location = P(58, 42), Size = Sz(190, 22), ForeColor = Muted, Font = new Font("Segoe UI", 8f), BackColor = CardBg, AutoEllipsis = true, UseMnemonic = false });
        var more = Glyph(GMore, 11f, Muted); more.Location = new Point(head.Width - 22, 2); more.Anchor = AnchorStyles.Top | AnchorStyles.Right; more.BackColor = CardBg; more.Cursor = Cursors.Hand;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Clear this output", null, (_, _) => ClearScope(dev.Id));
        more.Click += (_, _) => menu.Show(more, new Point(0, 18));
        head.Controls.Add(more);
        head.Resize += (_, _) => more.Location = new Point(head.ClientSize.Width - 22, 2);

        var chips = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = CardBg, Margin = new Padding(0, 6, 0, 0) };
        _outputChips[dev.Id] = chips;
        _outputCards.Add((card, chips));

        card.Controls.Add(chips); card.Controls.Add(head);
        WireDrop(card, dev.Id); WireDrop(chips, dev.Id); WireDrop(head, dev.Id);
        return card;
    }

    private static string ShortName(string name) { int i = name.IndexOf('('); return (i > 0 ? name[..i] : name).Trim(); }
    private static string SubName(string name) { int i = name.IndexOf('('), j = name.IndexOf(')'); return i >= 0 && j > i ? name.Substring(i + 1, j - i - 1) : "Audio device"; }

    private RoundedPanel BuildChip(Mapping m)
    {
        var chip = new RoundedPanel { Width = 280, Height = 46, Radius = 10, BackColor = CardBg, HoverBackColor = HoverBg, BorderColor = ChipBorder, Margin = new Padding(0, 0, 0, 8) };
        chip.Controls.Add(new PictureBox { Image = AppIcon(m.App.ProcessId, 22), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(22, 22), Location = new Point(12, 12), BackColor = Color.Transparent });
        var name = new Label { Text = m.App.ProcessName, Location = new Point(44, 0), Size = new Size(196, 46), TextAlign = ContentAlignment.MiddleLeft, ForeColor = Ink, Font = new Font("Segoe UI", 9.5f), BackColor = Color.Transparent, AutoEllipsis = true, UseMnemonic = false };
        var x = new Label { Text = "✕", Size = new Size(24, 24), Location = new Point(chip.Width - 32, 11), Font = new Font("Segoe UI", 9.5f), ForeColor = Muted, TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.Transparent, Cursor = Cursors.Hand, Anchor = AnchorStyles.Right | AnchorStyles.Top, UseMnemonic = false };
        x.MouseEnter += (_, _) => x.ForeColor = Color.FromArgb(220, 38, 38);
        x.MouseLeave += (_, _) => x.ForeColor = Muted;
        x.Click += (_, _) => RemoveMapping(m);
        // Click the chip body to open a delay/sync slider for this output.
        void openDelay(object? s, EventArgs e) => ShowDelayPopup(m, chip);
        chip.Click += openDelay; name.Click += openDelay;
        _tips.SetToolTip(name, "Click to set this output's delay (sync it with your other outputs)");
        chip.Resize += (_, _) => { x.Location = new Point(chip.ClientSize.Width - 32, 11); name.Size = new Size(chip.ClientSize.Width - 44 - 30, 46); };
        chip.Controls.Add(x); chip.Controls.Add(name);
        return chip;
    }

    private void ShowDelayPopup(Mapping m, Control anchor)
    {
        int cur = m.Routes.FirstOrDefault()?.Sink.DelayMs ?? 0;
        string dest = m.Scope == AllScope ? "All outputs" : (m.Routes.FirstOrDefault()?.Sink.DeviceName ?? "output");

        var pop = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Size = new Size(280, 92), ShowInTaskbar = false, BackColor = CardBg };
        pop.Paint += (s, e) => ControlPaint.DrawBorder(e.Graphics, pop.ClientRectangle, CardBorder, ButtonBorderStyle.Solid);

        var title = new Label { Text = $"Delay  ·  {m.App.ProcessName} → {dest}", Location = new Point(12, 10), AutoSize = true, ForeColor = Ink, Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold), BackColor = CardBg, UseMnemonic = false };
        var val = new Label { Text = cur + " ms", Location = new Point(224, 10), AutoSize = true, ForeColor = Accent, Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold), BackColor = CardBg, UseMnemonic = false };
        var tb = new TrackBar { Minimum = 0, Maximum = 500, TickFrequency = 50, Value = Math.Clamp(cur, 0, 500), Location = new Point(8, 36), Size = new Size(264, 45) };
        tb.Scroll += (_, _) => { val.Text = tb.Value + " ms"; foreach (var h in m.Routes) h.Sink.DelayMs = tb.Value; SetStatus($"{m.App.ProcessName} → {dest}: delay {tb.Value} ms"); };
        pop.Controls.Add(val); pop.Controls.Add(title); pop.Controls.Add(tb);

        var scr = anchor.PointToScreen(new Point(0, anchor.Height + 2));
        pop.Location = scr;
        pop.Deactivate += (_, _) => pop.Close();
        pop.Show(this);
    }

    private RoundedPanel BuildAppCard(AudioApp a)
    {
        var card = new RoundedPanel { Width = 150, Height = 176, Radius = 14, BackColor = CardBg, HoverBackColor = HoverBg, BorderColor = CardBorder, Margin = new Padding(0, 0, 14, 14), Cursor = Cursors.Hand };
        var grip = new Label { Text = "⋮⋮", Font = new Font("Segoe UI", 9f), ForeColor = Color.FromArgb(198, 203, 210), AutoSize = true, Location = new Point(card.Width - 22, 10), Anchor = AnchorStyles.Top | AnchorStyles.Right, BackColor = Color.Transparent, UseMnemonic = false };
        var pic = new PictureBox { Image = AppIcon(a.ProcessId, 44), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(44, 44), Location = new Point((150 - 44) / 2, 26), BackColor = Color.Transparent };
        var name = new Label { Text = a.ProcessName, Location = new Point(8, 82), Size = new Size(134, 34), TextAlign = ContentAlignment.TopCenter, ForeColor = Ink, Font = new Font("Segoe UI", 9f), BackColor = Color.Transparent, AutoEllipsis = true, UseMnemonic = false };

        bool muted; try { muted = AudioInventory.GetAppMuted(a.ProcessId); } catch { muted = false; }
        var spk = Glyph(muted ? GMute : GVolume, 11f, muted ? Muted : Accent); spk.Location = new Point(16, 138); spk.BackColor = Color.Transparent;
        var toggle = new ToggleSwitch { OnColor = Accent, OffColor = Color.FromArgb(203, 209, 217), KnobColor = Color.White, Location = new Point(card.Width - 58, 138), On = !muted };
        toggle.Toggled += (_, _) => { try { AudioInventory.SetAppMuted(a.ProcessId, !toggle.On); } catch { } spk.Text = toggle.On ? GVolume : GMute; spk.ForeColor = toggle.On ? Accent : Muted; SetStatus($"{a.ProcessName}: system speaker {(toggle.On ? "on" : "off")}"); };
        _tips.SetToolTip(toggle, "Also play on the system default speaker");

        card.Controls.Add(grip); card.Controls.Add(spk); card.Controls.Add(toggle); card.Controls.Add(name); card.Controls.Add(pic);
        WireDrag(card, a, card, pic, name);
        return card;
    }

    private void WireDrop(Control target, string scope)
    {
        target.AllowDrop = true;
        RoundedPanel? Host(Control c) => c as RoundedPanel ?? c.Parent as RoundedPanel ?? c.Parent?.Parent as RoundedPanel;
        void Hi(bool on) { var h = Host(target); if (h != null) { if (on) { h.BorderThickness = 2; h.BorderColor = Accent; } else { h.BorderThickness = 1; } h.Invalidate(); } }
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
        void Move(object? s, MouseEventArgs e) { if (e.Button == MouseButtons.Left && !dragging && (Math.Abs(e.X - down.X) > 4 || Math.Abs(e.Y - down.Y) > 4)) { dragging = true; card.DoDragDrop(app, DragDropEffects.Copy); } }
        void Enter(object? s, EventArgs e) { card.BorderColor = Accent; card.Invalidate(); }
        void Leave(object? s, EventArgs e) { if (!card.ClientRectangle.Contains(card.PointToClient(Cursor.Position))) { card.BorderColor = normal; card.Invalidate(); } }
        foreach (var h in handles) { h.MouseDown += Down; h.MouseMove += Move; h.MouseEnter += Enter; h.MouseLeave += Leave; }
    }

    // ---------------- mapping model ----------------

    private void CreateMapping(AudioApp app, string scope, int volPct = 100, int delayMs = 0)
    {
        if (_mappings.Any(m => m.App.ProcessId == app.ProcessId && m.Scope == scope)) { SetStatus($"{app.ProcessName} is already on that output."); return; }
        var m = new Mapping { App = app, Scope = scope };
        if (scope == AllScope)
            foreach (var d in AudioInventory.GetOutputDevices()) m.Routes.Add(AddRoute(app.ProcessId, app.ProcessName, d.Id, d.Name, volPct, delayMs));
        else
        {
            var dev = AudioInventory.GetOutputDevices().FirstOrDefault(d => d.Id == scope);
            if (dev == null) { SetStatus("That device is no longer available."); return; }
            m.Routes.Add(AddRoute(app.ProcessId, app.ProcessName, dev.Id, dev.Name, volPct, delayMs));
        }
        _mappings.Add(m);
        RenderChips();
        SetStatus($"Mapped {app.ProcessName} → {(scope == AllScope ? "ALL outputs" : m.Routes[0].Sink.DeviceName)}");
    }

    private void RemoveMapping(Mapping m)
    {
        foreach (var h in m.Routes) StopRouteHandle(h);
        _mappings.Remove(m);
        RenderChips();
        SetStatus($"Removed {m.App.ProcessName}");
    }

    private void ClearScope(string scope)
    {
        foreach (var m in _mappings.Where(m => m.Scope == scope).ToArray()) { foreach (var h in m.Routes) StopRouteHandle(h); _mappings.Remove(m); }
        RenderChips();
    }

    private void RenderChips()
    {
        foreach (var panel in _outputChips.Values) panel.Controls.Clear();
        _allChips.Controls.Clear();
        foreach (var m in _mappings)
        {
            FlowLayoutPanel? container = m.Scope == AllScope ? _allChips : (_outputChips.TryGetValue(m.Scope, out var c) ? c : null);
            if (container == null) continue;
            var chip = BuildChip(m);
            if (container == _allChips) { chip.Width = 168; chip.Height = 44; chip.Margin = new Padding(8, 0, 0, 0); }
            container.Controls.Add(chip);
        }
        FitOutputCards();
        SetStatus($"{_outputChips.Count} output(s)   •   {_allApps.Count} app(s)   •   {_mappings.Count} mapping(s)");
    }

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
            var devices = AudioInventory.GetOutputDevices();
            _defaultSpeakerLbl.Text = devices.FirstOrDefault(d => d.IsDefault)?.Name ?? "—";

            _outputRow.SuspendLayout();
            _outputRow.Controls.Clear(); _outputChips.Clear(); _outputCards.Clear();
            for (int i = 0; i < devices.Count; i++) _outputRow.Controls.Add(BuildOutputCard(devices[i], i));
            _outputRow.ResumeLayout();
            FitOutputCards();

            _allApps = AudioInventory.GetActiveAudioApps();
            RebuildAppCards();
            RenderChips();
        }
        catch (Exception ex) { SetStatus("Error: " + ex.Message); }
    }

    private void RebuildAppCards()
    {
        if (_appRow == null) return;
        var q = (_search?.Text ?? "").Trim();
        bool hasQ = q.Length > 0 && !_isPlaceholder;
        IEnumerable<AudioApp> apps = _allApps;
        if (hasQ) apps = apps.Where(a => a.ProcessName.Contains(q, StringComparison.OrdinalIgnoreCase));
        apps = _sortIndex == 1 ? apps.OrderByDescending(a => a.ProcessName) : apps.OrderBy(a => a.ProcessName);

        _appRow.SuspendLayout();
        _appRow.Controls.Clear();
        var list = apps.ToList();
        foreach (var a in list) _appRow.Controls.Add(BuildAppCard(a));
        if (list.Count == 0) _appRow.Controls.Add(new Label { Text = hasQ ? "No apps match your search." : "No apps are producing audio yet. Start playback, then press Refresh.", AutoSize = true, ForeColor = Muted, Font = new Font("Segoe UI", 9.5f), BackColor = CardBg, Margin = new Padding(4, 12, 0, 0), UseMnemonic = false });
        _appRow.ResumeLayout();
    }

    // ---------------- search placeholder ----------------

    private bool _isPlaceholder;
    private void SetPlaceholder(TextBox box, string text)
    {
        void Show() { _isPlaceholder = true; box.ForeColor = Muted; box.Text = text; }
        void Hide() { _isPlaceholder = false; box.ForeColor = Ink; box.Text = ""; }
        Show();
        box.GotFocus += (_, _) => { if (_isPlaceholder) Hide(); };
        box.LostFocus += (_, _) => { if (box.Text.Length == 0) Show(); };
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
        try
        {
            var diag = AppEndpointRouter.ClearAll();
            SetStatus("Reset all per-app audio overrides to Windows default.   " + diag);
            MessageBox.Show(this,
                "All apps have been reset to the Windows default output.\n\n" + diag +
                "\n\nIf an app is mid-playback, restart its playback (or the app) to pick up the change.",
                "Reset app audio", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { SetStatus("Reset failed: " + ex.Message); }
    }

    private void SetStatus(string text)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => _status.Text = text)); return; }
        _status.Text = text;
    }
}

internal static class ControlExt
{
    public static T Also<T>(this T c, Action<T> f) where T : Control { f(c); return c; }
}
