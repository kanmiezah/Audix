using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MultiAudio.App;

/// <summary>A small Windows 11-style pill toggle switch.</summary>
public sealed class ToggleSwitch : Control
{
    private bool _on = true;

    public Color OnColor { get; set; } = Color.FromArgb(0, 103, 192);
    public Color OffColor { get; set; } = Color.Gray;
    public Color KnobColor { get; set; } = Color.White;
    public event EventHandler? Toggled;

    public bool On
    {
        get => _on;
        set { if (_on != value) { _on = value; Invalidate(); } }
    }

    public ToggleSwitch()
    {
        DoubleBuffered = true;
        Size = new Size(40, 22);
        Cursor = Cursors.Hand;
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        _on = !_on;
        Invalidate();
        Toggled?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? BackColor);

        int h = Height - 2, w = Width - 2;
        var r = new Rectangle(1, 1, w, h);
        using var path = new GraphicsPath();
        int d = h;
        path.AddArc(r.X, r.Y, d, d, 90, 180);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        path.CloseFigure();
        using var br = new SolidBrush(_on ? OnColor : OffColor);
        g.FillPath(br, path);

        int kd = h - 6, ky = 4;
        int kx = _on ? Width - kd - 4 : 4;
        using var kb = new SolidBrush(KnobColor);
        g.FillEllipse(kb, kx, ky, kd, kd);
    }
}
