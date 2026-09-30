#pragma warning disable CS0105 // The style-test project already imports System.IO globally.
using System.IO;
#pragma warning restore CS0105
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ResumeBuilder;

/// <summary>
/// A Resume Builder copy of the user's tailoring prompt. The file they chose is never modified.
/// Job preparation reads the converted copy when its hash still matches that file.
/// </summary>
public static class PromptConversion {
    /// <summary>Test harnesses set this so existing prepare checks keep reading the source file.</summary>
    public static bool AllowUnadaptedSource { get; set; }

    static string? _rootOverride;

    /// <summary>The open profile's prompt folder, unless a test has pointed it somewhere else.</summary>
    public static string Root {
        get => string.IsNullOrEmpty(_rootOverride)
            ? Path.Combine(ProfileContext.ProfileRoot, "PromptAdaptation")
            : _rootOverride;
        set => _rootOverride = string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static string MetadataPath => Path.Combine(Root, "prompt-adaptation.json");
    public static string ConvertedPath => Path.Combine(Root, "adapted-prompt.txt");

    public const string ReadyStatus = "Prompt ready";
    public const string PreparingStatus = "Preparing prompt...";
    public const string FailedStatus = "Prompt conversion failed";

    public const string OutputModeHtml = "HTML";
    public const int HtmlContractVersion = 1;

    /// <summary>
    /// Output rules the adapted prompt must keep. The user's tailoring strategy stays in their own words.
    /// </summary>
    public const string HtmlContract =
        "Return one HTML document and nothing else. Do not return JSON.\r\n"
        + "Keep the <style> block exactly, including every existing CSS class, font, color, spacing, border, and alignment.\r\n"
        + "Keep the page and layout structure, the header and footer, the section heading markup, the employment metadata layout, the table structure, and the bullet and list style.\r\n"
        + "Keep the original top-level section order exactly. Do not move, merge, split, or reorder sections. Do not add a visible heading when the original section has none. Do not remove Education or Certifications.\r\n"
        + "You may change the headline, the summary, skill categories and items, the number of skill items and groups, allowed job titles, experience bullets, the number of experience bullets, and optional project or subtitle text.\r\n"
        + "When you add a bullet or a skill item, copy the neighboring HTML structure and class. Do not invent new CSS or classes.\r\n"
        + "Do not change the name, contact details, company names, employment dates, locations, employment type, work arrangement, education facts, or certifications.\r\n";

    public sealed class Record {
        public string OriginalPromptPath { get; set; } = "";
        public string OriginalPromptHash { get; set; } = "";
        public string ConvertedPromptPath { get; set; } = "";
        public string ConvertedPromptHash { get; set; } = "";
        public string ConvertedAt { get; set; } = "";
        public string OutputMode { get; set; } = "";
        public int HtmlContractVersion { get; set; }
    }

    public static string HashFile(string path) {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    public static bool Matches(string? originalPath) {
        if (string.IsNullOrWhiteSpace(originalPath) || !File.Exists(originalPath)) return false;
        var record = Load();
        if (record is null || string.IsNullOrEmpty(record.ConvertedPromptPath) || !File.Exists(record.ConvertedPromptPath))
            return false;
        var savedPath = record.OriginalPromptPath ?? "";
        var savedHash = record.OriginalPromptHash ?? "";
        return string.Equals(Path.GetFullPath(originalPath), Path.GetFullPath(savedPath), StringComparison.OrdinalIgnoreCase)
            && string.Equals(savedHash, HashFile(originalPath), StringComparison.OrdinalIgnoreCase)
            && string.Equals(record.OutputMode, OutputModeHtml, StringComparison.Ordinal)
            && record.HtmlContractVersion == HtmlContractVersion;
    }

    /// <summary>
    /// The prompt text a job sends. A matching converted file is used.
    /// An unconverted source is refused, so a failed conversion cannot be sent as if it were ready.
    /// </summary>
    public static string RequireText(string originalPath) {
        if (Matches(originalPath))
            return File.ReadAllText(Load()!.ConvertedPromptPath);
        if (AllowUnadaptedSource)
            return File.ReadAllText(originalPath);
        throw new InvalidOperationException(
            "The tailoring prompt has not been prepared. Open Settings, choose the Tailoring Prompt again, and wait until it says Prompt ready.");
    }

    public static string BuildInstruction(string userPrompt, bool resumeMode) {
        _ = resumeMode;
        return "Adapt the user's resume-tailoring prompt for Resume Builder.\r\n"
            + "Preserve the user's resume-tailoring strategy, rules, tone, constraints, and logic.\r\n"
            + "Change ONLY the final output requirements.\r\n"
            + "Remove or replace any conflicting output-format instructions (plain text, markdown, tables, JSON, or a different HTML shape).\r\n"
            + "Do not rewrite the user's tailoring methodology.\r\n"
            + "The adapted prompt must require the HTML output contract below, unchanged.\r\n"
            + "Return ONLY the full adapted prompt. No commentary before or after it.\r\n\r\n"
            + "===== HTML OUTPUT CONTRACT =====\r\n"
            + HtmlContract + "\r\n===== USER PROMPT =====\r\n"
            + userPrompt;
    }

    /// <summary>
    /// Accepts a converted prompt, or rejects it and leaves the previous converted file in place.
    /// </summary>
    public static bool TrySave(string originalPath, string? reply, out string error) {
        error = "";
        var text = (reply ?? "").Trim();
        if (!IsUsable(text)) {
            error = "ChatGPT did not return a usable Resume Builder prompt. The previous prepared prompt was kept.";
            return false;
        }

        Directory.CreateDirectory(Root);
        var temp = ConvertedPath + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        if (File.Exists(ConvertedPath)) File.Replace(temp, ConvertedPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        else File.Move(temp, ConvertedPath);

        var record = new Record {
            OriginalPromptPath = Path.GetFullPath(originalPath),
            OriginalPromptHash = HashFile(originalPath),
            ConvertedPromptPath = Path.GetFullPath(ConvertedPath),
            ConvertedPromptHash = HashFile(ConvertedPath),
            ConvertedAt = DateTimeOffset.Now.ToString("o"),
            OutputMode = OutputModeHtml,
            HtmlContractVersion = HtmlContractVersion
        };
        var json = JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true });
        var metaTemp = MetadataPath + ".tmp";
        File.WriteAllText(metaTemp, json);
        if (File.Exists(MetadataPath)) File.Replace(metaTemp, MetadataPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        else File.Move(metaTemp, MetadataPath);
        return true;
    }

    public static bool IsUsable(string text) {
        if (text.Length < 80) return false;
        if (ResultCapture.LooksLikeProfileResult(text) && text.TrimStart().StartsWith('{')) return false;
        return text.Contains("<style> block exactly", StringComparison.Ordinal)
            && text.Contains("top-level section order", StringComparison.Ordinal)
            && text.Contains("Do not invent new CSS", StringComparison.Ordinal)
            && text.Contains("certifications", StringComparison.Ordinal);
    }

    public static Record? Load() {
        if (!File.Exists(MetadataPath)) return null;
        try { return JsonSerializer.Deserialize<Record>(File.ReadAllText(MetadataPath)); }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }
}
