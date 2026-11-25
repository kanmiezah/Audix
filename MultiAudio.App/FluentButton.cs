using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MultiAudio.App;

/// <summary>A real <see cref="Button"/> (focus, Enter/Space, screen readers) painted as a Windows 11 control.</summary>
public sealed class FluentButton : Button
{
    public int Radius { get; set; } = 4;
    public Color BorderColor { get; set; } = Color.FromArgb(229, 229, 229);
    public bool NoBorder { get; set; }
    public Color HoverColor { get; set; } = Color.FromArgb(245, 245, 245);
    public Color FocusColor { get; set; } = Color.Black;

    private bool _hover, _down;

    public FluentButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        FlatStyle = FlatStyle.Flat;
        Cursor = Cursors.Hand;
        UseMnemonic = false;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? SystemColors.Control);

        var fill = _hover || _down ? HoverColor : BackColor;

        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = RoundedPanel.RoundedRect(r, Radius))
        {
            if (fill.A > 0) { using var br = new SolidBrush(fill); g.FillPath(br, path); }
            if (!NoBorder) { using var pen = new Pen(BorderColor); g.DrawPath(pen, path); }
        }

        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        flags |= TextAlign switch
        {
            ContentAlignment.MiddleLeft or ContentAlignment.TopLeft or ContentAlignment.BottomLeft => TextFormatFlags.Left,
            ContentAlignment.MiddleRight or ContentAlignment.TopRight or ContentAlignment.BottomRight => TextFormatFlags.Right,
            _ => TextFormatFlags.HorizontalCenter,
        };
        TextRenderer.DrawText(g, Text, Font, Rectangle.Inflate(ClientRectangle, -Padding.Left, 0), Enabled ? ForeColor : SystemColors.GrayText, flags);

        if (Focused && ShowFocusCues)
        {
            using var pen = new Pen(FocusColor, 2);
            using var ring = RoundedPanel.RoundedRect(new Rectangle(1, 1, Width - 3, Height - 3), Radius);
            g.DrawPath(pen, ring);
        }
    }
}
