using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ResumeBuilder;

/// <summary>
/// Where the reset is allowed to look. Defaults to the real app data folders; tests pass temporary
/// ones so nothing of the user's is ever involved.
/// </summary>
public sealed class ResetPaths {
    public string ResultsDir { get; init; } = ProfileResultStore.ResultsDir;
    public string PreparedRequestPath { get; init; } = RequestPreparation.PreparedPath;
    public string PreparedRequestTextPath { get; init; } = RequestPreparation.PreparedTextPath;

    public static ResetPaths Default => new();
}

/// <summary>One thing that could not be deleted, and why. Never swallowed.</summary>
public sealed class ResetFailure {
    public string Path { get; init; } = "";
    public string Reason { get; init; } = "";
}

/// <summary>
/// An immutable, fully resolved list of what a reset will remove. Built first, shown to the user,
/// then executed — so what is confirmed is exactly what is deleted.
/// </summary>
public sealed class ResetPlan {
    public IReadOnlyList<JobTask> Tasks { get; init; } = Array.Empty<JobTask>();

    /// <summary>results\&lt;jobId&gt;.* for these jobs, plus the prepared-request pair. Never anything else.</summary>
    public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();

    /// <summary>Generated job folders, only when documents were opted in and every safety check passed.</summary>
    public IReadOnlyList<string> Folders { get; init; } = Array.Empty<string>();

    /// <summary>Date folders to remove ONLY if they are empty once the job folders are gone.</summary>
    public IReadOnlyList<string> DateFolders { get; init; } = Array.Empty<string>();

    public bool IncludesDocuments { get; init; }
    public string ResumeRoot { get; init; } = "";

    public int JobCount => Tasks.Count;
    public int Count(string queueStatus) => Tasks.Count(t => t.Status == queueStatus);

    /// <summary>"141 jobs (130 queued, 8 completed, 3 failed)" — what the confirmation shows.</summary>
    public string JobSummary() {
        var parts = new[] { "Queued", "Processing", "Completed", "Failed" }
            .Select(s => (Status: s, Count: Count(s))).Where(p => p.Count > 0)
            .Select(p => $"{p.Count} {p.Status.ToLowerInvariant()}");
        return $"{JobCount} job{(JobCount == 1 ? "" : "s")}" + (parts.Any() ? $" ({string.Join(", ", parts)})" : "");
    }
}

/// <summary>What actually happened. A failure is reported, never hidden behind a success message.</summary>
public sealed class ResetReport {
    public int FilesDeleted { get; set; }
    public int FoldersDeleted { get; set; }
    public int DateFoldersRemoved { get; set; }
    public List<ResetFailure> Failures { get; } = new();
    public bool AnyFailure => Failures.Count > 0;

    public string Describe() {
        var text = $"Removed {FilesDeleted} file(s)" +
                   (FoldersDeleted > 0 ? $" and {FoldersDeleted} resume folder(s)" : "") +
                   (DateFoldersRemoved > 0 ? $" ({DateFoldersRemoved} empty date folder(s) tidied)" : "") + ".";
        if (!AnyFailure) return text;
        return text + Environment.NewLine + Environment.NewLine +
               $"{Failures.Count} item(s) could NOT be deleted:" + Environment.NewLine +
               string.Join(Environment.NewLine, Failures.Take(10).Select(f => $"  {f.Path} — {f.Reason}")) +
               (Failures.Count > 10 ? Environment.NewLine + $"  … and {Failures.Count - 10} more" : "");
    }
}

/// <summary>
/// Clearing job history for testing: the tasks and the artifacts Resume Builder generated for them.
/// It never touches settings, the candidate or baseline profile, prompts, the Job Browser profile,
/// diagnostics.log, or the Incoming/Imported input files.
///
/// Two rules make it safe. Every path is checked to be UNDER a known Resume Builder root, and a
/// generated folder is identified by its own resume-info.json — <see cref="JobTask.ResumePath"/> is
/// never treated as authority, because a task is data that could name any path at all.
/// This class performs no UI and does not write tasks.json; the caller clears the live list.
/// </summary>
public static class JobHistoryReset {
    /// <summary>A reset is refused while a job is in flight; a running job is never cancelled.</summary>
    public static bool IsProcessing(IEnumerable<JobTask> tasks) =>
        (tasks ?? Enumerable.Empty<JobTask>()).Any(t => t.Status == "Processing");

