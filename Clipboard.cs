using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Interop;
using System.Windows.Threading;
using Window = System.Windows.Window;
// UseWindowsForms also imports System.Windows.Forms, so the WPF types are aliased explicitly.
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;

namespace ResumeBuilder;

/// <summary>Outcome of a single clipboard operation, including a message suitable for the UI.</summary>
public sealed class ClipboardResult {
    public bool Success { get; init; }
    public int Attempts { get; init; }
    public string Message { get; init; } = "";
    public string? Error { get; init; }
}

/// <summary>
/// The single clipboard implementation for the whole application. Every clipboard read and write
/// goes through here; no window or service talks to Clipboard directly.
///
/// Windows only allows one process to own the clipboard at a time, and another application
/// (Office, a remote-desktop or clipboard-manager utility, a browser) can hold it open for short
/// periods. While it is held, OleSetClipboard fails with CLIPBRD_E_CANT_OPEN (0x800401D0), surfaced
/// by WPF as ExternalException / COMException. The fixes applied here:
///
///   1. Clipboard calls are OLE calls and must run on an STA thread. Work is marshalled onto the
///      WPF UI dispatcher; if there is no dispatcher (background/console callers) a dedicated STA
///      thread is used instead, so a background caller can never fail for apartment reasons.
///   2. ExternalException is caught, not just COMException. COMException derives from it, so the
///      previous COMException-only handler let plain ExternalException escape as a hard failure.
///   3. Data is published with SetDataObject rather than SetText, and the OleFlushClipboard step is
///      run separately and treated as best effort. Measured on this machine: OleSetClipboard
///      succeeds and the text is genuinely on the clipboard, then the flush throws
///      CLIPBRD_E_CANT_OPEN — so folding the flush into the copy (copy: true) reports a perfectly
///      good copy as a failure. That is what produced the old "clipboard busy" messages.
///   4. Success is decided by reading the clipboard back, not by the absence of an exception.
///      A silent no-op is retried; a copy that worked despite a throwing API is reported as copied.
///   5. Retries use a short backoff under an overall time budget, and a final fallback goes through
///      the WinForms clipboard, which applies its own native retry loop.
/// </summary>
public static class ClipboardService {
    // WPF already retries OleSetClipboard 10 times at 100 ms internally, so a failing attempt can
    // cost ~1 s on its own. These outer values keep the worst case bounded for the UI thread.
    const int MaxAttempts = 3;
    static readonly TimeSpan RetryBudget = TimeSpan.FromSeconds(3);
    static readonly TimeSpan DispatcherTimeout = TimeSpan.FromSeconds(15);
    static readonly TimeSpan StaThreadTimeout = TimeSpan.FromSeconds(15);

    /// <summary>CLIPBRD_E_CANT_OPEN — another process currently owns the clipboard.</summary>
    const int ClipboardCannotOpen = unchecked((int)0x800401D0);

    // ---------- public API ----------

    /// <summary>Copies text to the clipboard. Never throws.</summary>
    public static ClipboardResult SetText(string text) {
        if (string.IsNullOrEmpty(text))
            return new ClipboardResult { Success = false, Message = "There was nothing to copy." };
        try {
            return RunOnClipboardThread(() => SetCore(text));
        } catch (Exception ex) {
            return new ClipboardResult {
                Success = false,
                Message = "The Windows clipboard could not be reached. " + FallbackHint,
                Error = ex.Message
            };
        }
    }

    /// <summary>Compatibility wrapper for callers that only need pass/fail.</summary>
    public static bool TrySetText(string text) => SetText(text).Success;

    /// <summary>Reads text from the clipboard, or null when it holds no text or stays locked.</summary>
    public static string? TryGetText() {
        try {
            return RunOnClipboardThread(GetCore);
        } catch {
            return null;
        }
    }

    public const string FallbackHint =
        "The prepared input is still saved and shown in Settings → Job Details, so nothing was lost.";

    // ---------- STA / dispatcher marshalling ----------

