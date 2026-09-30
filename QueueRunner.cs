using System.Security.Cryptography;
using System.Text;

namespace ResumeBuilder;

public enum QueueState { Idle, Running, Paused, Finished }

/// <summary>Whether a captured answer may be attributed to the active job.</summary>
public enum CaptureDecision {
    Accept,
    /// <summary>No job is waiting: the capture has nothing to belong to.</summary>
    NoActiveJob,
    /// <summary>Already accepted for an earlier job.</summary>
    Duplicate,
    /// <summary>Belongs to a job whose capture timed out; never given to the next job.</summary>
    LateResponse,
    /// <summary>A slice of our own prepared request, not ChatGPT's answer.</summary>
    PromptEcho
}

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
    readonly HashSet<string> _timedOutHashes = new(StringComparer.Ordinal);
    int _index = -1;
    int _strikes;

    public QueueState State { get; private set; } = QueueState.Idle;
    public string? ActiveJobId { get; private set; }
    public bool IsRunning => State is QueueState.Running or QueueState.Paused;

    /// <summary>1-based position of the active job within the snapshot, for display.</summary>
    public int Position { get; private set; }
    public int Total => _snapshot.Count;

    /// <summary>Takes a snapshot of the currently queued jobs, in list order.</summary>
    /// <returns>How many queued jobs were captured. Returns 0 without changing state when a run is already active.</returns>
    public int Start(IEnumerable<JobTask> jobs) {
        // A second Start while Running/Paused would reset the snapshot mid-job and can re-select a
        // job that just failed. Refuse — the UI already disables the button; this is the hard guard.
        if (State is QueueState.Running or QueueState.Paused) return 0;

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
            // Re-read live status: Completed / Failed / Ignored / Processing must never be selected.
            if (candidate.Status != "Queued") {
                // Diagnostic hook for tests / callers that log skips (MainWindow logs the id).
                continue;
            }
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

    /// <summary>
    /// A6.6.13: no valid capture arrived within the watchdog timeout after ChatGPT finished. Nothing is
    /// active afterwards (the caller marks the job Failed / CaptureTimeout and advances).
    /// <paramref name="clipboardText"/> is the profile-like text on the clipboard at that moment, if any
    /// (most likely the timed-out job's own answer). It is remembered so that a late event carrying it
    /// can never be attributed to the next job. Returns the job that timed out.
    /// </summary>
    public string? OnCaptureTimedOut(string? clipboardText) {
        var timedOut = ActiveJobId;
        ActiveJobId = null;
        _strikes = 0;
        if (!string.IsNullOrWhiteSpace(clipboardText) && !IsDuplicate(clipboardText))
            _timedOutHashes.Add(Hash(clipboardText));
        return timedOut;
    }

    /// <summary>True when this response was on the clipboard when an earlier job's capture timed out.</summary>
    public bool IsLateResponse(string text) => _timedOutHashes.Contains(Hash(text));

    /// <summary>
    /// Decides whether a profile-like capture may be attributed to the active job. Pure: the caller has
    /// already checked <see cref="ResultCapture.ShouldCapture"/> and performs the effects.
    /// </summary>
    public CaptureDecision Classify(string text, string? preparedText) {
        if (ActiveJobId is null) return CaptureDecision.NoActiveJob;
        if (IsDuplicate(text)) return CaptureDecision.Duplicate;
        if (IsLateResponse(text)) return CaptureDecision.LateResponse;
        if (PromptEchoGuard.IsEchoOfPrompt(text, preparedText)) return CaptureDecision.PromptEcho;
        return CaptureDecision.Accept;
    }

    /// <summary>Abandons the active job (the Skip button). The caller marks it Failed.</summary>
    public string? SkipActive() {
        var skipped = ActiveJobId;
        ActiveJobId = null;
        _strikes = 0;
        return skipped;
    }

    /// <summary>
    /// Replaces the jobs not yet started with <paramref name="queuedInOrder"/>.
    /// The job already running stays the active job.
    /// </summary>
    public void ReorderRemaining(IEnumerable<JobTask> queuedInOrder) {
        if (State is not (QueueState.Running or QueueState.Paused)) return;
        var passed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i <= _index && i < _snapshot.Count; i++)
            passed.Add(_snapshot[i].JobId);
        if (_index + 1 < _snapshot.Count)
            _snapshot.RemoveRange(_index + 1, _snapshot.Count - _index - 1);
        foreach (var job in queuedInOrder) {
            if (job is null || job.Status != "Queued" || string.IsNullOrWhiteSpace(job.JobId)) continue;
            if (!passed.Add(job.JobId)) continue;
            _snapshot.Add(job);
        }
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

/// <summary>Job Tasks waiting order. Unknown values stay Queue so a bad setting cannot fail the load.</summary>
public static class QueueModes {
    public const string Queue = "Queue";
    public const string Stack = "Stack";

    public static string Normalize(string? value) =>
        string.Equals((value ?? "").Trim(), Stack, StringComparison.OrdinalIgnoreCase) ? Stack : Queue;

    public static bool IsStack(string? value) => Normalize(value) == Stack;
}

/// <summary>
/// Orders waiting jobs without changing their status, id or timestamps.
/// Queue is oldest first. Stack is newest first. Equal timestamps use the internal job id.
/// </summary>
public static class JobQueueOrder {
    public static List<JobTask> Arrange(IReadOnlyList<JobTask> tasks, string? mode) {
        var processing = new List<JobTask>();
        var queued = new List<JobTask>();
        var rest = new List<JobTask>();
        foreach (var job in tasks) {
            if (job.Status == "Processing") processing.Add(job);
            else if (job.Status == "Queued") queued.Add(job);
            else rest.Add(job);
        }
        queued.Sort((a, b) => Compare(a, b, mode));
        var arranged = new List<JobTask>(processing.Count + queued.Count + rest.Count);
        arranged.AddRange(processing);
        arranged.AddRange(queued);
        arranged.AddRange(rest);
        return arranged;
    }

    public static int Compare(JobTask a, JobTask b, string? mode) {
        var byTime = a.CreatedAt.CompareTo(b.CreatedAt);
        if (QueueModes.IsStack(mode)) byTime = -byTime;
        if (byTime != 0) return byTime;
        return string.Compare(a.JobId, b.JobId, StringComparison.Ordinal);
    }
}
