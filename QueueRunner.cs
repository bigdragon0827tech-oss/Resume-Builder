using System.Security.Cryptography;
using System.Text;

namespace ResumeBuilder;

public enum QueueState { Idle, Running, Paused, Finished }

/// <summary>What to do after a rejected response.</summary>
public enum FailureOutcome {
    /// <summary>First rejection: the job stays Processing and another Copy is requested.</summary>
    RetryCopy,
    /// <summary>Second rejection: mark the job Failed, keep the raw diagnostic, move on.</summary>
    GiveUp
}

/// <summary>
/// Sequencing for A6.6.10, deliberately free of I/O: no files, no clipboard, no WebView2, no UI.
/// Everything that decides "which job is active and what happens next" lives here so it can be
/// tested directly, which is where the real risk in a queue sits.
///
/// Invariants:
///   - At most one job is active at any moment (<see cref="ActiveJobId"/>).
///   - A response is always attributed to ActiveJobId, never to "whatever was prepared last".
///   - An identical response captured twice is refused rather than written against a second job.
/// </summary>
public sealed class QueueRunner {
    readonly List<JobTask> _snapshot = new();
    readonly HashSet<string> _acceptedHashes = new(StringComparer.Ordinal);
    int _index = -1;
    int _strikes;

    public QueueState State { get; private set; } = QueueState.Idle;
    public string? ActiveJobId { get; private set; }
    public bool IsRunning => State is QueueState.Running or QueueState.Paused;

    /// <summary>1-based position of the active job within the snapshot, for display.</summary>
    public int Position { get; private set; }
    public int Total => _snapshot.Count;

    /// <summary>Takes a snapshot of the currently queued jobs, in list order.</summary>
    public int Start(IEnumerable<JobTask> jobs) {
        _snapshot.Clear();
        _snapshot.AddRange(jobs.Where(j => j.Status == "Queued"));
        _index = -1;
        Position = 0;
        ActiveJobId = null;
        _strikes = 0;
        PausedForManualAction = false;
        State = _snapshot.Count == 0 ? QueueState.Finished : QueueState.Running;
        return _snapshot.Count;
    }

    /// <summary>
    /// Moves to the next job that is still Queued. Status is re-read from the live task, so a job
    /// completed or failed in the meantime is skipped instead of being run twice.
    /// </summary>
    public JobTask? Next() {
        ActiveJobId = null;
        if (State != QueueState.Running) return null;

        while (++_index < _snapshot.Count) {
            var candidate = _snapshot[_index];
            if (candidate.Status != "Queued") continue;   // Completed / Failed / Ignored are skipped
            BeginJob(candidate.JobId);
            Position = _index + 1;
            return candidate;
        }

        State = QueueState.Finished;
        return null;
    }

    /// <summary>Marks a job active and resets its strike count. Used by the queue and by manual runs.</summary>
    public void BeginJob(string jobId) {
        ActiveJobId = jobId;
        _strikes = 0;
    }

    /// <summary>True when the queue paused itself because a step needs the user, not because they asked.</summary>
    public bool PausedForManualAction { get; private set; }

    public void Pause() {
        if (State != QueueState.Running) return;
        State = QueueState.Paused;
        PausedForManualAction = false;      // an explicit pause must never auto-resume
    }

    public void Resume() {
        if (State != QueueState.Paused) return;
        State = QueueState.Running;
        PausedForManualAction = false;
    }

    /// <summary>
    /// A6.6.11: Auto-Send could not complete, so the run waits for one manual keystroke instead of
    /// advancing. The job stays active — nothing was rejected.
    /// </summary>
    public void PauseForManualAction() {
        if (State != QueueState.Running) return;
        State = QueueState.Paused;
        PausedForManualAction = true;
    }

    /// <summary>Resumes only a pause the queue caused itself. A user pause stays paused.</summary>
    public bool TryAutoResume() {
        if (State != QueueState.Paused || !PausedForManualAction) return false;
        State = QueueState.Running;
        PausedForManualAction = false;
        return true;
    }

    /// <summary>Stops the run and returns the job that was in flight so the caller can re-queue it.</summary>
    public string? Stop() {
        var inFlight = ActiveJobId;
        State = QueueState.Idle;
        ActiveJobId = null;
        _snapshot.Clear();
        _index = -1;
        Position = 0;
        _strikes = 0;
        PausedForManualAction = false;
        return inFlight;
    }

    /// <summary>True when this exact response was already accepted — a stale Copy of an earlier answer.</summary>
    public bool IsDuplicate(string text) => _acceptedHashes.Contains(Hash(text));

    public void OnCaptureSucceeded(string text) {
        _acceptedHashes.Add(Hash(text));
        _strikes = 0;
        ActiveJobId = null;
    }

    /// <summary>First rejection asks for another Copy; the second gives up so the queue cannot block.</summary>
    public FailureOutcome OnCaptureFailed() {
        _strikes++;
        if (_strikes >= 2) { ActiveJobId = null; return FailureOutcome.GiveUp; }
        return FailureOutcome.RetryCopy;
    }

    /// <summary>Abandons the active job (the Skip button). The caller marks it Failed.</summary>
    public string? SkipActive() {
        var skipped = ActiveJobId;
        ActiveJobId = null;
        _strikes = 0;
        return skipped;
    }

    /// <summary>Called when a job could not even be prepared; nothing is active afterwards.</summary>
    public void AbandonActive() {
        ActiveJobId = null;
        _strikes = 0;
    }

    static string Hash(string text) {
        var normalized = text.Replace("\r\n", "\n").Trim();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    /// <summary>
    /// A job left Processing by a crash or a close is stranded: nothing would ever re-run it.
    /// Called once at startup to put it back in the queue. Returns how many were recovered.
    /// </summary>
    public static int RecoverStaleProcessing(IEnumerable<JobTask> tasks) {
        var recovered = 0;
        foreach (var t in tasks) {
            if (t.Status != "Processing") continue;
            t.Status = "Queued";
            recovered++;
        }
        return recovered;
    }
}
