using System.IO;

namespace ResumeBuilder;

// ---------------------------------------------------------------------------
// Making a generated resume easy to UPLOAD to a job site.
//
// Job sites open the Windows file picker for "Upload resume", and that picker accepts a full path
// pasted into its File name box. So this class hands out plain absolute Windows paths — never a
// file:// URL — (Copy Resume Path gives the job's resume FOLDER) and keeps one stable alias,
// Documents\ResumeAutomation\CurrentResume.docx, that always holds the newest generated DOCX.
//
// It holds no UI and never touches a job's queue or application status. The clipboard setter and
// the alias path are injected, so every rule here is testable without the real clipboard or the
// user's Documents folder.
// ---------------------------------------------------------------------------

/// <summary>What a Copy Resume Path action did, ready to show in a status line.</summary>
public sealed class ResumePathCopy {
    public bool Copied { get; init; }

    /// <summary>The path placed on the clipboard; null when nothing was copied.</summary>
    public string? Path { get; init; }

    public string Message { get; init; } = "";
}

/// <summary>What refreshing CurrentResume.docx did. A failure never affects the job.</summary>
public sealed class CurrentResumeUpdate {
    public bool Updated { get; init; }
    public string AliasPath { get; init; } = "";
    public string? Reason { get; init; }

    /// <summary>The one diagnostics line: never resume content, only the job id and a path or reason.</summary>
    public string LogLine(string jobId) => Updated
        ? $"CURRENT RESUME updated {jobId} {AliasPath}"
        : $"WARN current resume alias update failed {jobId} {Reason}";
}

public static class ResumeUpload {
    /// <summary>Shown whenever there is no usable DOCX — a bogus path is never copied.</summary>
    public const string NotFound = "Resume DOCX not found.";

    public const string AliasFileName = "CurrentResume.docx";

