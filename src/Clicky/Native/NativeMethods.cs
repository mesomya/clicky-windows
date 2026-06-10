//
//  NativeMethods.cs
//  Clicky for Windows
//
//  Win32 interop used across the app: cursor position, low-level keyboard
//  hook for the global push-to-talk shortcut, and the extended window styles
//  that make the overlay click-through, non-activating, and invisible to
//  screen capture (so the buddy never appears in its own screenshots —
//  the Windows equivalent of the original excluding its own windows from
//  ScreenCaptureKit).
//

using System.Runtime.InteropServices;

namespace Clicky.Native;

public static class NativeMethods
{
    // ── Cursor ───────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT point);

    // ── Extended window styles ───────────────────────────────────────

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x00000020;   // Click-through
    public const int WS_EX_TOOLWINDOW = 0x00000080;    // No alt-tab entry
    public const int WS_EX_NOACTIVATE = 0x08000000;    // Never steals focus
    public const int WS_EX_LAYERED = 0x00080000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr windowHandle, int index, IntPtr newValue);

    // ── Screen-capture exclusion ─────────────────────────────────────

    /// Windows 10 2004+ — windows with this affinity are simply not rendered
    /// into screenshots or screen recordings, which is how the buddy stays
    /// out of the screen captures sent to the AI.
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowDisplayAffinity(IntPtr windowHandle, uint affinity);

    // ── Window positioning ───────────────────────────────────────────

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfterWindowHandle,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    // ── Low-level keyboard hook ──────────────────────────────────────

    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;

    public const int VK_LCONTROL = 0xA2;
    public const int VK_RCONTROL = 0xA3;
    public const int VK_LMENU = 0xA4;   // Left Alt
    public const int VK_RMENU = 0xA5;   // Right Alt

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    public delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookExW(int hookId, LowLevelKeyboardProc callback, IntPtr moduleHandle, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hookHandle, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string? moduleName);
}
