using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MultiAudio.App;

/// <summary>Reads the live Windows personalization settings (light/dark + accent color)
/// so the app matches the system the way real Windows 11 Settings does.</summary>
public static class ThemeInfo
{
    public static bool IsLight()
    {
        try
        {
            var v = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1);
            return v is int i ? i != 0 : true;
        }
        catch { return true; }
    }

    /// <summary>System accent, adjusted so it reads as text (4.5:1) against <paramref name="bg"/>.</summary>
    public static Color AccentColor(bool dark, Color bg)
    {
        var c = dark ? Color.FromArgb(96, 205, 255) : Color.FromArgb(0, 103, 192);
        try
        {
            // DWM AccentColor is a DWORD stored as 0xAABBGGRR (ABGR).
            var v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "AccentColor", null);
            if (v is int abgr) c = Color.FromArgb(abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF);
        }
        catch { }
        return EnsureContrast(c, bg);
    }

    /// <summary>Black or white, whichever reads better on <paramref name="fill"/>.</summary>
    public static Color OnColor(Color fill) =>
        Contrast(fill, Color.White) >= Contrast(fill, Color.Black) ? Color.White : Color.Black;

    public static double Contrast(Color a, Color b)
    {
        double x = Lum(a) + 0.05, y = Lum(b) + 0.05;
        return Math.Max(x, y) / Math.Min(x, y);
    }

    /// <summary>Moves <paramref name="c"/> toward black or white until it meets <paramref name="min"/> contrast.</summary>
    public static Color EnsureContrast(Color c, Color bg, double min = 4.5)
    {
        var target = Lum(bg) > 0.18 ? Color.Black : Color.White;
        for (int i = 0; i < 20 && Contrast(c, bg) < min; i++) c = Mix(c, target, 0.12);
        Debug.Assert(Contrast(c, bg) >= min, "accent still below contrast target");
        return c;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Dark/light title bar (DWMWA_USE_IMMERSIVE_DARK_MODE, Windows 10 20H1+).</summary>
    public static void ApplyTitleBar(IntPtr hwnd, bool dark)
    {
        int v = dark ? 1 : 0;
        try { DwmSetWindowAttribute(hwnd, 20, ref v, sizeof(int)); } catch { }
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);

    /// <summary>Dark scrollbars on a scrollable control (the undocumented-but-stable theme Explorer uses).</summary>
    public static void ApplyScrollbars(IntPtr hwnd, bool dark)
    {
        try { SetWindowTheme(hwnd, dark ? "DarkMode_Explorer" : "Explorer", null); } catch { }
    }

    private static double Lum(Color c)
    {
        static double Ch(int v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t),
        (int)(a.G + (b.G - a.G) * t),
        (int)(a.B + (b.B - a.B) * t));
}
