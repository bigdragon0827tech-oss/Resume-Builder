using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Serialization;

namespace ResumeBuilder;

public static class EmailTaskStatus {
    public const string Pending = "Pending";
    public const string Processing = "Processing";
    public const string Ready = "Ready";
    public const string Failed = "Failed";

    public static string Normalize(string? status) => status switch {
        Pending or Processing or Ready or Failed => status,
        _ => Pending
    };
}

/// <summary>One recruiter email or job description saved for later tailoring.</summary>
public sealed class EmailTask : INotifyPropertyChanged {
    public string Id { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string Sender { get; set; } = "";
    public string? Company { get; set; }
    public string JobTitle { get; set; } = "";
    public string JobDescription { get; set; } = "";
    public string? EmailUrl { get; set; }
    public string OriginalText { get; set; } = "";
    string _status = EmailTaskStatus.Pending;
    public string Status {
        get => _status;
        set {
            var next = EmailTaskStatus.Normalize(value);
            if (_status == next) return;
            _status = next;
            Notify(nameof(Status));
            Notify(nameof(CanRetry));
            Notify(nameof(CanTailor));
        }
    }
    public string? DocxPath { get; set; }
    public string? PdfPath { get; set; }
    public string? OutputFolder { get; set; }
    public string? FailureReason { get; set; }

    [JsonIgnore]
    public string DateText => CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    [JsonIgnore]
    public bool CanOpenResume => !string.IsNullOrWhiteSpace(DocxPath);

    [JsonIgnore]
    public bool CanOpenPdf => !string.IsNullOrWhiteSpace(PdfPath);

    [JsonIgnore]
    public bool CanOpenFolder => !string.IsNullOrWhiteSpace(OutputFolder);

    [JsonIgnore]
    public bool CanOpenEmail => JobTracker.IsOpenableUrl(EmailUrl);

    [JsonIgnore]
    public bool CanRetry => Status == EmailTaskStatus.Failed;

    [JsonIgnore]
    public bool CanTailor => Status is EmailTaskStatus.Pending or EmailTaskStatus.Failed;

    public void ApplyOutput(string? folder, string? docx, string? pdf) {
        OutputFolder = folder;
        DocxPath = docx;
        PdfPath = pdf;
        Notify(nameof(CanOpenResume));
        Notify(nameof(CanOpenPdf));
        Notify(nameof(CanOpenFolder));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public static bool OpenPath(string? path) {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        } catch (Exception ex) {
            PerfLog.Line("EMAIL open failed " + ex.GetType().Name);
            return false;
        }
    }

    public static bool OpenEmail(EmailTask? task) {
        if (task is null || !JobTracker.IsOpenableUrl(task.EmailUrl)) return false;
        return JobTracker.OpenJobUrl(task.EmailUrl);
    }
}

/// <summary>
/// Manual resumes live beside the dated job folders: Documents\ResumeAutomation\Manual
/// when the Resume Root is the default Resumes folder.
/// </summary>
public static class EmailOutput {
    public static string Root(AppSettings settings) {
        var resumes = (settings.ResumeRootFolder ?? "").Trim();
        if (resumes.Length == 0)
            return Path.Combine(AppPaths.DefaultAutomationFolder, "Manual");
        var trimmed = resumes.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(Path.GetFileName(trimmed), "Resumes", StringComparison.OrdinalIgnoreCase)) {
            var parent = Path.GetDirectoryName(trimmed);
            if (!string.IsNullOrEmpty(parent))
                return Path.Combine(parent, "Manual");
        }
        return Path.Combine(trimmed, "Manual");
    }

    /// <summary>
    /// Company - Title, or Title alone when company is blank. A folder already used by another
    /// email task gets this task's id appended. An existing folder for this task is reused.
    /// </summary>
    public static string FolderFor(EmailTask task, string root) {
        if (!string.IsNullOrWhiteSpace(task.OutputFolder))
            return task.OutputFolder!;
        var company = task.Company ?? "";
        var title = task.JobTitle ?? "";
        var name = string.IsNullOrWhiteSpace(company) && string.IsNullOrWhiteSpace(title)
            ? ResumeOutputManager.SanitizeFolderName(task.Id)
            : ResumeOutputManager.JobFolderName(company, title);
        if (name.Length == 0) name = ResumeOutputManager.SanitizeFolderName(task.Id);
        var folder = Path.Combine(root, name);
        if (OwnedByOther(folder, task.Id))
            folder = Path.Combine(root, name + " " + ResumeOutputManager.SanitizeFolderName(task.Id));
        return folder;
    }

    public static void Claim(string folder, string id) {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "email-task-id.txt"), id);
    }

    static bool OwnedByOther(string folder, string id) {
        if (!Directory.Exists(folder)) return false;
        var marker = Path.Combine(folder, "email-task-id.txt");
        if (!File.Exists(marker))
            return Directory.EnumerateFileSystemEntries(folder).Any();
        var owner = File.ReadAllText(marker).Trim();
        return owner.Length > 0 && !owner.Equals(id, StringComparison.OrdinalIgnoreCase);
    }

    public const string SourceName = "email";

    public static bool IsEmail(JobTask? job) =>
        job is not null && string.Equals(job.Source, SourceName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One SQLite job for this email task. The internal id is the email task id.
    /// A later tailor updates that same job and does not reset an application status already stored.
    /// </summary>
    public static JobTask Bind(EmailTask task, JobTask? existing = null) {
        var job = existing ?? new JobTask();
        var created = existing is null ? default : existing.CreatedAt;
        var application = existing is null ? "" : existing.ApplicationStatus;
        job.JobId = task.Id;
        job.Source = SourceName;
        job.Company = task.Company ?? "";
        job.Title = task.JobTitle ?? "";
        job.Jd = task.JobDescription ?? "";
        var url = JobTracker.IsOpenableUrl(task.EmailUrl) ? task.EmailUrl!.Trim() : "";
        if (url.Length > 0 || existing is null) job.Link = url;
        job.Status = "Queued";
        job.FailureReason = null;
        if (existing is null) {
            job.ApplicationStatus = ApplicationStatus.Viewed;
            job.CreatedAt = task.CreatedAt == default ? DateTime.Now : task.CreatedAt.LocalDateTime;
        } else if (!string.IsNullOrWhiteSpace(application)) {
            job.ApplicationStatus = application;
            if (created != default) job.CreatedAt = created;
        }
        return job;
    }

    /// <summary>The same payload shape a job uses. Sender is not included. A blank company stays blank.</summary>
    public static JobTask AsJob(EmailTask task) => Bind(task);

    /// <summary>Job Tasks and Email Tasks both tailor through <see cref="HtmlTailor"/>.</summary>
    public static PreparedRequest PrepareRun(JobTask job, AppSettings settings) =>
        HtmlTailor.Prepare(job, settings);

    public static GenerationResult WriteResume(EmailTask task, JobTask job, AppSettings settings, string html) {
        var folder = FolderFor(task, Root(settings));
        Claim(folder, task.Id);
        return HtmlTailor.WriteDocuments(job, settings, html, folder);
    }

    public static bool ApplyStoredResume(EmailTask task) {
        if (!JobStore.TryLatestResume(task.Id, out var docx, out var pdf, out var folder)) return false;
        if (docx.Length == 0 && pdf.Length == 0) return false;
        task.ApplyOutput(folder.Length > 0 ? folder : task.OutputFolder, docx, pdf);
        return true;
    }
}