    /// <summary>
    /// Documents\ResumeAutomation\CurrentResume.docx for whoever is signed in — resolved through the
    /// Documents known folder, so no user name is ever written into the code.
    /// </summary>
    public static string DefaultCurrentResumePath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                               "ResumeAutomation", AliasFileName);

    /// <summary>
    /// Where CurrentResume.docx lives for a given Resume Root, so it follows the user's configuration:
    /// <list type="bullet">
    /// <item>"…\ResumeAutomation\Resumes" (a root folder named Resumes) -> its parent,
    ///   "…\ResumeAutomation\CurrentResume.docx" — the default layout.</item>
    /// <item>any other root -> inside it, "&lt;root&gt;\CurrentResume.docx", so a custom root never
    ///   writes into a folder the user did not choose.</item>
    /// <item>a "Resumes" folder directly on a drive -> inside it too (a drive root is not writable).</item>
    /// <item>blank -> the Documents default.</item>
    /// </list>
    /// </summary>
    public static string CurrentResumePathFor(string? resumeRoot) {
        if (string.IsNullOrWhiteSpace(resumeRoot)) return DefaultCurrentResumePath;

        var root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(resumeRoot.Trim()));
        var parent = System.IO.Path.GetDirectoryName(root);
        var isDriveRoot = parent is null || System.IO.Path.GetPathRoot(parent) == parent;

        var folder = string.Equals(System.IO.Path.GetFileName(root), "Resumes", StringComparison.OrdinalIgnoreCase) && !isDriveRoot
            ? parent!
            : root;
        return System.IO.Path.Combine(folder, AliasFileName);
    }

    /// <summary>
    /// The exact DOCX generated for this job: the <see cref="JobTask.ResumePath"/> the generator
    /// recorded (and that Open Resume opens) — not a guess from scanning folders, so a job whose
    /// document is "Billy Lin (2).docx" gets that file. Null unless it is an absolute path to a .docx
    /// that exists. ResumePath can name the PDF when DOCX output is switched off; that is refused
    /// here, because the DOCX is the upload target.
    /// </summary>
    public static string? DocxPathFor(JobTask? job) {
        var path = job?.ResumePath?.Trim();
        if (string.IsNullOrEmpty(path)) return null;
        if (!System.IO.Path.IsPathFullyQualified(path)) return null;
        if (!string.Equals(System.IO.Path.GetExtension(path), ".docx", StringComparison.OrdinalIgnoreCase)) return null;

        var full = System.IO.Path.GetFullPath(path);
        return File.Exists(full) ? full : null;
    }

    /// <summary>
    /// Copies the folder containing the job's exact DOCX to the clipboard through <paramref name="setText"/>. The app
    /// passes a thin adapter over ClipboardService.SetText — the only clipboard implementation, which
    /// decides success by reading the clipboard back. Never throws.
    /// </summary>
    public static ResumePathCopy CopyResumePath(JobTask? job, Func<string, (bool Success, string Message)> setText) {
        // The FOLDER holding this job's exact DOCX, not the file itself: pasted into a file picker it
        // opens that folder, so the right Billy Lin.docx / Billy Lin (2).docx is one click away.
        if (DocxPathFor(job) is not string path ||
            System.IO.Path.GetDirectoryName(path) is not string folder ||
            !Directory.Exists(folder))
            return new ResumePathCopy { Copied = false, Message = NotFound };

        var jobId = (job?.JobId ?? "").Trim();
        if (jobId.Length > 0)
            PerfLog.Line("IDENTITY lookup source=internal id=" + jobId);

        return CopyPath(folder, setText, "Resume folder path copied.");
    }

    /// <summary>Copies the CurrentResume.docx alias path — only when that file really exists.</summary>
    public static ResumePathCopy CopyCurrentResumePath(string aliasPath, Func<string, (bool Success, string Message)> setText) {
        if (string.IsNullOrWhiteSpace(aliasPath) || !File.Exists(aliasPath))
            return new ResumePathCopy { Copied = false, Message = "CurrentResume.docx not found — generate a resume first." };

        return CopyPath(System.IO.Path.GetFullPath(aliasPath), setText,
            "Copied the CurrentResume.docx path. Paste it into the upload dialog's File name box.");
    }

    static ResumePathCopy CopyPath(string path, Func<string, (bool Success, string Message)> setText, string success) {
        (bool Success, string Message) result;
        try { result = setText(path); }
        catch (Exception ex) { result = (false, ex.Message); }

        return result.Success
            ? new ResumePathCopy { Copied = true, Path = path, Message = success }
            : new ResumePathCopy { Copied = false, Message = "The resume path could not be copied: " + result.Message };
    }

    /// <summary>
    /// After a generation run: when a DOCX was produced, refreshes CurrentResume.docx from it. It only
    /// READS the job (for its id) and the generation result — the job's queue status, application
    /// status and ResumePath are never touched, so an alias failure can never fail a job.
    /// Returns null when there was no DOCX to publish.
    /// </summary>
    public static CurrentResumeUpdate? PublishCurrentResume(JobTask job, GenerationResult generation, string aliasPath) {
        if (!generation.DocxGenerated || generation.DocxPath is null) return null;
        return UpdateCurrentResume(generation.DocxPath, aliasPath);
    }

    /// <summary>
    /// Replaces <paramref name="aliasPath"/> with a copy of <paramref name="docxPath"/>. The source is
    /// only read — never moved or deleted. The copy goes to a temporary file in the same folder, is
    /// checked to be complete, and only then replaces the alias, so a failure leaves the previous
    /// alias intact and never a zero-byte or half-written CurrentResume.docx. Never throws.
    /// </summary>
    public static CurrentResumeUpdate UpdateCurrentResume(string docxPath, string aliasPath) {
        var temp = aliasPath + ".tmp";

        try {
            if (!File.Exists(docxPath))
                return new CurrentResumeUpdate { AliasPath = aliasPath, Reason = "the generated DOCX is missing" };

            var folder = System.IO.Path.GetDirectoryName(aliasPath);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

            if (File.Exists(temp)) File.Delete(temp);
            File.Copy(docxPath, temp);

            var expected = new FileInfo(docxPath).Length;
            var copied = new FileInfo(temp).Length;
            if (copied == 0 || copied != expected)
                return new CurrentResumeUpdate { AliasPath = aliasPath, Reason = "the copy was incomplete" };

            if (File.Exists(aliasPath)) File.Replace(temp, aliasPath, destinationBackupFileName: null);
            else File.Move(temp, aliasPath);

            return new CurrentResumeUpdate { Updated = true, AliasPath = aliasPath };
        } catch (Exception ex) {
            // Typically CurrentResume.docx is open in Word, or the folder is read-only.
            return new CurrentResumeUpdate {
                AliasPath = aliasPath,
                Reason = ex is IOException or UnauthorizedAccessException
                    ? "the file is open in another program or read-only (" + ex.GetType().Name + ")"
                    : ex.GetType().Name
            };
        } finally {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
        }
    }
}
