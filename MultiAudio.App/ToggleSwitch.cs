using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MultiAudio.App;

/// <summary>A Windows 11-style pill toggle. It is a CheckBox underneath, so focus, Space and
/// screen readers (checkbox role + toggle state) come from the framework.</summary>
public sealed class ToggleSwitch : CheckBox
{
    public Color OnColor { get; set; } = Color.FromArgb(0, 103, 192);
    /// <summary>Track outline and knob when off (needs 3:1 against the parent background).</summary>
    public Color OffColor { get; set; } = Color.FromArgb(96, 96, 96);
    /// <summary>Knob when on.</summary>
    public Color KnobColor { get; set; } = Color.White;
    public Color FocusColor { get; set; } = Color.Black;

    /// <summary>Raised only for user changes (click or Space), not when <see cref="On"/> is set in code.</summary>
    public event EventHandler? Toggled;

    public bool On { get => Checked; set => Checked = value; }

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        AutoSize = false;
        Size = new Size(40, 20);
        Cursor = Cursors.Hand;
        Checked = true;
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e); // AutoCheck flips Checked here
        Toggled?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? BackColor);

        // Leave 2px all round for the focus ring.
        var r = new Rectangle(2, 2, Width - 5, Height - 5);
        using var track = Pill(r);
        if (Checked) { using var br = new SolidBrush(OnColor); g.FillPath(br, track); }
        else { using var pen = new Pen(OffColor); g.DrawPath(pen, track); }

        int d = r.Height, inset = Math.Max(3, d / 5), kd = d - 2 * inset;
        int kx = Checked ? r.Right - inset - kd : r.X + inset;
        using (var kb = new SolidBrush(Checked ? KnobColor : OffColor))
            g.FillEllipse(kb, kx, r.Y + inset, kd, kd);

        if (Focused && ShowFocusCues)
        {
            using var pen = new Pen(FocusColor, 1.5f);
            using var ring = Pill(new Rectangle(0, 0, Width - 1, Height - 1));
            g.DrawPath(pen, ring);
        }
    }

    private static GraphicsPath Pill(Rectangle r)
    {
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, r.Height, r.Height, 90, 180);
        p.AddArc(r.Right - r.Height, r.Y, r.Height, r.Height, 270, 180);
        p.CloseFigure();
        return p;
    }
}
