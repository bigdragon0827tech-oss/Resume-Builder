namespace ResumeBuilder;

public enum WatchdogResult {
    /// <summary>Nothing cancelled or replaced the watch before the timeout elapsed.</summary>
    TimedOut,
    /// <summary>A capture, Stop, Skip, Pause, failure, close or a new job ended the watch first.</summary>
    Cancelled
}

/// <summary>
/// A6.6.13 — a bounded wait for the user's Copy once ChatGPT has confirmably finished an answer.
///
/// Without it, a Copy that never produced a usable clipboard event left the queue waiting on one job
/// forever. The watchdog only decides "did the time run out for this job"; <see cref="MainWindow"/>
/// performs the effects (disarm, mark Failed, recycle, advance). No I/O, no UI, no WebView2, and the
/// delay is injected, so the timing is tested with a virtual clock.
///
/// At most one watch exists. Starting a watch for another job, or cancelling, retires the previous
/// one: it can then only ever report <see cref="WatchdogResult.Cancelled"/>, so a timer belonging to
/// an earlier job can never fail the job that is active now.
/// </summary>
public sealed class CaptureWatchdog {
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The failure reason recorded on the job.</summary>
    public const string FailureReason = JobTask.CaptureTimeoutReason;

    public const string TimeoutMessage = "No valid result capture received within 10 seconds after ChatGPT completed.";

    readonly Func<TimeSpan, CancellationToken, Task> _delay;
    CancellationTokenSource? _cancellation;
    int _generation;

    public CaptureWatchdog(TimeSpan? timeout = null, Func<TimeSpan, CancellationToken, Task>? delay = null) {
        Timeout = timeout ?? DefaultTimeout;
        _delay = delay ?? ((span, ct) => Task.Delay(span, ct));
    }

    public TimeSpan Timeout { get; }

    /// <summary>The job being watched, or null when no watch is running.</summary>
    public string? JobId { get; private set; }

    public bool IsRunning => JobId is not null;

    /// <summary>
    /// Watches <paramref name="jobId"/> until the timeout elapses or the watch is cancelled.
    /// Replaces any watch already running.
    /// </summary>
    public async Task<WatchdogResult> RunAsync(string jobId) {
        Cancel();
        var cancellation = new CancellationTokenSource();
        var generation = ++_generation;
        _cancellation = cancellation;
        JobId = jobId;

        try {
            await _delay(Timeout, cancellation.Token);
        } catch (OperationCanceledException) {
            return WatchdogResult.Cancelled;
        }

        // The delay can complete in the same instant as a cancel; the generation decides.
        if (generation != _generation || cancellation.IsCancellationRequested) return WatchdogResult.Cancelled;
        _cancellation = null;
        JobId = null;
        return WatchdogResult.TimedOut;
    }

    /// <summary>
    /// Ends the running watch. With <paramref name="onlyForJobId"/>, only a watch on that job is ended.
    /// Returns the job id whose watch was cancelled, or null when nothing was running.
    /// </summary>
    public string? Cancel(string? onlyForJobId = null) {
        if (JobId is null) return null;
        if (onlyForJobId is not null && !JobId.Equals(onlyForJobId, StringComparison.OrdinalIgnoreCase)) return null;
        var cancelled = JobId;
        _generation++;
        try { _cancellation?.Cancel(); } catch (ObjectDisposedException) { }
        _cancellation = null;
        JobId = null;
        return cancelled;
    }
}
