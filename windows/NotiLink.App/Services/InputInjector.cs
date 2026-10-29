using System.Runtime.InteropServices;
using NotiLink.Core.Networking;

namespace NotiLink.App.Services;

/// <summary>The controlled side of PC ↔ PC remote control: turns a viewer's PeerInput into real
/// mouse/keyboard input with SendInput — the same API remote-desktop tools use. Coordinates are
/// 0..1 across the primary monitor (the one ScreenCaptureService shares), mapped to the virtual
/// desktop in physical pixels (the app is PerMonitorV2-aware).
///
/// Windows limits that apply no matter what: input can't reach windows running as administrator
/// unless NotiLink itself does (UIPI), nor the secure desktop (UAC prompt, lock screen,
/// Ctrl+Alt+Supr) — those simply ignore it.</summary>
internal static class InputInjector
{
    private static readonly HashSet<int> HeldKeys = new();
    private static readonly HashSet<string> HeldButtons = new();

    public static void Inject(PeerInput input)
    {
        switch (input.Type)
        {
            case "move":
                SendMouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, input.X, input.Y, 0);
                break;
            case "down":
            case "up":
            {
                var down = input.Type == "down";
                var (flag, data) = ButtonFlags(input.Button, down);
                if (flag == 0) return;
                lock (HeldButtons) { if (down) HeldButtons.Add(input.Button!); else HeldButtons.Remove(input.Button!); }
                SendMouse(flag | MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, input.X, input.Y, data);
                break;
            }
            case "wheel":
                SendMouse(MOUSEEVENTF_WHEEL, 0, 0, (uint)input.Delta, absolute: false);
                break;
            case "key":
                if (input.Vk is <= 0 or > 254) return;
                lock (HeldKeys) { if (input.Down) HeldKeys.Add(input.Vk); else HeldKeys.Remove(input.Vk); }
                SendKey((ushort)input.Vk, input.Down);
                break;
        }
    }

    /// <summary>Lets go of every key/button a viewer left pressed (control ended mid-drag, the
    /// link dropped with Shift held, ...) so nothing stays stuck down on this PC.</summary>
    public static void ReleaseAll()
    {
        int[] keys;
        string[] buttons;
        lock (HeldKeys) { keys = HeldKeys.ToArray(); HeldKeys.Clear(); }
        lock (HeldButtons) { buttons = HeldButtons.ToArray(); HeldButtons.Clear(); }
        foreach (var vk in keys) SendKey((ushort)vk, down: false);
        foreach (var b in buttons)
        {
            var (flag, data) = ButtonFlags(b, down: false);
            if (flag != 0) SendMouse(flag, 0, 0, data, absolute: false);
        }
    }

    private static (uint Flag, uint Data) ButtonFlags(string? button, bool down) => button switch
    {
        "left" => (down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP, 0u),
        "right" => (down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP, 0u),
        "middle" => (down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP, 0u),
        "x1" => (down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, XBUTTON1),
        "x2" => (down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, XBUTTON2),
        _ => (0u, 0u),
    };

    private static void SendMouse(uint flags, double x, double y, uint data, bool absolute = true)
    {
        int dx = 0, dy = 0;
        if (absolute)
        {
            var b = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? System.Drawing.Rectangle.Empty;
            if (b.Width <= 0) return;
            var px = b.Left + x * (b.Width - 1);
            var py = b.Top + y * (b.Height - 1);
            int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int vw = Math.Max(2, GetSystemMetrics(SM_CXVIRTUALSCREEN)), vh = Math.Max(2, GetSystemMetrics(SM_CYVIRTUALSCREEN));
            dx = (int)Math.Round((px - vx) * 65535.0 / (vw - 1));
            dy = (int)Math.Round((py - vy) * 65535.0 / (vh - 1));
        }
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags } },
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static void SendKey(ushort vk, bool down)
    {
        var flags = down ? 0u : KEYEVENTF_KEYUP;
        if (IsExtended(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC), dwFlags = flags } },
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    // Keys whose scan code needs the E0 prefix: arrows, Ins/Del/Home/End/PgUp/PgDn, right
    // Ctrl/Alt, Windows keys, Apps (menu), numpad divide, NumLock, Print Screen.
    private static bool IsExtended(ushort vk) =>
        vk is >= 0x21 and <= 0x28 or 0x2D or 0x2E or 0x2C or 0x5B or 0x5C or 0x5D or 0x6F or 0x90 or 0xA3 or 0xA5;

    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004,
        MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010, MOUSEEVENTF_MIDDLEDOWN = 0x0020,
        MOUSEEVENTF_MIDDLEUP = 0x0040, MOUSEEVENTF_XDOWN = 0x0080, MOUSEEVENTF_XUP = 0x0100,
        MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_VIRTUALDESK = 0x4000, MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint XBUTTON1 = 1, XBUTTON2 = 2;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2;
    private const uint MAPVK_VK_TO_VSC = 0;
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint uCode, uint uMapType);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
}
