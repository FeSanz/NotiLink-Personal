using System.Runtime.InteropServices;

namespace NotiLink.App.Interop;

/// <summary>
/// Bitmap.GetHicon() allocates a raw GDI HICON that .NET does not own or track —
/// Icon.FromHandle() just wraps it without taking ownership either, so the handle
/// must be destroyed manually once a managed Icon.Clone() has copied its bits,
/// or every tray-badge redraw leaks a GDI handle.
/// </summary>
internal static class IconApi
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public static void Destroy(IntPtr hIcon) => DestroyIcon(hIcon);
}
