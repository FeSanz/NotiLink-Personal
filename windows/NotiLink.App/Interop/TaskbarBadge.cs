using System.Runtime.InteropServices;

namespace NotiLink.App.Interop;

/// <summary>
/// Wraps ITaskbarList3.SetOverlayIcon — the small badge Windows composites onto the corner
/// of a window's actual taskbar button (the same mechanism Teams/Slack/Discord use for their
/// unread-count badges). This is a different surface from the system tray's NotifyIcon: the
/// tray icon sits in the notification-area cluster next to the clock, while this badge lives
/// on the app's own button in the taskbar proper, which is what's actually visible while the
/// window is open or pinned.
/// </summary>
internal static class TaskbarBadge
{
    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    private class TaskbarInstance
    {
    }

    [ComImport]
    [Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);

        // ITaskbarList2
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);

        // ITaskbarList3 (only the one member this app needs; the rest keep the vtable
        // layout correct even though nothing here calls them)
        void SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
        void SetProgressState(IntPtr hwnd, int tbpFlags);
        void RegisterTab(IntPtr hwndTab, IntPtr hwndMDI);
        void UnregisterTab(IntPtr hwndTab);
        void SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
        void SetTabActive(IntPtr hwndTab, IntPtr hwndMDI, uint tbatFlags);
        void ThumbBarAddButtons(IntPtr hwnd, uint cButtons, IntPtr pButtons);
        void ThumbBarUpdateButtons(IntPtr hwnd, uint cButtons, IntPtr pButtons);
        void ThumbBarSetImageList(IntPtr hwnd, IntPtr himl);
        void SetOverlayIcon(IntPtr hwnd, IntPtr hIcon, [MarshalAs(UnmanagedType.LPWStr)] string pszDescription);
    }

    private static ITaskbarList3? _taskbarList;
    private static bool _initFailed;

    private static ITaskbarList3? GetInstance()
    {
        if (_taskbarList != null) return _taskbarList;
        if (_initFailed) return null;
        try
        {
            var instance = (ITaskbarList3)new TaskbarInstance();
            instance.HrInit();
            _taskbarList = instance;
            return instance;
        }
        catch (Exception)
        {
            // Not fatal — the badge is a nice-to-have, not something the rest of the app
            // depends on (e.g. this COM object can be unavailable under some shells/RDP).
            _initFailed = true;
            return null;
        }
    }

    public static void SetOverlay(IntPtr hwnd, IntPtr hIcon, string description)
    {
        try
        {
            GetInstance()?.SetOverlayIcon(hwnd, hIcon, description);
        }
        catch (Exception)
        {
            // Same reasoning as above — a failed badge update should never take the app down.
        }
    }

    public static void ClearOverlay(IntPtr hwnd) => SetOverlay(hwnd, IntPtr.Zero, "");
}
