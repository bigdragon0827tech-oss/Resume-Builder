namespace ResumeBuilder;

/// <summary>
/// Identifies one ChatGPT WebView2 instance so delayed focus/copy continuations from a recycled
/// browser cannot act on a newer one (or on a null view after dispose).
/// </summary>
public sealed class BrowserGeneration {
    int _generation;

    /// <summary>Current live generation. 0 means no browser has been created yet.</summary>
    public int Current => _generation;

    /// <summary>Call when a new WebView2 is created and becomes the live instance.</summary>
    public int BeginNew() => System.Threading.Interlocked.Increment(ref _generation);

    /// <summary>
    /// True when <paramref name="captured"/> still refers to the live browser.
    /// A dispose/recycle that creates a replacement, or a dispose with no replacement, both invalidate.
    /// </summary>
    public bool IsCurrent(int captured) => captured != 0 && captured == _generation;

    /// <summary>
    /// Invalidate every outstanding operation without creating a browser. Used at the start of dispose
    /// so in-flight focus/copy work exits before <c>_chatView</c> is cleared.
    /// </summary>
    public void Invalidate() => System.Threading.Interlocked.Increment(ref _generation);
}

/// <summary>
/// Pure decisions for copy-shortcut / focus lifecycle — unit-tested without WPF or SendInput.
/// </summary>
public static class CopyFocusPolicy {
    /// <summary>Maximum time to wait for Resume Builder to become foreground before giving up on auto-copy.</summary>
    public static readonly TimeSpan ForegroundRetryBudget = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Whether a delayed focus/copy continuation may touch the browser.
    /// Requires a matching generation, an open cancellation token, and a non-null view flag.
    /// </summary>
    public static bool MayTouchBrowser(int capturedGeneration, int liveGeneration, bool cancelled, bool viewExists) =>
        !cancelled && viewExists && capturedGeneration != 0 && capturedGeneration == liveGeneration;

    /// <summary>
    /// After a successful capture the answer is already on the clipboard — a trailing focus call from
    /// ShowAnswerReady must not run (it races recycle). Skip when the copy path already focused, or
    /// when capture is no longer armed / job is no longer waiting.
    /// </summary>
    public static bool ShouldFocusOnAnswerReady(bool copyPathAlreadyFocused, bool captureStillArmed, bool jobStillActive) =>
        !copyPathAlreadyFocused && captureStillArmed && jobStillActive;

    /// <summary>
    /// When the auto shortcut was skipped because another app owned the foreground, retry once the
    /// window is ours again — but only while the same job is still waiting for a capture.
    /// </summary>
    public static bool ShouldRetryCopyOnForeground(
        bool nowOwned, bool sameGeneration, bool jobStillProcessing, bool captureArmed, bool alreadyCopied) =>
        nowOwned && sameGeneration && jobStillProcessing && captureArmed && !alreadyCopied;
}
