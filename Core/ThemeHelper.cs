using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TunnelGate.Core;

public static class ThemeHelper
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    public static void EnableDarkTitleBar(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;
                var enabled = 1;
                var size = sizeof(int);
                if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enabled, size) != 0)
                    _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref enabled, size);
            }
            catch
            {
                // Cosmetic only. Never block application startup because of DWM theming.
            }
        };
    }
}
