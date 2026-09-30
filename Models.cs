using System.Text.Json.Serialization;
using System.Windows.Media;
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

    /// <summary>
    /// The platform <see cref="ApplyUrl"/> points at, derived by <see cref="ApplicationPlatformDetector"/>.
    /// Unknown when there is no ApplyUrl (and for every task saved before it existed). Read tolerantly:
    /// an unreadable stored value becomes Unknown rather than failing the whole tasks.json.
    /// </summary>
    [JsonConverter(typeof(TolerantPlatformConverter))]
    public ApplicationPlatform ApplicationPlatform { get; set; } = ApplicationPlatform.Unknown;

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
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(CanApply)));
        }
    }

    /// <summary>When the task first reached Resume Builder. Stamped on load for pre-tracking tasks.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>The generated document this application will be submitted with, once one exists.</summary>
    public string ResumePath { get; set; } = "";

    /// <summary>Unused by HTML tailoring. Kept so an older job record still loads.</summary>
    public string StyleReferenceResume { get; set; } = "";

    // When each stage was first reached. Historical facts: once set they are never rewritten or
    // cleared, so moving a task back and forward again cannot invent a new history.
    public DateTime? ViewedAt { get; set; } = DateTime.Now;
    public DateTime? ReadyAt { get; set; }
    public DateTime? AppliedAt { get; set; }
    public DateTime? InterviewAt { get; set; }
    public DateTime? DoneAt { get; set; }

    /// <summary>When the application was marked Failed. Empty until that happens.</summary>
    public DateTime? FailedAt { get; set; }

    /// <summary>
    /// The status this job left when it was marked Failed: Applied or Interview. Empty when it was
    /// never failed from one of those two. Set once and never rewritten.
    /// </summary>
    public string FailedFrom { get; set; } = "";

    [JsonIgnore] public bool ResumeGenerated => !string.IsNullOrWhiteSpace(ResumePath);

    /// <summary>Apply is enabled from a usable application address or job posting. Status is not consulted.</summary>
    [JsonIgnore] public bool CanApply => JobTracker.CanApply(this);

    /// <summary>
    /// The date this job is filed under in the tracker: when it was applied for, else when its resume
    /// became ready, else when it arrived. Never null, so sorting and date filters cannot crash on a
    /// task saved before tracking existed.
    /// </summary>
    [JsonIgnore] public DateTime TrackingDate => AppliedAt ?? ReadyAt ?? CreatedAt;

    [JsonIgnore] public string TrackingDateDisplay => TrackingDate == default ? "" : TrackingDate.ToString("MMM d");

    /// <summary>Resume column and board card text, so neither needs a value converter.</summary>
    [JsonIgnore] public string ResumeStateDisplay => ResumeGenerated ? "Ready" : "No resume";

    /// <summary>
    /// Whether this job can be applied for now (resume + usable application link). Derived by
    /// <see cref="JobTracker.GetReadiness"/>; display only, never saved, never a status.
    /// </summary>
    [JsonIgnore] public ApplicationReadiness Readiness => JobTracker.GetReadiness(this);

    /// <summary>Readiness column and board card text.</summary>
    [JsonIgnore] public string ReadinessDisplay => JobTracker.ReadinessText(Readiness);

    /// <summary>Readiness tooltip: what is missing, or that nothing is.</summary>
    [JsonIgnore] public string ReadinessHint => JobTracker.ReadinessHint(Readiness);

    string? _logoUrl;
    bool _logoUrlReady;

    /// <summary>
    /// The company site the icon cache is keyed by. <see cref="CompanyUrl"/> when that is a web
    /// address; otherwise the company website already written in the job text. Resolved once.
    /// </summary>
    [JsonIgnore]
    public string? LogoUrl {
        get {
            if (_logoUrlReady) return _logoUrl;
            _logoUrl = IconCache.CompanyPageUrl(this);
            _logoUrlReady = true;
            return _logoUrl;
        }
    }

    /// <summary>A real cached company icon, or null when the generic fallback should show.</summary>
    [JsonIgnore] public ImageSource? CompanyIcon => IconCache.CompanyImage(LogoUrl);

    /// <summary>The job board the posting came from. Not the ATS, and not saved on its own.</summary>
    [JsonIgnore]
    public string JobSite => ApplicationPlatformDetector.JobSiteName(Link, Source);

    /// <summary>
    /// The application-platform icon when one is cached, otherwise the job-site icon captured at import.
    /// Null when neither file exists, so the generic fallback shows.
    /// </summary>
    [JsonIgnore]
    public ImageSource? PlatformIcon {
        get {
            foreach (var platform in IconCache.PlatformsFor(this)) {
                var image = IconCache.PlatformImage(platform);
                if (image is not null) return image;
            }
            return null;
        }
    }

    /// <summary>Hover name: the ATS when one is stored, otherwise the job site.</summary>
    [JsonIgnore] public string PlatformToolTip =>
        ApplicationPlatform is ApplicationPlatform.Other or ApplicationPlatform.Unknown
            ? JobSite
            : JobTracker.PlatformDisplayName(ApplicationPlatform);

    public void NotifyIconsChanged() {
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(CompanyIcon)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(PlatformIcon)));
    }

    [JsonIgnore] public string CreatedDisplay => CreatedAt == default ? "" : CreatedAt.ToString("yyyy-MM-dd");

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raises change notification for the tracking columns after JobTracker edits a task.</summary>
    public void NotifyTrackingChanged() {
        foreach (var name in new[] { nameof(ApplicationStatus), nameof(CanApply), nameof(UpdatedAt), nameof(ResumePath),
                                     nameof(ResumeGenerated), nameof(ResumeStateDisplay),
                                     nameof(Readiness), nameof(ReadinessDisplay), nameof(ReadinessHint),
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

    /// <summary>
    /// Kept so an older settings file still loads. HTML tailoring does not read it.
    /// The Original Resume supplies the facts, the HTML, and the layout.
    /// </summary>
    public string StyleReferenceResume { get; set; } = "";
    public string CandidateProfile { get; set; } = "";
    public string MasterPrompt { get; set; } = "";

    /// <summary>
    /// Which prompt file a request is built from: <see cref="PromptModes"/> Resume (default) or Normal.
    /// A string, and read through PromptModes.Normalize, so an unknown or missing value falls back to
    /// Resume instead of throwing — a throw here would load DEFAULT settings and lose the user's paths.
    /// </summary>
    public string PromptMode { get; set; } = PromptModes.Resume;

    /// <summary>The user's own prompt file, used in Normal mode. Empty until they choose one.</summary>
    public string NormalPrompt { get; set; } = "";
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

    /// <summary>
    /// Job Tasks waiting order: Queue (oldest first) or Stack (newest first).
    /// A string, read through <see cref="QueueModes.Normalize"/>, so an unknown value stays Queue
    /// instead of throwing and replacing the user's settings.
    /// </summary>
    public string QueueOrder { get; set; } = QueueModes.Queue;

    // ---------- job import filters ----------
    //
    // Five independent switches, read by JobImportFilter and by nothing else. They are ordinary
    // AppSettings properties, so they live in the one settings.json, a file written before they
    // existed loads with exactly these defaults, and the Job Browser toolbar and the Settings
    // "Job Import Filters" section share one value each. They gate FUTURE imports only.

    /// <summary>Refuse a job whose application destination is a LinkedIn job posting. Default on.</summary>
    public bool SkipLinkedInApply { get; set; } = true;

    /// <summary>Refuse a job whose description demands a security clearance of the applicant. Default on.</summary>
    public bool SkipSecurityClearance { get; set; } = true;

    /// <summary>Refuse a job that requires U.S. citizenship or Public Trust eligibility. Default on.</summary>
    public bool SkipCitizenshipRequirement { get; set; } = true;

    /// <summary>Refuse a job restricted to U.S. persons by export control / ITAR. Default off.</summary>
    public bool SkipExportControl { get; set; } = false;

    /// <summary>Refuse a job that states visa sponsorship is not available. Default off.</summary>
    public bool SkipNoVisaSponsorship { get; set; } = false;

    // ---------- ChatGPT pacing (RateLimit reads these; ranges in RateLimit) ----------

    /// <summary>Seconds to wait after a successful job before the queue sends the next one. 0–600; 0 = immediately.</summary>
    public int GptJobDelaySeconds { get; set; } = 30;   // = RateLimit.DefaultJobDelaySeconds

    /// <summary>Minutes to pause after ChatGPT reports "Too many requests". 1–120.</summary>
    public int RateLimitCooldownMinutes { get; set; } = 10;   // = RateLimit.DefaultCooldownMinutes
}

/// <summary>
/// Signed ResumeBuilder license payload.
/// The signature itself is stored separately from this payload.
/// </summary>


public sealed class PreparedRequest {
    public string JobId { get; set; } = "";

    /// <summary>
    /// The prompt mode this request was prepared in (<see cref="PromptModes"/>). The capture checks
    /// the answer against the mode the request was SENT in, not whatever Settings says by the time the
    /// answer arrives. Additive: an older prepared-request.json loads with "" and reads as Resume.
    /// </summary>
    public string PromptMode { get; set; } = "";

    /// <summary>
    /// True when this request was sent as HTML. Capture follows the mode that was sent, even if
    /// Settings is changed before the answer arrives. Older prepared-request.json loads as false.
    /// </summary>
    public bool HtmlTailoring { get; set; }
    public string Company { get; set; } = "";
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
