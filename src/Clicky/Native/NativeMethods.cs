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

    /// Applies capture exclusion unless a `debug-capture.flag` file sits
    /// next to the exe. The flag exists for automated visual verification —
    /// capture exclusion makes the overlay invisible to screenshots, which
    /// is correct in production but makes the buddy untestable by tools
    /// that see the screen the same way screenshots do.
    public static void ApplyCaptureExclusion(IntPtr windowHandle)
    {
        bool debugCaptureFlagPresent = System.IO.File.Exists(
            System.IO.Path.Combine(AppContext.BaseDirectory, "debug-capture.flag"));
        if (!debugCaptureFlagPresent)
        {
            SetWindowDisplayAffinity(windowHandle, WDA_EXCLUDEFROMCAPTURE);
        }
    }

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
    public const int VK_CONTROL = 0x11; // Either Ctrl
    public const int VK_MENU = 0x12;    // Either Alt

    /// Reads the true current up/down state of a key. Using this in the hook
    /// (instead of tracking key-up/down deltas ourselves) means we can never
    /// desync — if the hook ever misses an event, the next keystroke still
    /// reads the real Ctrl/Alt state and recovers.
    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int virtualKey);

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

    // ── Message pump (for the dedicated keyboard-hook thread) ────────
    // The hook must live on a thread that constantly pumps messages, or
    // Windows silently removes it the moment that thread stalls. We give it
    // its own thread with a GetMessage loop so app/UI work can never kill it.

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr WindowHandle;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public POINT Point;
    }

    public const uint WM_QUIT = 0x0012;

    [DllImport("user32.dll")]
    public static extern int GetMessageW(out MSG message, IntPtr windowHandle, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TranslateMessage(ref MSG message);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessageW(ref MSG message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostThreadMessageW(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();
}
