using System.Drawing;
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

    public static Color AccentColor(bool dark)
    {
        try
        {
            // DWM AccentColor is a DWORD stored as 0xAABBGGRR (ABGR).
            var v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "AccentColor", null);
            if (v is int argb)
            {
                int r = argb & 0xFF, g = (argb >> 8) & 0xFF, b = (argb >> 16) & 0xFF;
                var c = Color.FromArgb(r, g, b);
                // On dark backgrounds a very dark accent is hard to see — lighten if needed.
                if (dark && (c.R + c.G + c.B) < 240) c = Lighten(c, 0.35);
                return c;
            }
        }
        catch { }
        return dark ? Color.FromArgb(96, 205, 255) : Color.FromArgb(0, 103, 192);
    }

    private static Color Lighten(Color c, double amt) => Color.FromArgb(
        (int)(c.R + (255 - c.R) * amt),
        (int)(c.G + (255 - c.G) * amt),
        (int)(c.B + (255 - c.B) * amt));
}
