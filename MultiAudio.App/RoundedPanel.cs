using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MultiAudio.App;

/// <summary>A Windows 11-style "settings card": rounded corners + hairline border.</summary>
public sealed class RoundedPanel : Panel
{
    public int Radius { get; set; } = 8;
    public Color BorderColor { get; set; } = Color.FromArgb(229, 229, 229);
    public int BorderThickness { get; set; } = 1;
    public bool Dashed { get; set; }
    public bool NoBorder { get; set; }

    public Color? HoverBackColor { get; set; }
    private Color? _normalBackColor;
    private bool _isHovering;

    public RoundedPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.FromArgb(251, 251, 251);
    }

    private void CheckHover()
    {
        if (!HoverBackColor.HasValue) return;
        bool hover = ClientRectangle.Contains(PointToClient(Cursor.Position));
        if (hover != _isHovering)
        {
            _isHovering = hover;
            if (hover) 
            { 
                _normalBackColor = BackColor; 
                BackColor = HoverBackColor.Value; 
            }
            else if (_normalBackColor.HasValue)
            { 
                BackColor = _normalBackColor.Value; 
            }
        }
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); CheckHover(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); CheckHover(); }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); CheckHover(); }

    private GraphicsPath RoundedRect(Rectangle r, int rad)
    {
        int d = rad * 2;
        var p = new GraphicsPath();
        if (rad <= 0) { p.AddRectangle(r); p.CloseFigure(); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        using var path = RoundedRect(new Rectangle(0, 0, Width, Height), Radius);
        Region = new Region(path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (NoBorder) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        int t = Math.Max(1, BorderThickness);
        using var pen = new Pen(BorderColor, t);
        if (Dashed) pen.DashStyle = DashStyle.Dash;
        int inset = t; // keep the thicker stroke inside the clipped region
        using var path = RoundedRect(new Rectangle(inset - 1, inset - 1, Width - 2 * inset, Height - 2 * inset), Radius);
        e.Graphics.DrawPath(pen, path);
    }
}
