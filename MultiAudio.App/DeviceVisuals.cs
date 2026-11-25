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
/// A tiny live level visualization: bars that scroll from right to left with the app's recent peak level
/// (sampled from Windows' meter for its audio session), centred like a waveform. Call <see cref="Sample"/> on a timer.
/// </summary>
public sealed class LevelMeter : Control
{
    public Func<float>? Source { get; set; }
    public Color BarColor { get; set; } = Color.RoyalBlue;
    public Color IdleColor { get; set; } = Color.Gray;
    private readonly float[] _bars;

    public LevelMeter(int bars = 7)
    {
        _bars = new float[bars];
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        TabStop = false;
        AccessibleRole = AccessibleRole.Indicator;
    }

    public void Sample()
    {
        float p = 0;
        try { p = Source?.Invoke() ?? 0; } catch { } // session gone: show silence
        // Music peaks mostly sit between 0.2 and 0.6; stretch that range so the bars visibly rise and fall.
        p = Math.Clamp(p * 1.6f, 0f, 1f);
        Array.Copy(_bars, 1, _bars, 0, _bars.Length - 1);
        _bars[^1] = p;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? BackColor);
        int n = _bars.Length;
        float gap = Math.Max(2f, Width / (n * 3f)), w = (Width - gap * (n - 1)) / n, mid = Height / 2f;
        using var on = new SolidBrush(BarColor);
        using var idle = new SolidBrush(IdleColor);
        for (int i = 0; i < n; i++)
        {
            float h = Math.Max(w, _bars[i] * Height); // never thinner than a dot
            float x = i * (w + gap);
            using var path = RoundedPanel.RoundedRect(Rectangle.Round(new RectangleF(x, mid - h / 2, w, h)), (int)(w / 2));
            g.FillPath(_bars[i] < 0.05f ? idle : on, path);
        }
    }
}