    static T RunOnClipboardThread<T>(Func<T> work) {
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is not null && !dispatcher.HasShutdownStarted) {
            if (dispatcher.CheckAccess()) return work();
            // Time-limited so a busy UI thread can never hang a background caller forever.
            return dispatcher.Invoke(work, DispatcherPriority.Normal, CancellationToken.None, DispatcherTimeout);
        }

        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) return work();
        return RunOnStaThread(work);
    }

    static T RunOnStaThread<T>(Func<T> work) {
        T result = default!;
        ExceptionDispatchInfo? failure = null;

        var thread = new Thread(() => {
            try { result = work(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(StaThreadTimeout))
            throw new TimeoutException("The clipboard did not respond within " + StaThreadTimeout.TotalSeconds + " seconds.");

        failure?.Throw();
        return result;
    }

    // ---------- core operations (always on an STA thread) ----------

    static ClipboardResult SetCore(string text) {
        Exception? last = null;
        var clock = Stopwatch.StartNew();
        int attempt = 0;

        while (attempt < MaxAttempts) {
            attempt++;
            var published = false;
            try {
                // copy: false performs the OleSetClipboard only. The flush is done separately below,
                // because OleFlushClipboard routinely fails with CLIPBRD_E_CANT_OPEN on machines that
                // run a clipboard manager or remote-desktop agent *even when the data was placed
                // successfully*. Folding it in here would report a good copy as a failure.
                var data = new DataObject(DataFormats.UnicodeText, text);
                Clipboard.SetDataObject(data, false);
                published = true;
            } catch (Exception ex) {
                last = ex;
                if (!IsTransient(ex)) break;
            }

            // Best effort: make the content outlive this process. Never fatal.
            TryFlush();

            // The clipboard itself is the source of truth, not whether an API call threw.
            switch (Verify(text)) {
                case VerifyResult.Match:
                    return Ok(attempt);
                case VerifyResult.Unknown when published:
                    // The write did not throw and the clipboard cannot be read back right now.
                    return Ok(attempt);
                case VerifyResult.Mismatch:
                    last ??= new InvalidOperationException("The clipboard still reported different content after the copy.");
                    break;
            }

            if (clock.Elapsed >= RetryBudget) break;
            Thread.Sleep(BackoffMs(attempt));
        }

        // Last resort: the WinForms clipboard, which applies its own native retry loop.
        try {
            System.Windows.Forms.Clipboard.SetDataObject(text, true, 5, 120);
        } catch (Exception ex) {
            last = ex;
        }
        if (Verify(text) == VerifyResult.Match)
            return new ClipboardResult {
                Success = true,
                Attempts = attempt + 1,
                Message = "Copied to clipboard after retrying a locked clipboard."
            };

        return new ClipboardResult {
            Success = false,
            Attempts = attempt,
            Message = Explain(last) + " " + FallbackHint,
            Error = last?.Message
        };
    }

    static ClipboardResult Ok(int attempt) => new() {
        Success = true,
        Attempts = attempt,
        Message = attempt == 1 ? "Copied to clipboard." : $"Copied to clipboard after {attempt} attempts."
    };

    /// <summary>
    /// Publishes the data so it survives this process exiting. This fails far more often than the
    /// copy itself, so a failure here is ignored: the text is already on the clipboard either way.
    /// </summary>
    static void TryFlush() {
        try { Clipboard.Flush(); } catch { /* content is on the clipboard; persistence is a bonus */ }
    }

    static string? GetCore() {
        var clock = Stopwatch.StartNew();
        for (int attempt = 1; attempt <= MaxAttempts; attempt++) {
            try {
                return Clipboard.ContainsText() ? Clipboard.GetText() : null;
            } catch (Exception ex) {
                if (!IsTransient(ex)) return null;
            }
            if (clock.Elapsed >= RetryBudget) break;
            Thread.Sleep(BackoffMs(attempt));
        }
        return null;
    }

    enum VerifyResult { Match, Mismatch, Unknown }

    /// <summary>Reads the clipboard back, so the decision is based on its real contents.</summary>
    static VerifyResult Verify(string expected) {
        try {
            if (!Clipboard.ContainsText()) return VerifyResult.Mismatch;
            return Normalize(Clipboard.GetText()) == Normalize(expected) ? VerifyResult.Match : VerifyResult.Mismatch;
        } catch {
            // The clipboard is locked for reading too; this attempt proves nothing either way.
            return VerifyResult.Unknown;
        }
    }

    /// <summary>Clipboard round-trips can normalize line endings, which is not a content difference.</summary>
    static string Normalize(string value) => value.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd();

    static int BackoffMs(int attempt) => Math.Min(60 * attempt, 250);

    /// <summary>Clipboard contention surfaces as ExternalException (COMException derives from it).</summary>
    static bool IsTransient(Exception ex) => ex switch {
        ExternalException => true,                 // includes COMException / CLIPBRD_E_CANT_OPEN
        InvalidOperationException => true,          // WPF wraps some OLE failures this way
        ThreadStateException => true,
        OutOfMemoryException => true,               // very large payloads can transiently fail HGLOBAL alloc
        _ => false
    };

    static string Explain(Exception? ex) {
        if (ex is ExternalException external && external.ErrorCode == ClipboardCannotOpen)
            return "Another application is holding the Windows clipboard open (CLIPBRD_E_CANT_OPEN), so the copy did not complete.";
        if (ex is null) return "The copy did not complete.";
        return "The copy did not complete (" + ex.GetType().Name + ").";
    }
}

/// <summary>
/// Watches for clipboard changes while a request is waiting for an answer, so clicking the AI's own
/// Copy button is enough to bring the response back into the application.
///
/// Scoping matters here. The watcher is ARMED only between sending a request and capturing its
/// answer, it reads through ClipboardService (no second clipboard implementation), it ignores the
/// text this application itself put on the clipboard, and text that is not recognisably a profile is
/// discarded without being stored anywhere. It is event-driven via WM_CLIPBOARDUPDATE, so it never
/// polls the clipboard and cannot contend with the user's own copying.
/// </summary>
public sealed class ClipboardWatcher : IDisposable {
    const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll", SetLastError = true)] static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    HwndSource? _source;
    IntPtr _handle = IntPtr.Zero;
    bool _listening;
    string? _ignore;
    string? _lastSeen;

    /// <summary>Raised on the UI thread with clipboard text that arrived while armed.</summary>
    public event Action<string>? TextCaptured;

    public bool IsArmed { get; private set; }
    public bool IsListening => _listening;

    /// <summary>Hooks the window's message loop. Safe to call once the window handle exists.</summary>
    public bool Attach(Window window) {
        if (_listening) return true;
        try {
            _handle = new WindowInteropHelper(window).EnsureHandle();
            _source = HwndSource.FromHwnd(_handle);
            if (_source is null) return false;
            _source.AddHook(WndProc);
            _listening = AddClipboardFormatListener(_handle);
            if (!_listening) _source.RemoveHook(WndProc);
            return _listening;
        } catch {
            _listening = false;
            return false;
        }
    }

    /// <summary>Starts capturing. <paramref name="ignoreText"/> is what this app just copied itself.</summary>
    public void Arm(string? ignoreText) {
        _ignore = ignoreText;
        _lastSeen = ignoreText;
        IsArmed = true;
    }

    public void Disarm() {
        IsArmed = false;
        _ignore = null;
        _lastSeen = null;
    }

    /// <summary>Decision logic, kept pure so it can be tested without a window or a message pump.</summary>
    public static bool ShouldCapture(bool armed, string? text, string? ignoreText, string? lastSeen) {
        if (!armed || string.IsNullOrWhiteSpace(text)) return false;
        if (Same(text, ignoreText)) return false;
        if (Same(text, lastSeen)) return false;
        return true;
    }

    static bool Same(string? a, string? b) {
        if (a is null || b is null) return false;
        return string.Equals(a.Replace("\r\n", "\n").TrimEnd(), b.Replace("\r\n", "\n").TrimEnd(), StringComparison.Ordinal);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
        if (msg != WM_CLIPBOARDUPDATE || !IsArmed) return IntPtr.Zero;
        try {
            var text = ClipboardService.TryGetText();
            if (ShouldCapture(IsArmed, text, _ignore, _lastSeen)) {
                _lastSeen = text;
                TextCaptured?.Invoke(text!);
            }
        } catch {
            // A clipboard read failure must never break the window's message loop.
        }
        return IntPtr.Zero;
    }

    public void Dispose() {
        Disarm();
        try {
            if (_listening && _handle != IntPtr.Zero) RemoveClipboardFormatListener(_handle);
            _source?.RemoveHook(WndProc);
        } catch {
            // Shutting down; nothing useful to do.
        }
        _listening = false;
        _source = null;
        _handle = IntPtr.Zero;
    }
}
