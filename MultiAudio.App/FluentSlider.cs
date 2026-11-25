using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MultiAudio.App;

/// <summary>
/// A native <see cref="TrackBar"/> (so keyboard, mouse dragging and screen readers all work as Windows
/// intends) that paints itself as a Windows 11 slider: thin rounded track, accent fill, round thumb.
/// It draws exactly where the native control puts its channel and thumb, so hit-testing stays correct.
/// </summary>
public sealed class FluentSlider : TrackBar
{
    public Color TrackColor { get; set; } = Color.FromArgb(160, 160, 160);
    public Color FillColor { get; set; } = Color.FromArgb(0, 103, 192);
    public Color ThumbColor { get; set; } = Color.White;
    public Color FocusColor { get; set; } = Color.Black;

    public FluentSlider()
    {
        AutoSize = false;
        TickStyle = TickStyle.None;
        Minimum = 0; Maximum = 100; SmallChange = 2; LargeChange = 10;
    }

    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        // Rows change colour on hover; repaint so the slider's background follows.
        if (Parent != null) Parent.BackColorChanged += (_, _) => Invalidate();
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    private const int WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014;
    private const int TBM_GETTHUMBRECT = 0x0419, TBM_GETCHANNELRECT = 0x041A;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT { public IntPtr hdc; public bool fErase; public RECT rc; public bool fRestore, fIncUpdate; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] reserved; }
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, ref RECT r);
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr h, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr h, ref PAINTSTRUCT ps);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_ERASEBKGND) { m.Result = (IntPtr)1; return; } // everything is painted below; avoids flicker
        if (m.Msg != WM_PAINT) { base.WndProc(ref m); return; }

        var hdc = BeginPaint(Handle, out var ps);
        try
        {
            using var target = Graphics.FromHdc(hdc);
            using var buffer = new Bitmap(Math.Max(1, Width), Math.Max(1, Height));
            using (var g = Graphics.FromImage(buffer)) Draw(g);
            target.DrawImageUnscaled(buffer, 0, 0);
        }
        finally { EndPaint(Handle, ref ps); }
    }

    private void Draw(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? BackColor);

        var ch = new RECT(); SendMessage(Handle, TBM_GETCHANNELRECT, IntPtr.Zero, ref ch);
        var th = new RECT(); SendMessage(Handle, TBM_GETTHUMBRECT, IntPtr.Zero, ref th);
        float cy = (th.T + th.B) / 2f, cx = (th.L + th.R) / 2f;
        float trackH = Math.Max(4f, Height / 7f);
        float x0 = ch.L + trackH / 2, x1 = ch.R - trackH / 2;

        using (var pen = new Pen(TrackColor, trackH) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(pen, x0, cy, x1, cy);
        if (cx > x0)
            using (var pen = new Pen(FillColor, trackH) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(pen, x0, cy, cx, cy);

        // Windows 11 thumb: a white disc with a hairline ring and an accent dot in the middle.
        float r = Math.Min(Height / 2f - 2, (th.B - th.T) / 2f + 2);
        using (var br = new SolidBrush(ThumbColor)) g.FillEllipse(br, cx - r, cy - r, 2 * r, 2 * r);
        using (var pen = new Pen(Color.FromArgb(60, 0, 0, 0))) g.DrawEllipse(pen, cx - r, cy - r, 2 * r, 2 * r);
        float dot = r * 0.55f;
        using (var br = new SolidBrush(FillColor)) g.FillEllipse(br, cx - dot, cy - dot, 2 * dot, 2 * dot);

        if (Focused && ShowFocusCues)
            using (var pen = new Pen(FocusColor, 1.5f)) g.DrawEllipse(pen, cx - r - 2, cy - r - 2, 2 * r + 4, 2 * r + 4);
    }
}
