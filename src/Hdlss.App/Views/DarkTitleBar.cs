using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Hdlss.App.Views;

/// <summary>
/// Makes the native title bar match the dark theme. Windows 10 20H1+ honours the dark-mode flag;
/// Windows 11 also takes an exact caption/border/text colour. Older systems simply ignore the calls.
/// </summary>
public static class DarkTitleBar
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Applies the dark title bar to every window the app opens.</summary>
    public static void Register()
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => Apply((Window)s)));
    }

    public static void Attach(Window window)
    {
        // SourceInitialized runs before the window is first painted: no white flash.
        window.SourceInitialized += (_, _) => Apply(window);
    }

    public static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int on = 1;
        if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref on, sizeof(int));
        int caption = Colorref(0x0F, 0x11, 0x15), border = Colorref(0x2C, 0x32, 0x40), text = Colorref(0xE8, 0xEA, 0xF0);
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, sizeof(int));
    }

    private static int Colorref(byte r, byte g, byte b) => r | (g << 8) | (b << 16);
}
