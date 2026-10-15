using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DrangedAddons;

/// <summary>Gives a window Windows' dark title bar, coloured to match the app's background on Windows 11.</summary>
public static class DarkTitleBar
{
    const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19; // Windows 10 before 20H1
    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    const int DWMWA_CAPTION_COLOR = 35;                // Windows 11
    const int AppBackground = 0x001D1816;              // #16181D as COLORREF (0x00BBGGRR)

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window) =>
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            var on = 1;
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref on, sizeof(int));
            var color = AppBackground;
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref color, sizeof(int)); // ignored before Windows 11
        };
}
