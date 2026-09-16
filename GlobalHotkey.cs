using System.Runtime.InteropServices;
using System.Windows.Interop;
using Window = System.Windows.Window;
using WindowState = System.Windows.WindowState;

namespace ResumeBuilder;

/// <summary>
/// A6.6.13 — a system-wide hotkey (Ctrl+Shift+') that brings Resume Builder to the front from any app
/// and puts keyboard focus in the ChatGPT pane, so the user's next Ctrl+Shift+; reaches ChatGPT.
///
/// It only moves focus. It never sends a keystroke to ChatGPT and never copies anything: the copy is
/// still the user's own Ctrl+Shift+; handled by ChatGPT's own feature. A hotkey press is user input,
/// which is what allows Windows to let the app come to the foreground.
/// </summary>
public sealed class GlobalHotkey : IDisposable {
    public const int MOD_CONTROL = 0x0002;
    public const int MOD_SHIFT = 0x0004;
    public const int MOD_NOREPEAT = 0x4000;
    /// <summary>The ' " key on a US keyboard.</summary>
    public const int VK_OEM_7 = 0xDE;
    public const int Modifiers = MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT;
    public const string DisplayText = "Ctrl+Shift+'";

    const int WM_HOTKEY = 0x0312;
    const int HotkeyId = 0x5242;   // "RB"

    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);
    [DllImport("user32.dll", SetLastError = true)] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);

    HwndSource? _source;
    IntPtr _handle = IntPtr.Zero;

    /// <summary>Raised on the UI thread when the user presses the hotkey anywhere in Windows.</summary>
    public event Action? Pressed;

    public bool IsRegistered { get; private set; }

    /// <summary>Win32 error when registration failed — usually another app already owns the combination.</summary>
    public int LastError { get; private set; }

    public bool Register(Window window) {
        if (IsRegistered) return true;
        try {
            _handle = new WindowInteropHelper(window).EnsureHandle();
            _source = HwndSource.FromHwnd(_handle);
            if (_source is null) return false;
            _source.AddHook(WndProc);

            IsRegistered = RegisterHotKey(_handle, HotkeyId, Modifiers, VK_OEM_7);
            if (!IsRegistered) {
                LastError = Marshal.GetLastWin32Error();
                _source.RemoveHook(WndProc);
                _source = null;
            }
            return IsRegistered;
        } catch {
            IsRegistered = false;
            return false;
        }
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId) {
            handled = true;
            try { Pressed?.Invoke(); } catch { /* a focus problem must never break the message loop */ }
        }
        return IntPtr.Zero;
    }

    /// <summary>Restores and activates the window. Called from the hotkey, so Windows permits it.</summary>
    public static void BringToFront(Window window) {
        try {
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            if (!window.IsVisible) window.Show();
            window.Activate();
            SetForegroundWindow(new WindowInteropHelper(window).Handle);
        } catch {
            // Focus is a convenience; failing to take it must not affect a job.
        }
    }

    public void Dispose() {
        try {
            if (IsRegistered && _handle != IntPtr.Zero) UnregisterHotKey(_handle, HotkeyId);
            _source?.RemoveHook(WndProc);
        } catch {
            // Shutting down.
        }
        IsRegistered = false;
        _source = null;
        _handle = IntPtr.Zero;
    }
}
