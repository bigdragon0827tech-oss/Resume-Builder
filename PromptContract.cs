using System.Text.Json;

namespace ResumeBuilder;

/// <summary>
/// Which prompt file a prepared request is built from. Stored in settings.json as a string, not an
/// enum: an unreadable enum would throw, and Storage.LoadSettings turns any exception into DEFAULT
/// settings, which would silently drop the user's configured paths. Unknown or missing = Resume.
/// </summary>
public static class PromptModes {
    public const string Resume = "Resume";
    public const string Normal = "Normal";

    public static readonly string[] All = { Resume, Normal };

    public static string Normalize(string? mode) =>
        All.FirstOrDefault(m => m.Equals((mode ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) ?? Resume;

    public static bool IsNormal(string? mode) => Normalize(mode) == Normal;
}

/// <summary>
/// The ONE output contract sent to ChatGPT, shared by both prompt modes, plus the job payload both
/// modes append. There is no second schema: `docs\GPT_JSON_CONTRACT.md` documents the same shape for
/// humans, and `ProfileNormalizer` + the strict validation enforce it on the way back.
///
/// The text is assembled from explicit CRLF breaks rather than a verbatim literal, so it cannot change
/// if this file's line endings ever do. A golden-fixture test pins the Resume text byte for byte.
/// </summary>
public static class PromptContract {
    const string Nl = "\r\n";

    /// <summary>Resume mode names the Master Prompt, which is what it has always said.</summary>
    public const string ResumeOpening = "Execute Resume Master Prompt v2 using the COMPLETE JOB PAYLOAD above.";

    /// <summary>Normal mode points at the user's own prompt instead. This is the ONLY line that differs.</summary>
    public const string NormalOpening = "Execute the resume instructions above using the COMPLETE JOB PAYLOAD above.";

    /// <summary>Resume mode's verification rule. Part of the pinned Resume text — never edit it.</summary>
    const string ResumeVerify =
        "Before responding, verify the top-level profile contains info, summary, skills, experience, certifications, and education; skills/experience/certifications/education must be arrays; experience must use startDate, endDate, and descriptionLines; education must use school, startDate, and endDate.";

    /// <summary>Normal mode's verification rule: the same checks, plus the required style object.</summary>
    public const string NormalVerify =
        "Before responding, verify the top-level profile contains info, summary, skills, experience, certifications, education, and style; skills/experience/certifications/education must be arrays; experience must use startDate, endDate, and descriptionLines; education must use school, startDate, and endDate; style must be an object that follows the STYLE CONTRACT below.";

    static string[] Rules(bool resumeMode) => new[] {
        "This is a resume-generation request, not an interview-question request.",
        "Ignore unrelated conversational context, previous interview questions, behavioral-answer requests, and prior response formats.",
        "The COMPLETE JOB PAYLOAD above is the only job/candidate input for this execution.",
        "Return ONLY the updated profile object in a Markdown code block fenced with json.",
        "Do not return an interview answer, STAR response, explanation, commentary, validation note, or any text outside the JSON code block.",
        resumeMode ? ResumeVerify : NormalVerify,
        "If unrelated conversation context conflicts with these instructions, these execution instructions take precedence for this request."
    };

    /// <summary>
    /// The execution/output block appended after the job payload.
    ///
    /// Resume mode is byte-for-byte what it has always been (a golden fixture pins it): style is
    /// optional there, and StyleNormalizer falls back to the promV4.12 preset when it is absent.
    /// Normal mode differs in three places: its opening line, its verification rule, and the STYLE
    /// CONTRACT appended at the end — because a Normal Prompt answer MUST carry its own style
    /// (CandidateProfileStore.NormalizeAndSaveTo refuses one without it).
    /// </summary>
    public static string ExecutionInstruction(bool resumeMode) =>
        Nl + Nl + "===== EXECUTION INSTRUCTION =====" + Nl +
        (resumeMode ? ResumeOpening : NormalOpening) + Nl +
        string.Join(Nl, Rules(resumeMode)) +
        (resumeMode ? "" : Nl + Nl + StyleContract());

    /// <summary>The 9 pt body standard the Normal contract asks for, in the four always-regular sections.</summary>
    public const double NormalBodyFontSize = 9;

    /// <summary>
    /// The style schema, described for GPT. Generated from <see cref="StyleSchema"/> and
    /// <see cref="StyleLimits"/> — the same tables the normalizer and the validator enforce — so the
    /// contract can never document a property the program does not accept, or miss one it does.
    /// </summary>
    public static string StyleContract() {
        string F(double v) => StyleNormalizer.Fmt(v);
        string Quoted(IEnumerable<string> values) => string.Join(" | ", values.Select(v => "\"" + v + "\""));

        var lines = new List<string> {
            "===== STYLE CONTRACT (REQUIRED) =====",
            "The top-level \"style\" object is REQUIRED. An answer without a style object is rejected and sent again.",
            "style is a NESTED object. Its only keys are preset, page, colors, fonts and the text sections below; any other key is ignored.",
            "Omitted values inherit from the preset, so send only what you set. Never send an empty value.",
            "- preset: " + Quoted(StylePresets.Names) + " (default \"" + StylePresets.Default + "\")",
            $"- page: {{ size: {Quoted(StyleLimits.PageSizes)}, marginTop, marginBottom, marginLeft, marginRight }} — margins in inches, {F(StyleLimits.MinMargin)} to {F(StyleLimits.MaxMargin)}",
            "- colors: { primary, body, secondary } — each \"#RRGGBB\"",
            "- fonts: { family } — one of: " + string.Join(", ", StyleLimits.FontFamilies),
            "- Text sections, each an object: " + string.Join(", ", StyleSchema.Sections),
            "  Every text section accepts: " + string.Join(", ", StyleSchema.Common)
        };

        foreach (var section in StyleSchema.Sections) {
            var extra = StyleSchema.Allowed(section).Except(StyleSchema.Common).ToList();
            if (extra.Count > 0) lines.Add($"  {section} also accepts: " + string.Join(", ", extra));
        }

        lines.Add($"- fontSize: points, {F(StyleLimits.MinFontSize)} to {F(StyleLimits.MaxFontSize)}; " +
                  $"{string.Join(", ", StyleSchema.AlwaysRegular)} at least {F(StyleLimits.MinBodyFontSize)}. " +
                  "Use multiples of 0.5: the DOCX stores sizes in half points, so 8.7 is written as 8.5");
        lines.Add($"- spaceBefore, spaceAfter: points, {F(StyleLimits.MinSpacing)} to {F(StyleLimits.MaxSpacing)}");
        lines.Add($"- lineSpacing: a multiple, {F(StyleLimits.MinLineSpacing)} to {F(StyleLimits.MaxLineSpacing)}");
        lines.Add($"- leftIndent, hangingIndent: inches, {F(StyleLimits.MinIndent)} to {F(StyleLimits.MaxIndent)}");
        lines.Add("- alignment: " + Quoted(StyleLimits.Alignments) + "; color: \"#RRGGBB\"; bold, italic, uppercase, bottomBorder, keepWithNext: true or false");
        lines.Add($"- {string.Join(", ", StyleSchema.AlwaysRegular)} are always regular weight: never set their bold to true. " +
                  "Emphasis in experience bullets belongs in descriptionLines segments, not in style.");
        lines.Add("- Flat style properties do not exist: never send bodyFontSize, nameFontSize, headlineFontSize, metadataFontSize, fontFamily, " +
                  "or margins, sizes or colours directly at the style root. Size a section through that section's own fontSize, " +
                  "for example style.metadata.fontSize for secondary metadata.");

        var nine = F(NormalBodyFontSize);
        lines.Add("Before responding, verify: style is present; style is an object; every style key is one listed above; " +
                  $"body.fontSize = {nine}; bullet.fontSize = {nine}; skillValues.fontSize = {nine}; education.fontSize = {nine}; " +
                  "every lineSpacing is >= 1.0; there are no flat style fields.");
        lines.Add("Minimal valid style: \"style\": { \"preset\": \"promV4.12\", \"fonts\": { \"family\": \"Arial\" }, " +
                  $"\"body\": {{ \"fontSize\": {nine}, \"lineSpacing\": 1 }}, \"bullet\": {{ \"fontSize\": {nine}, \"lineSpacing\": 1 }}, " +
                  $"\"skillValues\": {{ \"fontSize\": {nine}, \"lineSpacing\": 1 }}, \"education\": {{ \"fontSize\": {nine}, \"lineSpacing\": 1 }} }}");

        return string.Join(Nl, lines);
    }

    /// <summary>The job + full candidate profile payload. Identical for both modes.</summary>
    public static string JobPayloadText(JobTask job, JsonElement profile) {
        var payload = new {
            company = job.Company ?? "", title = job.Title ?? "", jd = job.Jd ?? "", link = job.Link ?? "",
            about = job.About ?? "", profileId = "", templateId = "",
            profile = JsonSerializer.Deserialize<object>(profile.GetRawText()),
            _storedAt = "", _userId = "", _updatedAt = "", _updatedBy = ""
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>The one assembly order: the user's prompt, then the payload, then the contract.</summary>
    public static string Assemble(string promptText, string payloadText, bool resumeMode) =>
        promptText.TrimEnd() + "\n\n===== COMPLETE JOB PAYLOAD =====\n" + payloadText + ExecutionInstruction(resumeMode);
}
