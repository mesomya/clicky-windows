//
//  GlobalPushToTalkShortcutMonitor.cs
//  Clicky for Windows
//
//  Captures the push-to-talk shortcut while Clicky is running in the
//  background. Uses a listen-only low-level keyboard hook (WH_KEYBOARD_LL) —
//  the Windows equivalent of the original's listen-only CGEvent tap — so the
//  modifier-only shortcut (ctrl + alt, mirroring the Mac's ctrl + option)
//  is detected reliably regardless of which app has focus.
//
//  Unlike macOS, no Accessibility permission is needed for this on Windows.
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

    /// Computes the shortcut transition for the current modifier state.
    /// Pressed fires when both Ctrl and Alt become held; Released fires
    /// when either one lifts. Mirrors BuddyPushToTalkShortcut's
    /// modifier-only flagsChanged logic from the Swift original.
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

    private IntPtr keyboardHookHandle = IntPtr.Zero;

    // The delegate must be stored in a field — if it were only passed inline,
    // the GC could collect it while Windows still holds the native callback
    // pointer, crashing the app on the next keystroke.
    private NativeMethods.LowLevelKeyboardProc? keyboardHookCallback;

    private readonly Dispatcher uiDispatcher;

    // Modifier state tracked from the raw key stream. Left/right variants are
    // tracked separately so e.g. holding LCtrl and RAlt still counts.
    private bool isLeftControlDown;
    private bool isRightControlDown;
    private bool isLeftAltDown;
    private bool isRightAltDown;

    public GlobalPushToTalkShortcutMonitor()
    {
        uiDispatcher = Dispatcher.CurrentDispatcher;
    }

    public void Start()
    {
        // If the hook is already installed, don't reinstall it — that would
        // reset the pressed state mid-hold and kill the waveform overlay.
        if (keyboardHookHandle != IntPtr.Zero)
        {
            return;
        }

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
        }
    }

    public void Stop()
    {
        IsShortcutCurrentlyPressed = false;
        isLeftControlDown = isRightControlDown = isLeftAltDown = isRightAltDown = false;

        if (keyboardHookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(keyboardHookHandle);
            keyboardHookHandle = IntPtr.Zero;
            keyboardHookCallback = null;
        }
    }

    public void Dispose() => Stop();

    private IntPtr HandleKeyboardHookEvent(int code, IntPtr wParam, IntPtr lParam)
    {
        // The hook callback must stay extremely fast — Windows silently removes
        // hooks that block the input pipeline. We only update four booleans and
        // post any transition to the dispatcher asynchronously.
        if (code >= 0)
        {
            int message = wParam.ToInt32();
            bool isKeyDownMessage = message == NativeMethods.WM_KEYDOWN || message == NativeMethods.WM_SYSKEYDOWN;
            bool isKeyUpMessage = message == NativeMethods.WM_KEYUP || message == NativeMethods.WM_SYSKEYUP;

            if (isKeyDownMessage || isKeyUpMessage)
            {
                var keyboardEvent = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);

                switch (keyboardEvent.VkCode)
                {
                    case NativeMethods.VK_LCONTROL: isLeftControlDown = isKeyDownMessage; break;
                    case NativeMethods.VK_RCONTROL: isRightControlDown = isKeyDownMessage; break;
                    case NativeMethods.VK_LMENU: isLeftAltDown = isKeyDownMessage; break;
                    case NativeMethods.VK_RMENU: isRightAltDown = isKeyDownMessage; break;
                }

                var transition = PushToTalkShortcut.ComputeTransition(
                    isControlCurrentlyDown: isLeftControlDown || isRightControlDown,
                    isAltCurrentlyDown: isLeftAltDown || isRightAltDown,
                    wasShortcutPreviouslyPressed: IsShortcutCurrentlyPressed);

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
