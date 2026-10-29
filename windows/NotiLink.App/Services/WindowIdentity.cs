using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;

namespace NotiLink.App.Services;

/// <summary>
/// Gives each scrcpy window its own look in the Windows taskbar: the shared app's own icon (or
/// NotiLink's) instead of scrcpy's Android logo.
///
/// Two things were wrong before: (1) the icon handle was disposed right after WM_SETICON, so the
/// window was left pointing at a destroyed icon and Windows fell back to scrcpy's; (2) every scrcpy
/// window shares scrcpy.exe's taskbar group, whose button shows the exe's icon. Each window now
/// gets its own AppUserModelID (its own taskbar button) and an icon kept alive for the app's
/// lifetime, re-applied a few times since SDL can set its own icon right after the window appears.
/// </summary>
internal static class WindowIdentity
{
    private static readonly Dictionary<string, Icon> IconCache = new();
    private static readonly object Gate = new();

    public static Icon? NotiLinkIcon()
    {
        lock (Gate)
        {
            if (IconCache.TryGetValue("notilink", out var cached)) return cached;
            try
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath)) return null;
                var icon = Icon.ExtractAssociatedIcon(exePath);
                if (icon != null) IconCache["notilink"] = icon;
                return icon;
            }
            catch (Exception) { return null; }
        }
    }

    /// <summary>An app's PNG icon (as synced from the phone) as a window icon; null if unusable.</summary>
    public static Icon? FromPng(string cacheKey, byte[]? png)
    {
        if (png == null || png.Length == 0) return null;
        lock (Gate)
        {
            if (IconCache.TryGetValue(cacheKey, out var cached)) return cached;
            try
            {
                using var ms = new MemoryStream(png);
                using var src = new Bitmap(ms);
                using var bmp = new Bitmap(src, new Size(64, 64));
                // GetHicon's handle is intentionally never destroyed: the window keeps using it.
                var icon = Icon.FromHandle(bmp.GetHicon());
                IconCache[cacheKey] = icon;
                return icon;
            }
            catch (Exception) { return null; }
        }
    }

    /// <summary>[icon] with a small colored circle + the phone's identifier (DeviceBadge) in its
    /// bottom-right corner, so the same app shared from two phones gets two distinguishable taskbar icons.</summary>
    public static Icon? WithDeviceBadge(Icon? icon, string iconKey, string deviceId, string deviceName)
    {
        if (icon == null) return null;
        var (text, colorHex, _) = DeviceBadge.Resolve(deviceId, deviceName);
        var cacheKey = $"{iconKey}|badge|{text}|{colorHex}";
        lock (Gate)
        {
            if (IconCache.TryGetValue(cacheKey, out var cached)) return cached;
            try
            {
                using var bmp = new Bitmap(64, 64);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    using (var baseBmp = icon.ToBitmap()) g.DrawImage(baseBmp, 0, 0, 64, 64);
                    var color = ColorTranslator.FromHtml(colorHex);
                    var badge = new Rectangle(34, 34, 29, 29);
                    using (var white = new SolidBrush(Color.White)) g.FillEllipse(white, Rectangle.Inflate(badge, 2, 2));
                    using (var fill = new SolidBrush(color)) g.FillEllipse(fill, badge);
                    using var font = new Font("Segoe UI", text.Length > 1 ? 12f : 15f, FontStyle.Bold, GraphicsUnit.Pixel);
                    using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    using var textBrush = new SolidBrush(Color.White);
                    g.DrawString(text, font, textBrush, new RectangleF(badge.X, badge.Y + 1, badge.Width, badge.Height), fmt);
                }
                var result = Icon.FromHandle(bmp.GetHicon());
                IconCache[cacheKey] = result;
                return result;
            }
            catch (Exception) { return icon; }
        }
    }

    /// <param name="bringToFront">A just-opened share: put its window in front once it appears
    /// (see BringToFront). [keepTopmost] = the user chose "siempre encima", leave it that way.</param>
    public static async Task ApplyWhenReadyAsync(Process process, string appUserModelId, Icon? icon,
        bool bringToFront = false, bool keepTopmost = false)
    {
        IntPtr hwnd = IntPtr.Zero;
        for (var i = 0; i < 40 && !process.HasExited; i++)
        {
            process.Refresh();
            hwnd = process.MainWindowHandle;
            if (hwnd != IntPtr.Zero) break;
            await Task.Delay(200).ConfigureAwait(false);
        }
        if (hwnd == IntPtr.Zero) return;
        SetAppUserModelId(hwnd, appUserModelId);
        foreach (var delay in new[] { 0, 800, 2500 })
        {
            if (delay > 0) await Task.Delay(delay).ConfigureAwait(false);
            if (process.HasExited) return;
            if (icon != null)
            {
                SendMessage(hwnd, WmSetIcon, (IntPtr)IconBig, icon.Handle);
                SendMessage(hwnd, WmSetIcon, (IntPtr)IconSmall, icon.Handle);
            }
            // Twice at most: SDL may still be settling its window right after it first shows.
            if (bringToFront && delay < 2500 && GetForegroundWindow() != hwnd) BringToFront(hwnd, keepTopmost);
        }
    }

    /// <summary>
    /// Shows [hwnd] above the other windows and gives it focus, without leaving it always on top.
    /// Windows refuses SetForegroundWindow to a background process (the usual case here: the share
    /// was started from the phone, so NotiLink isn't the active app and the scrcpy window opened
    /// behind everything). Two things get around that: a brief TOPMOST → NOTOPMOST flip changes
    /// the z-order regardless of focus, and attaching to the current foreground thread's input
    /// lets the focus change go through.
    /// </summary>
    public static void BringToFront(IntPtr hwnd, bool keepTopmost = false)
    {
        try
        {
            if (IsIconic(hwnd)) ShowWindow(hwnd, SwRestore);
            SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
            if (!keepTopmost) SetWindowPos(hwnd, HwndNoTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);

            var foreground = GetForegroundWindow();
            var foregroundThread = foreground == IntPtr.Zero ? 0u : GetWindowThreadProcessId(foreground, out _);
            var thisThread = GetCurrentThreadId();
            var attached = foregroundThread != 0 && foregroundThread != thisThread && AttachThreadInput(thisThread, foregroundThread, true);
            try
            {
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached) AttachThreadInput(thisThread, foregroundThread, false);
            }
        }
        catch (Exception) { }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNoTopmost = new(-2);
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpShowWindow = 0x0040;
    private const int SwRestore = 9;

    // --- interop ---

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private const uint WmSetIcon = 0x0080;
    private const int IconSmall = 0;
    private const int IconBig = 1;

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount(out uint count);
        int GetAt(uint index, out PropertyKey key);
        int GetValue(ref PropertyKey key, out PropVariant value);
        int SetValue(ref PropertyKey key, ref PropVariant value);
        int Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VarType;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    private const ushort VtLpwstr = 31;

    private static void SetAppUserModelId(IntPtr hwnd, string id)
    {
        try
        {
            var iid = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0 || store == null) return;
            var key = new PropertyKey { FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), PropertyId = 5 };
            var value = new PropVariant { VarType = VtLpwstr, Pointer = Marshal.StringToCoTaskMemUni(id) };
            try
            {
                store.SetValue(ref key, ref value);
                store.Commit();
            }
            finally
            {
                Marshal.FreeCoTaskMem(value.Pointer);
                Marshal.ReleaseComObject(store);
            }
        }
        catch (Exception)
        {
            // Cosmetic only.
        }
    }
}
