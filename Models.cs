using System.Text.Json.Serialization;
namespace ResumeBuilder;

public sealed class JobBatch {
    [JsonPropertyName("schemaVersion")] public string SchemaVersion { get; set; } = "";
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("jobs")] public List<JobInput> Jobs { get; set; } = new();
}
public sealed class JobInput {
    [JsonPropertyName("jobId")] public string JobId { get; set; } = "";
    [JsonPropertyName("company")] public string Company { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("location")] public string Location { get; set; } = "";
    [JsonPropertyName("jd")] public string Jd { get; set; } = "";
    [JsonPropertyName("link")] public string Link { get; set; } = "";
    [JsonPropertyName("about")] public string About { get; set; } = "";
}
public sealed class JobTask : System.ComponentModel.INotifyPropertyChanged {
    public string JobId { get; set; } = "";
    public string Source { get; set; } = "";
    public string Company { get; set; } = "";
    public string Title { get; set; } = "";
    public string Location { get; set; } = "";
    public string Jd { get; set; } = "";
    public string Link { get; set; } = "";
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
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public string StatusDisplay => Status switch {
        "Completed" => "✓ Completed", "Processing" => "● Processing",
        "Failed" => "✕ Failed", "Ignored" => "↷ Ignored — Existing Job",
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
