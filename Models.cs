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
public sealed class JobTask {
    public string JobId { get; set; } = "";
    public string Source { get; set; } = "";
    public string Company { get; set; } = "";
    public string Title { get; set; } = "";
    public string Location { get; set; } = "";
    public string Jd { get; set; } = "";
    public string Link { get; set; } = "";
    public string About { get; set; } = "";
    public string Status { get; set; } = "Queued";
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
}
public sealed class PreparedRequest {
    public string JobId { get; set; } = "";
    public string Company { get; set; } = "";
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
