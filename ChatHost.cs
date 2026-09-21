namespace ResumeBuilder;

/// <summary>
/// The single owner of the ChatGPT WebView2 lifetime.
///
/// A6.6.12 measured an empty ChatGPT tab at ~700 MB across six WebView2 processes, so the browser is
/// destroyed after every completed job and rebuilt lazily for the next one. The subtlety that makes
/// this safe for long runs: <c>Dispose()</c> returns before the browser process has actually exited,
/// so creating the next browser immediately would overlap two full browser trees. This class treats
/// a release as finished only when the dispose delegate reports that the browser process is gone,
/// and <see cref="EnsureAsync"/> waits for any release still in flight before creating anything.
///
/// Create and dispose are injected, so the orchestration is unit-tested with no browser, no WPF and
/// no WebView2 reference in this file.
/// </summary>
public sealed class ChatHost {
    /// <summary>Creates the browser and returns its process id.</summary>
    readonly Func<Task<int>> _create;

    /// <summary>Destroys the browser and returns true only once its process has exited.</summary>
    readonly Func<Task<bool>> _dispose;

    Task? _pendingRelease;

    public ChatHost(Func<Task<int>> create, Func<Task<bool>> dispose) {
        _create = create;
        _dispose = dispose;
    }

    /// <summary>True while a browser exists. Nothing else should hold a reference to it.</summary>
    public bool IsAlive { get; private set; }
    public int Creations { get; private set; }
    public int Disposals { get; private set; }

    /// <summary>Process id of the live browser, or of the last one created.</summary>
    public int BrowserProcessId { get; private set; }

    /// <summary>True when the last shutdown could not be confirmed within the timeout.</summary>
    public bool LastShutdownTimedOut { get; private set; }
    public int TimeoutCount { get; private set; }

    /// <summary>True between a release starting and the browser process actually exiting.</summary>
    public bool IsReleasing => _pendingRelease is not null;

    /// <summary>
    /// Creates the browser if none is alive. If a previous release is still in flight, this waits for
    /// that browser process to exit first — two browser trees must never overlap.
    /// </summary>
    public async Task EnsureAsync() {
        var pending = _pendingRelease;
        if (pending is not null) await pending;

        if (IsAlive) return;
        BrowserProcessId = await _create();   // a throw leaves IsAlive false, so the next call retries
        IsAlive = true;
        Creations++;
    }

    /// <summary>
    /// Destroys the browser and waits for its process to exit. Idempotent, and never throws — a
    /// teardown problem must not fail a job whose result is already saved. A caller that arrives
    /// while a release is in flight waits for that release instead of starting another.
    /// </summary>
    public async Task ReleaseAsync() {
        if (!IsAlive) {
            var inFlight = _pendingRelease;
            if (inFlight is not null) await inFlight;
            return;
        }

        IsAlive = false;      // cleared first: a failed teardown must not leave a stale "alive"
        Disposals++;

        var release = RunReleaseAsync();
        _pendingRelease = release;
        try { await release; }
        finally { _pendingRelease = null; }
    }

    async Task RunReleaseAsync() {
        try {
            var exited = await _dispose();
            LastShutdownTimedOut = !exited;
            if (!exited) TimeoutCount++;
        } catch {
            // The reference is already dropped; report it as unconfirmed rather than as success.
            LastShutdownTimedOut = true;
            TimeoutCount++;
        }
    }

    /// <summary>True when the host still thinks a browser is alive but the view/core is gone.</summary>
    public static bool IsDesynced(bool hostReportsAlive, bool coreWebViewAvailable) =>
        hostReportsAlive && !coreWebViewAvailable;

    /// <summary>
    /// Hard recycle after a completed job: destroy now, wait for the process to go, rebuild lazily at
    /// the next job. Callers must only invoke this once the response is captured — never while one is
    /// pending.
    /// </summary>
    public Task RecycleAsync() => ReleaseAsync();
}
