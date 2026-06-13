//
//  GlobalPushToTalkShortcutMonitor.cs
//  Clicky for Windows
//
//  Captures the push-to-talk shortcut (ctrl + alt, mirroring the Mac's
//  ctrl + option) while Clicky runs in the background, using a low-level
//  keyboard hook (WH_KEYBOARD_LL).
//
//  CRITICAL DESIGN: the hook runs on its OWN dedicated thread with its own
//  message loop — never the UI thread. A low-level keyboard hook must live
//  on a thread that stays responsive; if that thread is busy for even
//  ~300ms (Windows' LowLevelHooksTimeout), Windows silently removes the
//  hook and the hotkey goes dead until restart. The UI thread is constantly
//  busy drawing the buddy and rebuilding the panel, so it is the worst place
//  for the hook. A dedicated pump thread keeps the hook alive forever.
//
//  We also read the true Ctrl/Alt state via GetAsyncKeyState on each event
//  instead of tracking key-up/down deltas, so a single missed event can
//  never leave the shortcut stuck "half pressed".
//
//  Windows needs no special permission for this (unlike macOS Accessibility).
//

using System.Runtime.InteropServices;
using System.Windows.Threading;
using Clicky.Native;

namespace Clicky.Hotkey;

public enum ShortcutTransition
{
    None,
    Pressed,
    Released,
}

public static class PushToTalkShortcut
{
    public const string DisplayText = "ctrl + alt";

    /// Pressed fires when both Ctrl and Alt are held; Released fires when
    /// either lifts. Mirrors the modifier-only logic of the Swift original.
    public static ShortcutTransition ComputeTransition(
        bool isControlCurrentlyDown,
        bool isAltCurrentlyDown,
        bool wasShortcutPreviouslyPressed)
    {
        bool isShortcutCurrentlyPressed = isControlCurrentlyDown && isAltCurrentlyDown;

        if (isShortcutCurrentlyPressed && !wasShortcutPreviouslyPressed)
        {
            return ShortcutTransition.Pressed;
        }
        if (!isShortcutCurrentlyPressed && wasShortcutPreviouslyPressed)
        {
            return ShortcutTransition.Released;
        }
        return ShortcutTransition.None;
    }
}

public sealed class GlobalPushToTalkShortcutMonitor : IDisposable
{
    /// Fired on the UI dispatcher whenever the shortcut is pressed or released.
    public event Action<ShortcutTransition>? ShortcutTransitionOccurred;

    public bool IsShortcutCurrentlyPressed { get; private set; }

    private readonly Dispatcher uiDispatcher;

    private Thread? hookThread;
    private uint hookThreadId;
    private IntPtr keyboardHookHandle = IntPtr.Zero;

    // Rooted so the GC can't collect the native callback target.
    private NativeMethods.LowLevelKeyboardProc? keyboardHookCallback;

    private volatile bool isRunning;

    public GlobalPushToTalkShortcutMonitor()
    {
        uiDispatcher = Dispatcher.CurrentDispatcher;
    }

    public void Start()
    {
        if (isRunning)
        {
            return;
        }
        isRunning = true;

        // The hook is installed INSIDE this thread's proc and the thread then
        // pumps messages forever, so it's always responsive to the hook.
        hookThread = new Thread(HookThreadProc)
        {
            IsBackground = true,
            Name = "ClickyKeyboardHook",
        };
        hookThread.SetApartmentState(ApartmentState.STA);
        hookThread.Start();
    }

    private void HookThreadProc()
    {
        hookThreadId = NativeMethods.GetCurrentThreadId();

        keyboardHookCallback = HandleKeyboardHookEvent;
        IntPtr moduleHandle = NativeMethods.GetModuleHandleW(null);
        keyboardHookHandle = NativeMethods.SetWindowsHookExW(
            NativeMethods.WH_KEYBOARD_LL,
            keyboardHookCallback,
            moduleHandle,
            0);

        if (keyboardHookHandle == IntPtr.Zero)
        {
            System.Diagnostics.Debug.WriteLine(
                $"⚠️ Global push-to-talk: couldn't install keyboard hook (error {Marshal.GetLastWin32Error()})");
            return;
        }

        DebugTrace.Log("hotkey hook installed on dedicated thread");

        // Dedicated message loop — keeps the hook thread responsive so Windows
        // never times out and removes the hook. GetMessage returns 0 on WM_QUIT.
        while (isRunning && NativeMethods.GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(ref message);
            NativeMethods.DispatchMessageW(ref message);
        }

        if (keyboardHookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(keyboardHookHandle);
            keyboardHookHandle = IntPtr.Zero;
        }
        keyboardHookCallback = null;
    }

    public void Stop()
    {
        if (!isRunning)
        {
            return;
        }
        isRunning = false;
        IsShortcutCurrentlyPressed = false;

        // Wake the hook thread's GetMessage loop so it exits and unhooks.
        if (hookThreadId != 0)
        {
            NativeMethods.PostThreadMessageW(hookThreadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }
        hookThread = null;
        hookThreadId = 0;
    }

    public void Dispose() => Stop();

    private IntPtr HandleKeyboardHookEvent(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int message = wParam.ToInt32();
            bool isKeyEvent =
                message == NativeMethods.WM_KEYDOWN || message == NativeMethods.WM_SYSKEYDOWN ||
                message == NativeMethods.WM_KEYUP || message == NativeMethods.WM_SYSKEYUP;

            if (isKeyEvent)
            {
                // Read the TRUE current modifier state rather than tracking
                // deltas — this self-heals if any event was ever missed.
                bool isControlDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0;
                bool isAltDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) & 0x8000) != 0;

                var transition = PushToTalkShortcut.ComputeTransition(
                    isControlDown, isAltDown, IsShortcutCurrentlyPressed);

                if (transition != ShortcutTransition.None)
                {
                    IsShortcutCurrentlyPressed = transition == ShortcutTransition.Pressed;
                    uiDispatcher.BeginInvoke(() => ShortcutTransitionOccurred?.Invoke(transition));
                }
            }
        }

        return NativeMethods.CallNextHookEx(keyboardHookHandle, code, wParam, lParam);
    }
}
