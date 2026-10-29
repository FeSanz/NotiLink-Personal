using System.Runtime.InteropServices;

namespace NotiLink.App.Interop;

/// <summary>
/// P/Invoke wrapper for the Windows 11 DWM system-backdrop API (the same one
/// WinUI3/Settings/Terminal use) — gives a real translucent blurred-desktop
/// "glass" effect behind the window's transparent areas, not a fake overlay.
/// Silently no-ops on older Windows since DwmSetWindowAttribute just returns
/// a non-zero HRESULT for an unrecognized attribute rather than throwing.
/// </summary>
internal static class DwmApi
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_ACRYLIC = 3;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    public static void EnableAcrylicBackdrop(IntPtr hwnd)
    {
        int value = DWMSBT_ACRYLIC;
        try { DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref value, sizeof(int)); } catch (DllNotFoundException) { }
    }

    public static void SetImmersiveDarkMode(IntPtr hwnd, bool dark)
    {
        int value = dark ? 1 : 0;
        try { DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int)); } catch (DllNotFoundException) { }
    }
}
