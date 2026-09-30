using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResumeBuilder;

// ---------------------------------------------------------------------------
// Where a generated resume is stored.
//
//   ResumeRoot\yyyy-MM-dd\<Company> - <Role>\<Candidate Name>.docx
//                                           \<Candidate Name>.pdf
//                                           \resume-info.json
//
// All of the folder and file-name decisions live here, so neither renderer nor the generator has to
// know anything about dates, sanitization or collisions. Nothing in this file renders a document.
// ---------------------------------------------------------------------------

/// <summary>The record written beside a generated resume, so a folder explains itself later.</summary>
public sealed class ResumeOutputMetadata {
    [JsonPropertyName("jobId")] public string JobId { get; set; } = "";
    [JsonPropertyName("company")] public string Company { get; set; } = "";
    [JsonPropertyName("role")] public string Role { get; set; } = "";
    [JsonPropertyName("jobUrl")] public string JobUrl { get; set; } = "";
    [JsonPropertyName("generatedAt")] public string GeneratedAt { get; set; } = "";
    [JsonPropertyName("docxFile")] public string? DocxFile { get; set; }
    [JsonPropertyName("pdfFile")] public string? PdfFile { get; set; }
}

/// <summary>One job's resolved output paths. Every file in it shares the same revision suffix.</summary>
public sealed class ResumeOutputPaths {
    public string DateFolder { get; init; } = "";
    public string JobFolder { get; init; } = "";
    public string DocxPath { get; init; } = "";
    public string PdfPath { get; init; } = "";
    public string MetadataPath { get; init; } = "";

    /// <summary>1 for "Billy Lin.docx", 2 for "Billy Lin (2).docx", and so on.</summary>
    public int Revision { get; init; } = 1;
}

public static class ResumeOutputManager {
    /// <summary>The document file name used when the profile carries no candidate name.</summary>
    public const string BaseName = "Resume";
    public const string MetadataBaseName = "resume-info";

    /// <summary>
    /// The DOCX/PDF file name for a candidate: "Billy Lin" -> "Billy Lin". Spaces are kept; characters
    /// Windows forbids in a file name are sanitized exactly as folder names are; a missing or blank
    /// name falls back to <see cref="BaseName"/> ("Resume"). Capped so the full path stays short.
    /// </summary>
    public static string DocumentBaseName(string? candidateName) {
        var name = SanitizeFolderName(candidateName);
        if (name.Length > 80) name = name.Substring(0, 80).TrimEnd(' ', '.');
        return name.Length == 0 ? BaseName : name;
    }

    /// <summary>ResumeRoot\yyyy-MM-dd. Reused when it already exists; never created twice.</summary>
    public static string GetDateFolder(string resumeRoot, DateTime? on = null) =>
        Path.Combine(resumeRoot, (on ?? DateTime.Now).ToString("yyyy-MM-dd"));

    /// <summary>ResumeRoot\yyyy-MM-dd\&lt;Company&gt; - &lt;Role&gt;.</summary>
    public static string GetJobFolder(string resumeRoot, string company, string role, DateTime? on = null) =>
        Path.Combine(GetDateFolder(resumeRoot, on), JobFolderName(company, role));

    /// <summary>"Caterpillar Inc - Senior AI Software Engineer". The job data itself is never changed.</summary>
    public static string JobFolderName(string company, string role) {
        var cleanCompany = SanitizeFolderName(company);
        var cleanRole = SanitizeFolderName(role);

        var name = cleanCompany.Length > 0 && cleanRole.Length > 0 ? cleanCompany + " - " + cleanRole
                 : cleanCompany.Length > 0 ? cleanCompany
                 : cleanRole;

        if (name.Length == 0) name = BaseName;
        // Long paths are the one failure a user cannot diagnose, so the name is bounded.
        return name.Length > 120 ? name.Substring(0, 120).TrimEnd(' ', '.') : name;
    }

