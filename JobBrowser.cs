using System.IO;
using System.Text.Json.Serialization;

namespace ResumeBuilder;

// ---------------------------------------------------------------------------
// Built-in job browser.
//
// A SECOND WebView2, completely separate from the ChatGPT one: its own user-data folder, so its own
// environment, its own browser process tree and its own cookie jar. Recycling the ChatGPT browser
// between jobs cannot touch it, and browsing here cannot disturb the queue.
//
// Nothing in this file reads a page. Phase 2 reads one job, only when the user clicks Import
// Current Job, and only inside JobrightPageExtractor.
// ---------------------------------------------------------------------------

public static class JobBrowser {
    /// <summary>The site the Home button returns to. The user signs in there themselves.</summary>
    public const string HomeUrl = "https://jobright.ai/";

    /// <summary>
    /// %LOCALAPPDATA%\ResumeBuilder\JobBrowserWebView2 — deliberately a sibling of the ChatGPT
    /// profile, never inside it. Two different folders mean two isolated browser profiles.
    /// </summary>
    public static string UserDataFolder => Path.Combine(Storage.DataDir, "JobBrowserWebView2");

    /// <summary>
    /// True when the two browsers really are separate profiles: different folders, and neither one
    /// nested inside the other. A test asserts this so the profiles can never be quietly merged.
    /// </summary>
    public static bool IsSeparateProfileFrom(string chatUserDataFolder) {
        var mine = Normalize(UserDataFolder);
        var theirs = Normalize(chatUserDataFolder);
        if (mine.Equals(theirs, StringComparison.OrdinalIgnoreCase)) return false;

        return !mine.StartsWith(theirs + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !theirs.StartsWith(mine + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>
    /// What a diagnostics line may contain: scheme, host and path only. A query string can carry a
    /// session token or a search term, and neither belongs in a log file.
    /// </summary>
    public static string SafeForLog(string? url) {
        if (string.IsNullOrWhiteSpace(url)) return "(none)";
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return "(not a url)";
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return uri.Scheme + ":";
        return uri.GetLeftPart(UriPartial.Path);
    }
}

/// <summary>
/// THE canonical input: one job, five fields. An Incoming JSON file deserializes straight into this,
/// and the job browser's extractor produces it, so both sources meet the importer in one shape.
///
/// There is deliberately no external id and no location. The importer gives a new task its own
/// internal JobId, and duplicates are recognised by the normalized <see cref="JobUrl"/>.
/// It is NOT a JobTask: a page reader or a file can never write to storage or the queue itself.
/// </summary>
public sealed class JobImportData {
    [JsonPropertyName("company")] public string Company { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";

    /// <summary>The posting itself. Required; opens the job and identifies duplicates.</summary>
    [JsonPropertyName("jobUrl")] public string JobUrl { get; set; } = "";

    /// <summary>The company's own website. Optional and informational — never a duplicate key.</summary>
    [JsonPropertyName("companyUrl")] public string? CompanyUrl { get; set; }

    [JsonPropertyName("description")] public string Description { get; set; } = "";
}

public enum JobImportKind { Imported, Duplicate, Invalid }

/// <summary>What happened to one imported job, ready to show to the user.</summary>
public sealed class JobImportOutcome {
    public JobImportKind Kind { get; init; }

    /// <summary>The internal task id: the new task's, or the existing task's for a duplicate.</summary>
    public string JobId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Company { get; init; } = "";
    public string ApplicationStatus { get; init; } = "";

    /// <summary>Why an Invalid import was refused, in plain words.</summary>
    public string Reason { get; init; } = "";
}

/// <summary>
/// Implemented by <see cref="JobrightPageExtractor"/>. The boundary: an extractor returns data; it
/// never touches storage, the queue or a JobTask.
/// </summary>
public interface IJobPageExtractor {
    /// <summary>Null when the current page is not a job posting this extractor understands.</summary>
    Task<JobImportData?> ExtractCurrentJobAsync();
}
