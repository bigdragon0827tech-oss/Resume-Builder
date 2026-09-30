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
/// Turns an AI answer into a saved profile. The text may come from an automatic
/// <see cref="ChatResponseReader"/> capture or from the manual clipboard fallback.
/// Text that is not recognisably a candidate profile is ignored and never stored.
/// Anything that passes the gate is normalized and strictly validated before it is saved.
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
    /// True when the captured text is worth running through the pipeline at all — either a profile that
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
    /// <summary>
    /// One safe diagnostics line describing the style a saved profile will render with — sizes and
    /// font only, never resume content: "STYLE accepted preset=promV4.12 body=9 bullet=9 …".
    /// </summary>
    public static string StyleLogLine(System.Text.Json.Nodes.JsonObject? profile) {
        var style = StyleNormalizer.Normalize(profile?["style"]).Style;
        string F(double v) => StyleNormalizer.Fmt(v);
        return $"STYLE accepted preset={style.Preset} body={F(style.Body.FontSize)} bullet={F(style.Bullet.FontSize)} " +
               $"skills={F(style.SkillValues.FontSize)} education={F(style.Education.FontSize)} font={style.Fonts.Family}";
    }

    public static CapturedResult Accept(string text, string? jobId, bool requireStyle = false) {
        var target = TargetPathFor(jobId);
        var baseline = IsBaseline(jobId);
        try {
            var report = CandidateProfileStore.NormalizeAndSaveTo(text, target, requireStyle, ExperienceSource.Load());
            if (baseline) CandidateProfileStore.RecordSourceFromBaseline();
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
    public static string ResultsDir => Path.Combine(ProfileContext.ProfileRoot, "results");

    public static string ResultPath(string jobId) => Path.Combine(ResultsDir, Safe(jobId) + ".json");
    public static string RawPath(string jobId) => Path.Combine(ResultsDir, Safe(jobId) + ".raw.txt");
    public static string DocGenLogPath(string jobId) => Path.Combine(ResultsDir, Safe(jobId) + ".docgen.txt");

    /// <summary>Style system: the fully resolved style a document was rendered with, for debugging.</summary>
    public static string EffectiveStylePath(string? jobId) =>
        Path.Combine(ResultsDir, Safe(string.IsNullOrWhiteSpace(jobId) ? ResultCapture.BaselineJobId : jobId) + ".effective-style.json");

    /// <summary>Records why documents could not be produced. Never touches the validated JSON.</summary>
    public static void SaveDocGenLog(string? jobId, string text) {
        try {
            Directory.CreateDirectory(ResultsDir);
            File.WriteAllText(DocGenLogPath(string.IsNullOrWhiteSpace(jobId) ? ResultCapture.BaselineJobId : jobId),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine + text);
        } catch {
            // Logging must never affect the outcome.
        }
    }

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

/// <summary>
/// A6.6.13 guard for "Copy last code block". The prepared request itself contains code blocks (the
/// master prompt's schema template, for example). If an answer ever arrives without a code block, the
/// shortcut could copy a block from the user's own prompt — and an empty schema template would pass
/// validation and be saved as a "tailored" resume. A genuine answer is never a verbatim slice of the
/// prompt, so text that is one is refused before it reaches the pipeline.
/// </summary>
public static class PromptEchoGuard {
    const int MinimumLength = 40;

    public static bool IsEchoOfPrompt(string? captured, string? preparedRequest) {
        if (string.IsNullOrWhiteSpace(captured) || string.IsNullOrWhiteSpace(preparedRequest)) return false;
        var copied = Collapse(captured);
        if (copied.Length < MinimumLength) return false;
        return Collapse(preparedRequest).Contains(copied, StringComparison.Ordinal);
    }

    /// <summary>Clipboard and code-block copies can change line endings and indentation; compare content.</summary>
    static string Collapse(string text) {
        var sb = new System.Text.StringBuilder(text.Length);
        var inSpace = false;
        foreach (var c in text) {
            if (char.IsWhiteSpace(c)) { if (!inSpace) sb.Append(' '); inSpace = true; }
            else { sb.Append(c); inSpace = false; }
        }
        return sb.ToString().Trim();
    }
}
