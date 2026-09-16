namespace ResumeBuilder;

/// <summary>
/// Owns the lifetime of the ChatGPT WebView2 so it can be destroyed between jobs.
///
/// A6.6.12 measured an empty ChatGPT tab at ~700 MB across six WebView2 processes. Keeping that
/// alive for the whole session is the single largest cost in the application, and nothing needs it
/// between jobs: the answer has already been captured, validated, saved and turned into documents.
/// So the browser is destroyed after every completed job and recreated lazily when the next job
/// starts. The user-data folder is shared and untouched, so the signed-in session survives.
///
/// The create and dispose steps are injected, which keeps the orchestration testable without a
/// browser and keeps this class free of WPF and WebView2 references.
/// </summary>
public sealed class ChatHost {
    readonly Func<Task> _create;
    readonly Func<Task> _dispose;

    public ChatHost(Func<Task> create, Func<Task> dispose) {
        _create = create;
        _dispose = dispose;
    }

    /// <summary>True while a browser exists. Nothing else should hold a reference to it.</summary>
    public bool IsAlive { get; private set; }
    public int Creations { get; private set; }
    public int Disposals { get; private set; }

    /// <summary>Creates the browser if it is not alive. Safe to call before every job.</summary>
    public async Task EnsureAsync() {
        if (IsAlive) return;
        await _create();          // a throw leaves IsAlive false, so the next call retries cleanly
        IsAlive = true;
        Creations++;
    }

    /// <summary>
    /// Destroys the browser. Idempotent, and never throws — a teardown problem must not fail a job
    /// whose result is already saved.
    /// </summary>
    public async Task ReleaseAsync() {
        if (!IsAlive) return;
        IsAlive = false;          // cleared first: a failed teardown must not leave a stale "alive"
        Disposals++;
        try { await _dispose(); }
        catch { /* the reference is already dropped; the next EnsureAsync builds a new one */ }
    }

    /// <summary>
    /// Hard recycle after a completed job: destroy now, rebuild lazily at the next job. Callers must
    /// only invoke this once the response is captured — never while one is pending.
    /// </summary>
    public Task RecycleAsync() => ReleaseAsync();
}
