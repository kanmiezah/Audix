using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MultiAudio.App;

/// <summary>A device icon on a soft rounded tile, with an optional corner badge (e.g. Bluetooth)
/// or a second, offset glyph behind it ("several devices").</summary>
public sealed class DeviceIcon : Control
{
    public string Glyph { get; set; } = "";
    public string Badge { get; set; } = "";
    public bool Stacked { get; set; }
    public Font? GlyphFont { get; set; }
    public Font? BadgeFont { get; set; }
    public Color TileColor { get; set; } = Color.Gainsboro;
    public Color GlyphColor { get; set; } = Color.Black;
    public Color BadgeColor { get; set; } = Color.RoyalBlue;
    public Color BadgeBack { get; set; } = Color.White;
    public int Radius { get; set; } = 8;
    /// <summary>Draw a portable (boombox-style) speaker instead of <see cref="Glyph"/>; Fluent has no such icon.</summary>
    public bool PortableSpeaker { get; set; }
    private Font? _small;

    /// <summary>Outline drawing matched to the Fluent glyphs' line weight: body, two drivers, carry handle.</summary>
    private void DrawPortableSpeaker(Graphics g)
    {
        float s = Width, sw = Math.Max(1.4f, s / 24f);
        using var pen = new Pen(GlyphColor, sw) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        var body = new RectangleF(s * 0.20f, s * 0.42f, s * 0.60f, s * 0.30f);
        float r = s * 0.08f;
        using (var p = new GraphicsPath())
        {
            p.AddArc(body.X, body.Y, 2 * r, 2 * r, 180, 90);
            p.AddArc(body.Right - 2 * r, body.Y, 2 * r, 2 * r, 270, 90);
            p.AddArc(body.Right - 2 * r, body.Bottom - 2 * r, 2 * r, 2 * r, 0, 90);
            p.AddArc(body.X, body.Bottom - 2 * r, 2 * r, 2 * r, 90, 90);
            p.CloseFigure();
            g.DrawPath(pen, p);
        }
        // Carry handle rising from the top of the body.
        float hl = s * 0.33f, hr = s * 0.67f, ht = s * 0.29f, hrad = s * 0.05f;
        using (var h = new GraphicsPath())
        {
            h.AddLine(hl, body.Y, hl, ht + hrad);
            h.AddArc(hl, ht, 2 * hrad, 2 * hrad, 180, 90);
            h.AddArc(hr - 2 * hrad, ht, 2 * hrad, 2 * hrad, 270, 90);
            h.AddLine(hr, ht + hrad, hr, body.Y);
            g.DrawPath(pen, h);
        }
        // Two drivers.
        float d = body.Height * 0.58f, cy = body.Y + body.Height / 2;
        foreach (var cx in new[] { body.X + body.Width * 0.28f, body.Right - body.Width * 0.28f })
            g.DrawEllipse(pen, cx - d / 2, cy - d / 2, d, d);
    }

    protected override void Dispose(bool disposing) { if (disposing) _small?.Dispose(); base.Dispose(disposing); }

    public DeviceIcon()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        TabStop = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? BackColor);
        using (var tile = RoundedPanel.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), Radius))
        using (var br = new SolidBrush(TileColor)) g.FillPath(br, tile);

        var font = GlyphFont ?? Font;
        const TextFormatFlags centre = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
        if (PortableSpeaker) DrawPortableSpeaker(g);
        else if (Stacked)
        {
            // Two smaller glyphs side by side, like a stereo pair: reads as "several devices" without smudging.
            _small ??= new Font(font.FontFamily, font.Size * 0.72f);
            int d = Width / 5;
            TextRenderer.DrawText(g, Glyph, _small, new Rectangle(-d, 0, Width, Height), GlyphColor, centre);
            TextRenderer.DrawText(g, Glyph, _small, new Rectangle(d, 0, Width, Height), GlyphColor, centre);
        }
        else TextRenderer.DrawText(g, Glyph, font, ClientRectangle, GlyphColor, centre);

        if (Badge.Length > 0)
        {
            int bd = Width * 9 / 20; // badge disc in the bottom-right corner, ringed by the tile colour
            var r = new Rectangle(Width - bd, Height - bd, bd - 1, bd - 1);
            using (var ring = new SolidBrush(BadgeBack)) g.FillEllipse(ring, r);
            TextRenderer.DrawText(g, Badge, BadgeFont ?? font, r, BadgeColor, centre);
        }
    }
}

/// <summary>
/// A small equaliser-style visualization for an app's sound. The bars' overall height follows the app's real
/// loudness (Windows' peak meter for its audio session); their spread between bars is decorative, since Windows
/// exposes a level, not a spectrum. Bars rise fast and fall gently, so it reads as fluid motion rather than flicker.
/// Call <see cref="Sample"/> on a ~30 fps timer.
/// </summary>
public sealed class LevelMeter : Control
{
    public Func<float>? Source { get; set; }
    public Color BarColor { get; set; } = Color.RoyalBlue;

    private readonly float[] _h;       // current, smoothed bar heights (0..1)
    private readonly float[] _speed;   // each bar sways at its own rate so they don't move in lockstep
    private readonly float[] _phase;
    private double _t;

    private static int _instances; // offsets each meter's sway so meters for the same app don't move in lockstep

    public LevelMeter(int bars = 6)
    {
        _h = new float[bars];
        _speed = new float[bars];
        _phase = new float[bars];
        float[] speeds = { 7.1f, 9.7f, 6.3f, 8.9f, 10.9f, 7.7f };
        float offset = (_instances++ % 7) * 2.3f;
        for (int i = 0; i < bars; i++) { _speed[i] = speeds[i % speeds.Length]; _phase[i] = i * 1.9f + offset; }
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        TabStop = false;
        AccessibleRole = AccessibleRole.Indicator;
    }

    public void Sample()
    {
        float level = 0;
        try { level = Source?.Invoke() ?? 0; } catch { } // session gone: show silence
        // Music peaks mostly sit between 0.2 and 0.6; stretch that range so loud and quiet passages look different.
        level = Math.Clamp(level * 1.7f, 0f, 1f);
        _t += 0.033;
        for (int i = 0; i < _h.Length; i++)
        {
            float sway = 0.5f + 0.5f * (float)Math.Sin(_t * _speed[i] + _phase[i]);
            float target = level < 0.03f ? 0f : level * (0.35f + 0.65f * sway);
            _h[i] += (target - _h[i]) * (target > _h[i] ? 0.55f : 0.16f); // fast attack, gentle release
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? BackColor);
        int n = _h.Length;
        // Thin bars with clear gaps, sitting on a common baseline like an equaliser.
        float w = Math.Max(2f, Width / (n * 1.9f)), gap = (Width - w * n) / Math.Max(1, n - 1);
        if (_h.All(v => v < 0.04f)) return; // silent: show nothing rather than placeholder dots
        using var brush = new SolidBrush(BarColor);
        for (int i = 0; i < n; i++)
        {
            float h = Math.Max(w, _h[i] * Height); // a dot when silent
            var r = new RectangleF(i * (w + gap), Height - h, w, h);
            using var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, w, w, 180, 180);                    // rounded top
            path.AddLine(r.Right, r.Y + w / 2, r.Right, r.Bottom);
            path.AddLine(r.Right, r.Bottom, r.X, r.Bottom);
            path.CloseFigure();
            g.FillPath(brush, path);
        }
    }
}