    /// <summary>True when <paramref name="path"/> is inside <paramref name="root"/> (never the root itself).</summary>
    public static bool IsUnderRoot(string? path, string? root) {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root)) return false;
        try {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var under = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            return full.Length > under.Length
                && full.StartsWith(under + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        } catch { return false; }
    }

    static bool IsDateFolder(string name) =>
        DateTime.TryParseExact(name, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                               System.Globalization.DateTimeStyles.None, out _);

    /// <summary>
    /// Works out exactly what would be removed. Reads only; deletes nothing. A job id that would not
    /// map to its own results file (empty, or the reserved BASELINE id) contributes no file.
    /// </summary>
    public static ResetPlan Plan(IReadOnlyList<JobTask> tasks, AppSettings settings, bool includeDocuments,
                                 ResetPaths? paths = null) {
        tasks ??= Array.Empty<JobTask>();
        paths ??= ResetPaths.Default;

        var files = new List<string>();
        foreach (var job in tasks) {
            var id = (job.JobId ?? "").Trim();
            if (id.Length == 0 || id.Equals(ResultCapture.BaselineJobId, StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var file in new[] { ProfileResultStore.ResultPath(id), ProfileResultStore.RawPath(id),
                                         ProfileResultStore.DocGenLogPath(id), ProfileResultStore.EffectiveStylePath(id) }) {
                // Rebase onto the configured results folder so tests can use a temporary one.
                var candidate = Path.Combine(paths.ResultsDir, Path.GetFileName(file));
                if (Path.GetFileName(candidate).StartsWith(ResultCapture.BaselineJobId + ".", StringComparison.OrdinalIgnoreCase)) continue;
                if (IsUnderRoot(candidate, paths.ResultsDir) && File.Exists(candidate) && !files.Contains(candidate))
                    files.Add(candidate);
            }
        }

        // The prepared request is the last job's, never anyone else's data.
        foreach (var prepared in new[] { paths.PreparedRequestPath, paths.PreparedRequestTextPath })
            if (!string.IsNullOrWhiteSpace(prepared) && File.Exists(prepared)) files.Add(prepared);

        var folders = new List<string>();
        var dateFolders = new List<string>();
        var root = settings?.ResumeRootFolder ?? "";

        if (includeDocuments && !string.IsNullOrWhiteSpace(root) && Directory.Exists(root)) {
            // Ownership is the internal job id inside resume-info*.json. The generated folder name
            // is not a key: two jobs can share a company and title, and a renamed title must not
            // hide or steal a folder.
            var jobIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var folderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var task in tasks) {
                var id = (task.JobId ?? "").Trim();
                if (id.Length == 0) continue;
                jobIds.Add(id);
                if ((task.Company ?? "").Trim().Length == 0 && (task.Title ?? "").Trim().Length == 0) continue;
                folderNames.Add(ResumeOutputManager.JobFolderName(task.Company ?? "", task.Title ?? ""));
            }

            foreach (var dateFolder in SafeDirectories(root)) {
                if (!IsDateFolder(Path.GetFileName(dateFolder)) || !IsUnderRoot(dateFolder, root)) continue;

                var matched = false;
                foreach (var jobFolder in SafeDirectories(dateFolder)) {
                    if (!IsUnderRoot(jobFolder, dateFolder)) continue;
                    if (!OwnedByJob(jobFolder, jobIds)) {
                        if (folderNames.Contains(Path.GetFileName(jobFolder)))
                            PerfLog.Line("IDENTITY unsafe-match-removed context=clear-history");
                        continue;
                    }

                    PerfLog.Line("IDENTITY lookup source=internal id=" + FirstRecordedId(jobFolder));
                    folders.Add(jobFolder);
                    matched = true;
                }
                if (matched) dateFolders.Add(dateFolder);
            }
        }

        return new ResetPlan {
            Tasks = tasks.ToList(), Files = files, Folders = folders, DateFolders = dateFolders,
            IncludesDocuments = includeDocuments, ResumeRoot = root
        };
    }

    /// <summary>
    /// A folder belongs to one of these jobs only if its OWN resume-info.json says so. Several
    /// revisions can sit in one folder (resume-info (2).json); every one of them must name a job
    /// being cleared, so a folder shared with a job that stays is never deleted.
    /// </summary>
    static string FirstRecordedId(string folder) {
        try {
            foreach (var info in Directory.GetFiles(folder, "resume-info*.json")) {
                var recorded = ((string?)JsonNode.Parse(File.ReadAllText(info))?["jobId"])?.Trim();
                if (!string.IsNullOrEmpty(recorded)) return recorded;
            }
        } catch { }
        return "";
    }

    static bool OwnedByJob(string folder, HashSet<string> jobIds) {
        try {
            var infos = Directory.GetFiles(folder, "resume-info*.json");
            if (infos.Length == 0) return false;

            foreach (var info in infos) {
                var recorded = (string?)JsonNode.Parse(File.ReadAllText(info))?["jobId"];
                if (string.IsNullOrWhiteSpace(recorded) || !jobIds.Contains(recorded.Trim())) return false;
            }
            return true;
        } catch { return false; }
    }

    static IEnumerable<string> SafeDirectories(string path) {
        try { return Directory.GetDirectories(path); } catch { return Array.Empty<string>(); }
    }

    /// <summary>
    /// Deletes what the plan lists, re-checking every guard at the moment of deletion. It never throws:
    /// anything that cannot be removed is reported. It does not write tasks.json.
    /// </summary>
    public static ResetReport Execute(ResetPlan plan, ResetPaths? paths = null) {
        var report = new ResetReport();
        if (plan is null) return report;
        paths ??= ResetPaths.Default;

        foreach (var file in plan.Files) {
            var allowed = IsUnderRoot(file, paths.ResultsDir)
                          || string.Equals(file, paths.PreparedRequestPath, StringComparison.OrdinalIgnoreCase)
                          || string.Equals(file, paths.PreparedRequestTextPath, StringComparison.OrdinalIgnoreCase);
            if (!allowed) { report.Failures.Add(new ResetFailure { Path = file, Reason = "outside Resume Builder's own folders; skipped" }); continue; }

            try {
                if (File.Exists(file)) { File.Delete(file); report.FilesDeleted++; }
            } catch (Exception ex) {
                report.Failures.Add(new ResetFailure { Path = file, Reason = ex.GetType().Name + ": " + ex.Message });
            }
        }

        foreach (var folder in plan.Folders) {
            if (!IsUnderRoot(folder, plan.ResumeRoot)) {
                report.Failures.Add(new ResetFailure { Path = folder, Reason = "outside the Resume Root; skipped" });
                continue;
            }
            try {
                if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); report.FoldersDeleted++; }
            } catch (Exception ex) {
                report.Failures.Add(new ResetFailure { Path = folder, Reason = ex.GetType().Name + ": " + ex.Message });
            }
        }

        // A date folder goes only when nothing of the user's is left in it.
        foreach (var dateFolder in plan.DateFolders.Distinct(StringComparer.OrdinalIgnoreCase)) {
            if (!IsUnderRoot(dateFolder, plan.ResumeRoot)) continue;
            try {
                if (Directory.Exists(dateFolder) && !Directory.EnumerateFileSystemEntries(dateFolder).Any()) {
                    Directory.Delete(dateFolder);
                    report.DateFoldersRemoved++;
                }
            } catch (Exception ex) {
                report.Failures.Add(new ResetFailure { Path = dateFolder, Reason = ex.GetType().Name + ": " + ex.Message });
            }
        }

        return report;
    }
}