    /// <summary>
    /// Makes a value safe as a Windows folder name: every invalid character (\ / : * ? " &lt; &gt; |
    /// and the control characters) becomes a space, runs of spaces collapse, and a trailing dot or
    /// space is removed because Windows silently drops those.
    /// </summary>
    public static string SanitizeFolderName(string? value) {
        if (string.IsNullOrWhiteSpace(value)) return "";

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(value.Length);
        foreach (var c in value) sb.Append(invalid.Contains(c) ? ' ' : c);

        var collapsed = string.Join(" ", sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Trim().TrimEnd('.', ' ').Trim();
    }

    /// <summary>
    /// The first free "&lt;base&gt;.ext", "&lt;base&gt; (2).ext", "&lt;base&gt; (3).ext" … in a folder.
    /// </summary>
    public static string GetUniqueFilePath(string folder, string baseName, string extension) =>
        Path.Combine(folder, FileName(baseName, NextRevision(folder, baseName, extension), extension));

    /// <summary>
    /// Resolves every path for one generation. The documents and their metadata share a single
    /// revision, so a second run for the same job on the same day lands as a complete
    /// "&lt;Name&gt; (2)" set rather than overwriting, or half-overwriting, the first one.
    /// The documents are named after <paramref name="candidateName"/> (profile info.name).
    /// </summary>
    /// <summary>
    /// Documents for one email task, written straight into <paramref name="folder"/>.
    /// The same revision rule applies, so a second run does not overwrite the first set.
    /// </summary>
    public static ResumeOutputPaths ResolveInFolder(string folder, string? candidateName = null) {
        var documentBase = DocumentBaseName(candidateName);
        var revision = 1;
        while (Exists(folder, revision, documentBase)) revision++;
        return new ResumeOutputPaths {
            DateFolder = folder,
            JobFolder = folder,
            DocxPath = Path.Combine(folder, FileName(documentBase, revision, ".docx")),
            PdfPath = Path.Combine(folder, FileName(documentBase, revision, ".pdf")),
            MetadataPath = Path.Combine(folder, FileName(MetadataBaseName, revision, ".json")),
            Revision = revision
        };
    }

    public static ResumeOutputPaths Resolve(string resumeRoot, string company, string role, DateTime? on = null,
                                            string? candidateName = null) {
        var documentBase = DocumentBaseName(candidateName);
        var dateFolder = GetDateFolder(resumeRoot, on);
        var jobFolder = Path.Combine(dateFolder, JobFolderName(company, role));

        var revision = 1;
        while (Exists(jobFolder, revision, documentBase)) revision++;

        return new ResumeOutputPaths {
            DateFolder = dateFolder,
            JobFolder = jobFolder,
            DocxPath = Path.Combine(jobFolder, FileName(documentBase, revision, ".docx")),
            PdfPath = Path.Combine(jobFolder, FileName(documentBase, revision, ".pdf")),
            MetadataPath = Path.Combine(jobFolder, FileName(MetadataBaseName, revision, ".json")),
            Revision = revision
        };
    }

    /// <summary>Writes resume-info.json beside the documents. Produced with System.Text.Json only.</summary>
    public static void SaveMetadata(string metadataPath, ResumeOutputMetadata metadata) {
        Directory.CreateDirectory(Path.GetDirectoryName(metadataPath) ?? ".");
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>A revision is taken if any of that generation's three files already exists.</summary>
    static bool Exists(string folder, int revision, string documentBase) =>
        File.Exists(Path.Combine(folder, FileName(documentBase, revision, ".docx")))
        || File.Exists(Path.Combine(folder, FileName(documentBase, revision, ".pdf")))
        || File.Exists(Path.Combine(folder, FileName(MetadataBaseName, revision, ".json")));

    static int NextRevision(string folder, string baseName, string extension) {
        var revision = 1;
        while (File.Exists(Path.Combine(folder, FileName(baseName, revision, extension)))) revision++;
        return revision;
    }

    static string FileName(string baseName, int revision, string extension) =>
        revision <= 1 ? baseName + extension : $"{baseName} ({revision}){extension}";
}
