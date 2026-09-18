using System.Text.Json.Serialization;
namespace ResumeBuilder;

// The input contract is JobImportData (JobBrowser.cs): one job per file, no batches.

public sealed class JobTask : System.ComponentModel.INotifyPropertyChanged {
    /// <summary>
    /// Resume Builder's OWN task id ("RB-…" for anything imported now; older tasks keep the id they
    /// were saved with). The queue, the capture, results\&lt;id&gt;.json and every diagnostic hang off
    /// it, so it is generated once and never changes. It is not how duplicates are found — that is
    /// the normalized job URL in <see cref="Link"/>.
    /// </summary>
    public string JobId { get; set; } = "";
    public string Source { get; set; } = "";
    public string Company { get; set; } = "";
    public string Title { get; set; } = "";

    /// <summary>Kept for tasks saved before the one-job contract. New imports leave it empty.</summary>
    public string Location { get; set; } = "";
    public string Jd { get; set; } = "";

    /// <summary>The job posting URL — the input's <c>jobUrl</c>, stored normalized. Opens the job.</summary>
    public string Link { get; set; } = "";

    /// <summary>The company's own website, when the input had one. Optional; never a duplicate key.</summary>
    public string CompanyUrl { get; set; } = "";

    /// <summary>
    /// The real application address (the ATS page), recorded when the user clicked Apply in the job
    /// browser (<see cref="ApplyCapture"/>). Empty until then, and for every task saved before it
    /// existed. Informational only — never a duplicate key, and <see cref="Link"/> keeps its meaning.
    /// </summary>
    public string ApplyUrl { get; set; } = "";

    /// <summary>When <see cref="ApplyUrl"/> was recorded; null when it never was.</summary>
    public DateTime? ApplyUrlCapturedAt { get; set; }

    public string About { get; set; } = "";

    string _status = "Queued";
    /// <summary>Queued -> Processing -> Completed/Failed. A6.6.8 drives these; the queue shows them live.</summary>
    public string Status {
        get => _status;
        set {
            if (_status == value) return;
            _status = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Status)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(StatusDisplay)));
            if (value != "Failed") FailureReason = null;       // a re-queued or completed job has no failure
        }
    }

    public const string CaptureTimeoutReason = "CaptureTimeout";

    string? _failureReason;
    /// <summary>A6.6.13: why a Failed job failed, when the app knows (for example "CaptureTimeout").</summary>
    public string? FailureReason {
        get => _failureReason;
        set {
            if (_failureReason == value) return;
            _failureReason = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(FailureReason)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(StatusDisplay)));
        }
    }

    // ---------- application tracking ----------
    //
    // Deliberately separate from Status above. Status is where the job is in the QUEUE
    // (Queued/Processing/Completed/Failed); ApplicationStatus is where the APPLICATION has got to
    // (Viewed/Ready/Applied/Interview/Done). A job can be queue-Completed and application-Ready.

    // Fully qualified: inside this class "ApplicationStatus" is the property below, not the class.
    string _applicationStatus = ResumeBuilder.ApplicationStatus.Viewed;

    /// <summary>Viewed -> Ready -> Applied -> Interview -> Done. A task without one is Viewed.</summary>
    public string ApplicationStatus {
        get => _applicationStatus;
        set {
            var normalized = ResumeBuilder.ApplicationStatus.Normalize(value);
            if (_applicationStatus == normalized) return;
            _applicationStatus = normalized;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ApplicationStatus)));
        }
    }

    /// <summary>When the task first reached Resume Builder. Stamped on load for pre-tracking tasks.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>The generated document this application will be submitted with, once one exists.</summary>
    public string ResumePath { get; set; } = "";

    // When each stage was first reached. Historical facts: once set they are never rewritten or
    // cleared, so moving a task back and forward again cannot invent a new history.
    public DateTime? ViewedAt { get; set; } = DateTime.Now;
    public DateTime? ReadyAt { get; set; }
    public DateTime? AppliedAt { get; set; }
    public DateTime? InterviewAt { get; set; }
    public DateTime? DoneAt { get; set; }

    [JsonIgnore] public bool ResumeGenerated => !string.IsNullOrWhiteSpace(ResumePath);

    /// <summary>
    /// The date this job is filed under in the tracker: when it was applied for, else when its resume
    /// became ready, else when it arrived. Never null, so sorting and date filters cannot crash on a
    /// task saved before tracking existed.
    /// </summary>
    [JsonIgnore] public DateTime TrackingDate => AppliedAt ?? ReadyAt ?? CreatedAt;

    [JsonIgnore] public string TrackingDateDisplay => TrackingDate == default ? "" : TrackingDate.ToString("MMM d");

    /// <summary>Resume column and board card text, so neither needs a value converter.</summary>
    [JsonIgnore] public string ResumeStateDisplay => ResumeGenerated ? "Ready" : "No resume";

    [JsonIgnore] public string CreatedDisplay => CreatedAt == default ? "" : CreatedAt.ToString("yyyy-MM-dd");

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raises change notification for the tracking columns after JobTracker edits a task.</summary>
    public void NotifyTrackingChanged() {
        foreach (var name in new[] { nameof(ApplicationStatus), nameof(UpdatedAt), nameof(ResumePath),
                                     nameof(ResumeGenerated), nameof(ResumeStateDisplay),
                                     nameof(TrackingDate), nameof(TrackingDateDisplay) })
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }

    public string StatusDisplay => Status switch {
        "Completed" => "✓ Completed", "Processing" => "● Processing",
        "Failed" => FailureReason == CaptureTimeoutReason ? "✕ Failed — no answer captured in time" : "✕ Failed", "Ignored" => "↷ Ignored — Existing Job",
        _ => "○ Queued"
    };
}
public sealed class AppSettings {
    public string OriginalResume { get; set; } = "";
    public string CandidateProfile { get; set; } = "";
    public string MasterPrompt { get; set; } = "";
    public string IncomingFolder { get; set; } = "";
    public string ImportedFolder { get; set; } = "";
    public string ResumeRootFolder { get; set; } = "";
    public bool Docx { get; set; } = true;
    public bool Pdf { get; set; } = true;
    /// <summary>A6.6.8: type the prepared request into the ChatGPT box automatically.</summary>
    public bool AutoFillComposer { get; set; } = true;
    /// <summary>A6.6.8: capture the AI answer from the clipboard while a request is pending.</summary>
    public bool AutoCaptureResult { get; set; } = true;
    /// <summary>A6.6.11: click ChatGPT Send automatically. Copying the answer stays manual.</summary>
    public bool AutoSend { get; set; } = true;
    /// <summary>A6.6.13: show the right-side "answer ready" notification.</summary>
    public bool ReadyToast { get; set; } = true;
    /// <summary>A6.6.13: play a short sound when an answer is ready.</summary>
    public bool ReadySound { get; set; } = true;
    /// <summary>A6.6.13: flash the taskbar button when an answer is ready and the app is in the background.</summary>
    public bool ReadyFlash { get; set; } = true;
    /// <summary>A6.6.13: Ctrl+Shift+' brings Resume Builder to the front from any app (takes effect at startup).</summary>
    public bool FocusHotkey { get; set; } = true;
}
public sealed class PreparedRequest {
    public string JobId { get; set; } = "";
    public string Company { get; set; } = "";
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
