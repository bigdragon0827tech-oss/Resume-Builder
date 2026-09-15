using System.IO;
using System.Text.Json;

namespace ResumeBuilder;

public sealed class CapturedResult {
    public bool Saved { get; init; }
    public string TargetPath { get; init; } = "";
    public string? RawPath { get; init; }
    public NormalizationReport? Report { get; init; }
    public string? Error { get; init; }
    public string Message { get; init; } = "";
}

/// <summary>
/// Turns a copied AI answer into a saved profile, without depending on any page structure.
///
/// The gate is deliberate: the clipboard watcher sees everything the user copies while a request is
/// pending, so text that is not recognisably a candidate profile is ignored and never stored.
/// Anything that passes the gate goes through the unchanged A6.6.6 pipeline —
/// normalize -> strict validate -> save.
/// </summary>
public static class ResultCapture {
    public const string BaselineJobId = "BASELINE";

    /// <summary>Canonical names plus the aliases the normalizer already understands.</summary>
    static readonly string[] ProfileMarkers = {
        "info", "summary", "skills", "experience", "certifications", "education",
        "workExperience", "work_experience", "employment", "professionalSummary"
    };

    /// <summary>
    /// Cheap, conservative test for "this looks like a candidate profile the AI just produced".
    /// Wrong answers here are safe in both directions: a false negative leaves the manual Result tab
    /// working, and a false positive is still rejected by strict validation before anything is saved.
    /// </summary>
    /// <summary>
    /// True when the copied text is worth running through the pipeline at all — either a profile that
    /// parses, or a recognisable attempt at one that failed (a truncated answer, for instance).
    /// The second case matters: silently ignoring a cut-off answer would leave the user watching a
    /// "waiting" message with no idea why nothing happened.
    /// </summary>
    public static bool ShouldCapture(string? text) =>
        LooksLikeProfileResult(text) || LooksLikeFailedProfileAttempt(text);

    /// <summary>
    /// A response that is clearly meant to be the profile — it came in a json code fence and mentions
    /// the canonical fields — but does not parse or is not shaped like a profile. Captured so the
    /// failure is reported and the raw text is kept, never saved as a profile.
    /// </summary>
    public static bool LooksLikeFailedProfileAttempt(string? text) {
        if (string.IsNullOrWhiteSpace(text) || LooksLikeProfileResult(text)) return false;
        if (text.IndexOf("```json", StringComparison.OrdinalIgnoreCase) < 0) return false;
        var quoted = ProfileMarkers.Count(name => text.Contains('"' + name + '"', StringComparison.OrdinalIgnoreCase));
        return quoted >= 2;
    }

    public static bool LooksLikeProfileResult(string? text) {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 60) return false;

        string json;
        try { json = CandidateProfileStore.ExtractJson(text); }
        catch { return false; }

        try {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions {
                AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip
            });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            if (root.TryGetProperty("profile", out var wrapped) && wrapped.ValueKind == JsonValueKind.Object)
                root = wrapped;

            var hits = ProfileMarkers.Count(name => root.TryGetProperty(name, out _));
            return hits >= 2;
        } catch {
            return false;
        }
    }

    /// <summary>
    /// Where a captured result belongs. The baseline profile is the input to every future job, so a
    /// tailored answer must never overwrite it — otherwise job 2 would be tailored from job 1's output.
    /// </summary>
    public static string TargetPathFor(string? jobId) =>
        IsBaseline(jobId) ? CandidateProfileStore.CandidateProfilePath : ProfileResultStore.ResultPath(jobId!);

    public static bool IsBaseline(string? jobId) =>
        string.IsNullOrWhiteSpace(jobId) || jobId.Equals(BaselineJobId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Runs the captured text through normalize -> strict validate -> save. Never throws.
    /// On failure nothing is written to the profile and the raw answer is kept so it is not lost.
    /// </summary>
    public static CapturedResult Accept(string text, string? jobId) {
        var target = TargetPathFor(jobId);
        var baseline = IsBaseline(jobId);
        try {
            var report = CandidateProfileStore.NormalizeAndSaveTo(text, target);
            return new CapturedResult {
                Saved = true,
                TargetPath = target,
                Report = report,
                Message = baseline
                    ? "Captured the AI response — baseline candidate profile validated and saved."
                    : $"Captured the AI response — tailored profile validated and saved to {Path.GetFileName(target)}."
            };
        } catch (Exception ex) {
            var raw = ProfileResultStore.SaveRaw(jobId, text);
            return new CapturedResult {
                Saved = false,
                TargetPath = target,
                RawPath = raw,
                Error = ex.Message,
                Message = "The copied response did not pass validation, so nothing was saved. " +
                          (raw is null ? "" : "The raw response was kept at " + raw + ". ") + ex.Message
            };
        }
    }
}

/// <summary>Per-job tailored results. The baseline profile keeps its own path and is never touched here.</summary>
public static class ProfileResultStore {
    public static string ResultsDir => Path.Combine(Storage.DataDir, "results");

    public static string ResultPath(string jobId) => Path.Combine(ResultsDir, Safe(jobId) + ".json");
    public static string RawPath(string jobId) => Path.Combine(ResultsDir, Safe(jobId) + ".raw.txt");

    public static string? SaveRaw(string? jobId, string text) {
        try {
            Directory.CreateDirectory(ResultsDir);
            var path = RawPath(string.IsNullOrWhiteSpace(jobId) ? ResultCapture.BaselineJobId : jobId);
            File.WriteAllText(path, text);
            return path;
        } catch {
            return null;
        }
    }

    static string Safe(string jobId) {
        var cleaned = new string(jobId.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? ResultCapture.BaselineJobId : cleaned;
    }
}
