using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LeaveMeAlone.Views;

/// <summary>
/// Gives a window the dark caption bar that matches the app theme (Windows 10 20H1+ / 11).
/// Older Windows versions ignore the attributes and keep the default caption.
/// </summary>
public static class WindowTheme
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;
    private const int CaptionColor = 0x001A130F; // COLORREF (0x00BBGGRR) of the sidebar, #0F131A

    public static void UseDarkCaption(Window window)
    {
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Apply(window);
        else window.SourceInitialized += (_, _) => Apply(window);
    }

    private static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var on = 1;
        var color = CaptionColor;
        // Return codes are ignored on purpose: an unsupported attribute just leaves the default look.
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref on, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref color, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
