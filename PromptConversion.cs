#pragma warning disable CS0105 // The style-test project already imports System.IO globally.
using System.IO;
#pragma warning restore CS0105
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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
    public const string OutputModePatch = "Patch";
    public const int HtmlContractVersion = 1;
    public const int ResumePatchContractVersion = 1;
    public const string PatchContractMarker = "===== RESUME BUILDER JSON PATCH OUTPUT CONTRACT =====";

    /// <summary>
    /// The only output contract appended to an imported prompt. The user's tailoring strategy is not rewritten.
    /// </summary>
    public const string PatchContract =
        "Return one JSON object and nothing else. Do not return HTML, CSS, a style block, or a full resume document.\r\n"
        + "Change only ids listed in editableFields. Return only fields you change:\r\n"
        + "{ \"updates\": [ { \"id\": \"summary\", \"value\": [ \"Updated paragraph\" ] } ] }\r\n"
        + "Use the type named on that id: string, nullableString, stringArray, skillGroups, or projectArray.\r\n"
        + "skillGroups is an array of { \"label\": \"...\", \"items\": [ \"...\" ] }.\r\n"
        + "stringArray and projectArray are arrays of strings. nullableString is a string or null.\r\n"
        + "Do not return unchanged values, unknown ids, or locked facts.\r\n";

    static readonly string[] OutputHeadings = {
        "OUTPUT FORMAT", "OUTPUT CONTRACT", "RESPONSE FORMAT", "RETURN FORMAT", "HTML CONTRACT", "FINAL OUTPUT"
    };

    public sealed class Record {
        public string OriginalPromptPath { get; set; } = "";
        public string OriginalPromptHash { get; set; } = "";
        public string ConvertedPromptPath { get; set; } = "";
        public string ConvertedPromptHash { get; set; } = "";
        public string ConvertedAt { get; set; } = "";
        public string OutputMode { get; set; } = "";
        public int HtmlContractVersion { get; set; }
        public int ResumePatchContractVersion { get; set; }
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
            && string.Equals(record.OutputMode, OutputModePatch, StringComparison.Ordinal)
            && record.ResumePatchContractVersion == ResumePatchContractVersion;
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

    /// <summary>Deterministic adapted prompt. The file the user imported is not modified.</summary>
    public static string Adapt(string? userPrompt) {
        var kept = RemoveOutputSections(userPrompt ?? "");
        return kept.TrimEnd() + "\r\n\r\n" + PatchContractMarker + "\r\n" + PatchContract;
    }

    public static string BuildInstruction(string userPrompt, bool resumeMode) {
        _ = resumeMode;
        return Adapt(userPrompt);
    }

    public static bool TryAdapt(string originalPath, out string error) =>
        TrySave(originalPath, Adapt(File.ReadAllText(originalPath)), out error);

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
            OutputMode = OutputModePatch,
            HtmlContractVersion = HtmlContractVersion,
            ResumePatchContractVersion = ResumePatchContractVersion
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
        return text.Contains(PatchContractMarker, StringComparison.Ordinal)
            && text.Contains("\"updates\"", StringComparison.Ordinal);
    }

    static string RemoveOutputSections(string prompt) {
        var lines = Regex.Split(prompt, "\r\n|\n|\r");
        var kept = new List<string>();
        var skipping = false;
        foreach (var line in lines) {
            if (IsHeading(line) && IsOutputHeading(line)) {
                skipping = true;
                continue;
            }
            if (skipping && IsHeading(line) && !IsOutputHeading(line)) skipping = false;
            if (!skipping) kept.Add(line);
        }
        return string.Join("\r\n", kept);
    }

    static bool IsHeading(string line) {
        var text = line.Trim();
        if (text.Length == 0 || text.Length > 80) return false;
        if (text.StartsWith('#')) return true;
        if (text.Contains("====", StringComparison.Ordinal)) return true;
        var letters = text.Count(char.IsLetter);
        if (letters < 4) return false;
        return text.Count(char.IsUpper) >= letters * 0.75 && !text.EndsWith('.') && !text.EndsWith('?');
    }

    static bool IsOutputHeading(string line) {
        var compact = Regex.Replace(line.ToUpperInvariant(), @"[^A-Z ]", " ");
        compact = Regex.Replace(compact, @"\s+", " ").Trim();
        foreach (var heading in OutputHeadings)
            if (compact.Contains(heading, StringComparison.Ordinal)) return true;
        return false;
    }

    public static Record? Load() {
        if (!File.Exists(MetadataPath)) return null;
        try { return JsonSerializer.Deserialize<Record>(File.ReadAllText(MetadataPath)); }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }
}
