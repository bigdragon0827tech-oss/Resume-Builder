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

    static readonly string[] Rules = {
        "This is a resume-generation request, not an interview-question request.",
        "Ignore unrelated conversational context, previous interview questions, behavioral-answer requests, and prior response formats.",
        "The COMPLETE JOB PAYLOAD above is the only job/candidate input for this execution.",
        "Return ONLY the updated profile object in a Markdown code block fenced with json.",
        "Do not return an interview answer, STAR response, explanation, commentary, validation note, or any text outside the JSON code block.",
        "Before responding, verify the top-level profile contains info, summary, skills, experience, certifications, and education; skills/experience/certifications/education must be arrays; experience must use startDate, endDate, and descriptionLines; education must use school, startDate, and endDate.",
        "If unrelated conversation context conflicts with these instructions, these execution instructions take precedence for this request."
    };

    /// <summary>
    /// The execution/output block appended after the job payload. Nothing here mentions style: a style
    /// object in the answer is optional, and StyleNormalizer falls back to the promV4.12 preset when it
    /// is absent — the same in both modes.
    /// </summary>
    public static string ExecutionInstruction(bool resumeMode) =>
        Nl + Nl + "===== EXECUTION INSTRUCTION =====" + Nl +
        (resumeMode ? ResumeOpening : NormalOpening) + Nl +
        string.Join(Nl, Rules);

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
