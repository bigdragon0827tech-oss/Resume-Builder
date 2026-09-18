using System.Diagnostics;
using System.IO;

namespace ResumeBuilder;

// ---------------------------------------------------------------------------
// Job application tracking.
//
// Kept entirely separate from resume generation: nothing here renders, normalizes, captures or
// queues anything. It only answers "where has this application got to", and the only transition it
// makes on its own is the one Resume Builder can actually verify — a resume was generated, so the
// application is Ready. Everything after that is the user's own knowledge and must be entered by hand.
// ---------------------------------------------------------------------------

/// <summary>The five application states, in order.</summary>
public static class ApplicationStatus {
    public const string Viewed = "Viewed";
    public const string Ready = "Ready";
    public const string Applied = "Applied";
    public const string Interview = "Interview";
    public const string Done = "Done";

    /// <summary>The filter's "no filter" entry. Never stored on a task.</summary>
    public const string All = "All";

    public static readonly string[] Ordered = { Viewed, Ready, Applied, Interview, Done };

    public static readonly string[] Filters = { All, Viewed, Ready, Applied, Interview, Done };

    public static bool IsKnown(string? value) =>
        value is not null && Ordered.Any(s => s.Equals(value, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Anything unrecognised — missing, empty, or written by an older version — is Viewed. That is
    /// what keeps a tasks.json from before tracking existed loading without a migration step.
    /// </summary>
    public static string Normalize(string? value) =>
        Ordered.FirstOrDefault(s => s.Equals((value ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) ?? Viewed;

    /// <summary>0-based position in the flow, used to keep the stage timestamps consistent.</summary>
    public static int Index(string? value) => Array.IndexOf(Ordered, Normalize(value));
}

/// <summary>Counts and conversion rates over a set of tasks. Plain numbers, no charting.</summary>
public sealed class JobStatistics {
    public int Total { get; init; }
    public int Viewed { get; init; }
    public int Ready { get; init; }
    public int Applied { get; init; }
    public int Interview { get; init; }
    public int Done { get; init; }

    /// <summary>Of everything tracked, how much was actually applied for (Applied and beyond).</summary>
    public double AppliedRate => Total == 0 ? 0 : (double)AppliedOrLater / Total;

    /// <summary>Of the applications sent, how many reached an interview.</summary>
    public double InterviewRate => AppliedOrLater == 0 ? 0 : (double)InterviewOrLater / AppliedOrLater;

    /// <summary>Of the interviews, how many ran to completion.</summary>
    public double CompletionRate => InterviewOrLater == 0 ? 0 : (double)Done / InterviewOrLater;

    // A job that reached Interview was applied for, even though it no longer sits in Applied.
    public int AppliedOrLater => Applied + Interview + Done;
    public int InterviewOrLater => Interview + Done;

    public static string Percent(double rate) => (rate * 100).ToString("0.#") + "%";

    /// <summary>
    /// The share the first of two adjacent stages holds of the pair: A / (A + B). With 30 in A and
    /// 20 in B that is 60%, not the 66.7% a B/A conversion would give. An empty pair is 0.
    /// </summary>
    public static double PairShare(int a, int b) => a + b == 0 ? 0 : a / (double)(a + b);

    /// <summary>Whole percentages, which is how the dashboard shows a pair share.</summary>
    public static string WholePercent(double rate) => Math.Round(rate * 100).ToString("0") + "%";

    /// <summary>"60% Ready vs 40% Applied" — both sides, so the number cannot be misread.</summary>
    public static string PairSplit(string firstStage, int a, string secondStage, int b) =>
        $"{WholePercent(PairShare(a, b))} {firstStage} vs {WholePercent(PairShare(b, a))} {secondStage}";
}

/// <summary>The three headline numbers on the dashboard, all derived from AppliedAt.</summary>
public sealed class ApplicationActivity {
    /// <summary>Applications submitted on the local current date.</summary>
    public int Today { get; init; }

    /// <summary>Applications submitted in the last 30 days, today included.</summary>
    public int Last30Days { get; init; }

    /// <summary>Every job that reached Applied, Interview or Done — each counted once.</summary>
    public int Total { get; init; }
}

/// <summary>One bar of the activity chart.</summary>
public sealed class DailyApplicationCount {
    public DateTime Date { get; init; }
    public int Count { get; init; }
    public string Label => Date.ToString("MMM d");
}

/// <summary>One stage of the pipeline.</summary>
public sealed class PipelineStage {
    public string Status { get; init; } = "";
    public int Count { get; init; }
}

/// <summary>The date ranges the dashboard filters by.</summary>
public static class DateFilter {
    public const string AllDates = "Any date";
    public const string Today = "Today";
    public const string Last3 = "Last 3 days";
    public const string Last7 = "Last 7 days";
    public const string Last15 = "Last 15 days";
    public const string Last30 = "Last 30 days";

    public static readonly string[] Options = { Today, Last3, Last7, Last15, Last30, AllDates };

    /// <summary>How many days back a quick choice covers, today included.</summary>
    public static int Days(string? filter) => filter switch {
        Today => 1, Last3 => 3, Last7 => 7, Last15 => 15, Last30 => 30, _ => 0
    };

    /// <summary>The earliest date a filter admits, or null for no limit.</summary>
    public static DateTime? Since(string? filter, DateTime? now = null) {
        var days = Days(filter);
        return days == 0 ? null : (now ?? DateTime.Now).Date.AddDays(-(days - 1));
    }
}

public static class JobTracker {
    /// <summary>
    /// Sets the application status by hand. Returns false when nothing changed, so a caller can skip
    /// a pointless save. Entering a stage stamps its time if it has none; a stamp already set is never
    /// rewritten or cleared, so moving a task back and forward keeps the real history.
    /// </summary>
    public static bool UpdateStatus(JobTask job, string status, DateTime? at = null) {
        var target = ApplicationStatus.Normalize(status);
        var previous = job.ApplicationStatus;
        if (previous == target) return false;

        var now = at ?? DateTime.Now;
        job.ApplicationStatus = target;
        job.UpdatedAt = now;
        StampStage(job, target, now);

        PerfLog.Line($"TRACKING status {job.JobId} {previous} -> {target}");
        job.NotifyTrackingChanged();
        return true;
    }

    /// <summary>
    /// Records when a stage was reached. An existing stamp always wins, and any earlier stage that was
    /// skipped is filled in with the same time: a job moved straight to Interview was certainly applied
    /// for, and without an AppliedAt it would count in the totals but vanish from the activity chart.
    /// </summary>
    static void StampStage(JobTask job, string status, DateTime now) {
        var reached = ApplicationStatus.Index(status);

        if (reached >= ApplicationStatus.Index(ApplicationStatus.Viewed)) job.ViewedAt ??= now;
        if (reached >= ApplicationStatus.Index(ApplicationStatus.Ready)) job.ReadyAt ??= now;
        if (reached >= ApplicationStatus.Index(ApplicationStatus.Applied)) job.AppliedAt ??= now;
        if (reached >= ApplicationStatus.Index(ApplicationStatus.Interview)) job.InterviewAt ??= now;
        if (reached >= ApplicationStatus.Index(ApplicationStatus.Done)) job.DoneAt ??= now;
    }

    /// <summary>
    /// The one automatic transition: a resume exists, so the application is Ready to send.
    /// It never moves a job backwards — regenerating the documents for a job already marked Applied
    /// records the new file without undoing what the user told us.
    /// </summary>
    public static bool MarkResumeReady(JobTask job, string? resumePath, DateTime? at = null) {
        var now = at ?? DateTime.Now;
        var changed = false;

        if (!string.IsNullOrWhiteSpace(resumePath) && job.ResumePath != resumePath) {
            job.ResumePath = resumePath!;
            changed = true;
        }

        if (job.ApplicationStatus == ApplicationStatus.Viewed) {
            job.ApplicationStatus = ApplicationStatus.Ready;
            job.ReadyAt ??= now;
            PerfLog.Line($"TRACKING status {job.JobId} {ApplicationStatus.Viewed} -> {ApplicationStatus.Ready}");
            changed = true;
        }

        if (changed) {
            job.UpdatedAt = now;
            job.NotifyTrackingChanged();
        }
        return changed;
    }

    /// <summary>Tasks in one status, or all of them for null / "All". Order is never changed.</summary>
    public static List<JobTask> GetTasksByStatus(IEnumerable<JobTask> tasks, string? status) {
        var all = tasks ?? Enumerable.Empty<JobTask>();
        if (string.IsNullOrWhiteSpace(status) || status == ApplicationStatus.All) return all.ToList();

        var wanted = ApplicationStatus.Normalize(status);
        return all.Where(t => ApplicationStatus.Normalize(t.ApplicationStatus) == wanted).ToList();
    }

    public static JobStatistics GetStatistics(IEnumerable<JobTask> tasks) {
        var all = (tasks ?? Enumerable.Empty<JobTask>()).ToList();
        int Count(string status) => all.Count(t => ApplicationStatus.Normalize(t.ApplicationStatus) == status);

        return new JobStatistics {
            Total = all.Count,
            Viewed = Count(ApplicationStatus.Viewed),
            Ready = Count(ApplicationStatus.Ready),
            Applied = Count(ApplicationStatus.Applied),
            Interview = Count(ApplicationStatus.Interview),
            Done = Count(ApplicationStatus.Done)
        };
    }

    // ---------- dashboard ----------

    /// <summary>
    /// Today / last 30 days / total, counted from AppliedAt because that is the moment an application
    /// actually existed. A job at Interview or Done was applied for too, so it keeps its AppliedAt and
    /// is counted exactly once here.
    /// </summary>
    public static ApplicationActivity GetApplicationActivity(IEnumerable<JobTask> tasks, DateTime? now = null) {
        var today = (now ?? DateTime.Now).Date;
        var applied = AppliedJobs(tasks).ToList();

        return new ApplicationActivity {
            Today = applied.Count(t => t.AppliedAt!.Value.Date == today),
            Last30Days = applied.Count(t => t.AppliedAt!.Value.Date >= today.AddDays(-29) && t.AppliedAt!.Value.Date <= today),
            Total = CountAppliedOrLater(tasks)
        };
    }

    /// <summary>
    /// Bars for the activity chart, oldest first. A day window (7, 30) includes every day in the
    /// window, zeros included, so the axis is continuous; days &lt;= 0 means all time, which lists only
    /// the days that actually have applications so a year of history stays readable.
    /// </summary>
    public static List<DailyApplicationCount> GetDailyApplicationCounts(IEnumerable<JobTask> tasks, int days, DateTime? now = null) {
        var today = (now ?? DateTime.Now).Date;

        var byDay = AppliedJobs(tasks)
            .GroupBy(t => t.AppliedAt!.Value.Date)
            .ToDictionary(g => g.Key, g => g.Count());

        if (days <= 0)
            return byDay.OrderBy(pair => pair.Key)
                        .Select(pair => new DailyApplicationCount { Date = pair.Key, Count = pair.Value })
                        .ToList();

        return Enumerable.Range(0, days)
                         .Select(offset => today.AddDays(offset - days + 1))
                         .Select(date => new DailyApplicationCount {
                             Date = date,
                             Count = byDay.TryGetValue(date, out var count) ? count : 0
                         })
                         .ToList();
    }

    /// <summary>The five stages with their current counts, in flow order.</summary>
    public static List<PipelineStage> GetPipelineCounts(IEnumerable<JobTask> tasks) {
        var all = (tasks ?? Enumerable.Empty<JobTask>()).ToList();
        return ApplicationStatus.Ordered
            .Select(status => new PipelineStage {
                Status = status,
                Count = all.Count(t => ApplicationStatus.Normalize(t.ApplicationStatus) == status)
            })
            .ToList();
    }

    /// <summary>
    /// The list and board share one filter path: free text, then the status filter, then either one
    /// exact date or a quick range over each job's tracking date. Order is never changed.
    /// </summary>
    public static List<JobTask> ApplyFilters(IEnumerable<JobTask> tasks, string? search, string? status,
                                             string? dateFilter, DateTime? now = null, DateTime? exactDate = null) {
        IEnumerable<JobTask> result = Search(GetTasksByStatus(tasks, status), search);

        if (exactDate is DateTime day) return result.Where(t => t.TrackingDate.Date == day.Date).ToList();

        if (DateFilter.Since(dateFilter, now) is DateTime since)
            result = result.Where(t => t.TrackingDate.Date >= since);

        return result.ToList();
    }

    /// <summary>
    /// Free-text search over company, role and job id together. Every word has to appear somewhere in
    /// the three, so "Caterpillar AI" finds Caterpillar Inc / Senior AI Software Engineer even though
    /// neither field contains the whole phrase. Case and word order do not matter.
    /// </summary>
    public static List<JobTask> Search(IEnumerable<JobTask> tasks, string? search) {
        var all = (tasks ?? Enumerable.Empty<JobTask>()).ToList();

        var terms = (search ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) return all;

        return all.Where(task => {
            var haystack = Searchable(task);
            return terms.All(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase));
        }).ToList();
    }

    static string Searchable(JobTask task) => $"{task.Company} {task.Title} {task.JobId}";

    /// <summary>Jobs that reached Applied — they all carry an AppliedAt, so activity can be counted.</summary>
    static IEnumerable<JobTask> AppliedJobs(IEnumerable<JobTask> tasks) =>
        (tasks ?? Enumerable.Empty<JobTask>()).Where(t => t.AppliedAt.HasValue);

    /// <summary>
    /// Applied, Interview and Done all mean "an application was sent", counted once each. This uses
    /// the status rather than AppliedAt, so a job marked Applied before tracking stamped timestamps
    /// still counts in the totals.
    /// </summary>
    static int CountAppliedOrLater(IEnumerable<JobTask> tasks) =>
        (tasks ?? Enumerable.Empty<JobTask>())
            .Count(t => ApplicationStatus.Index(t.ApplicationStatus) >= ApplicationStatus.Index(ApplicationStatus.Applied));

    /// <summary>
    /// Only ordinary web links are ever handed to Windows. A job payload is third-party data, so a
    /// "url" of file:///... or a local executable must not become a launch — it is refused instead.
    /// </summary>
    public static bool IsOpenableUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// Opens the job posting in the user's default browser. No browser is embedded and nothing is
    /// fetched here. Returns false when there is no usable link or the shell refused it.
    /// </summary>
    public static bool OpenJobUrl(JobTask? job) {
        if (job is null || !OpenJobUrl(job.Link)) return false;
        PerfLog.Line("TRACKING open URL " + job.JobId);
        return true;
    }

    public static bool OpenJobUrl(string? url) => IsOpenableUrl(url) && Launch(url!.Trim());

    /// <summary>
    /// Opens the generated resume in whatever Windows uses for a DOCX. The path was produced by this
    /// app, but it is still checked to exist first — a document deleted or moved by hand does nothing
    /// rather than throwing.
    /// </summary>
    public static bool OpenResume(JobTask? job) {
        if (job is null) return false;

        if (!job.ResumeGenerated) {
            PerfLog.Line("TRACKING open resume " + job.JobId + " FAILED - no resume path is stored for this job");
            return false;
        }
        if (!File.Exists(job.ResumePath)) {
            PerfLog.Line("TRACKING open resume " + job.JobId + " FAILED - file is missing: " + job.ResumePath);
            return false;
        }

        if (!Launch(job.ResumePath)) return false;
        PerfLog.Line("TRACKING open resume " + job.JobId + " " + job.ResumePath);
        return true;
    }

    /// <summary>
    /// Opens the job's own folder — the dated Company - Role folder the document actually sits in,
    /// never the Resume Root above it.
    /// </summary>
    public static bool OpenResumeFolder(JobTask? job) {
        if (job is null) return false;

        if (!job.ResumeGenerated) {
            PerfLog.Line("TRACKING open resume folder " + job.JobId + " FAILED - no resume path is stored for this job");
            return false;
        }

        var folder = Path.GetDirectoryName(job.ResumePath);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) {
            PerfLog.Line("TRACKING open resume folder " + job.JobId + " FAILED - folder is missing: " + (folder ?? "(none)"));
            return false;
        }

        if (!Launch(folder)) return false;
        PerfLog.Line("TRACKING open resume folder " + job.JobId + " " + folder);
        return true;
    }

    /// <summary>
    /// Reconnects a job to a resume that is already on disk but whose path was never stored — every
    /// document generated before tracking existed is in that state, which is why its Resume actions
    /// did nothing. The folder name comes from <see cref="ResumeOutputManager"/>, so no path rule is
    /// duplicated here, and the newest dated folder wins. Returns the path found, or null.
    /// </summary>
    public static string? FindExistingResume(JobTask job, string? resumeRoot) {
        if (string.IsNullOrWhiteSpace(resumeRoot) || !Directory.Exists(resumeRoot)) return null;

        var folderName = ResumeOutputManager.JobFolderName(job.Company, job.Title);

        try {
            foreach (var dated in Directory.GetDirectories(resumeRoot).OrderByDescending(d => d)) {
                var jobFolder = Path.Combine(dated, folderName);
                if (!Directory.Exists(jobFolder)) continue;

                // Most recently written wins, so "Resume (3).docx" beats "Resume.docx". Sorting by
                // name would not: '.' sorts after ' ', so "Resume.docx" would come first.
                var document = Directory.GetFiles(jobFolder, ResumeOutputManager.BaseName + "*.docx")
                                        .OrderByDescending(File.GetLastWriteTimeUtc)
                                        .ThenByDescending(f => f)
                                        .FirstOrDefault();
                if (document is not null) return document;
            }
        } catch (Exception ex) {
            PerfLog.Line("TRACKING could not search for an existing resume: " + ex.Message);
        }
        return null;
    }

    /// <summary>
    /// One-time repair across a task list: fills in a missing or stale ResumePath from what is on
    /// disk. Returns the number of tasks changed, so the caller can save only when something moved.
    /// </summary>
    public static int RelinkResumes(IEnumerable<JobTask> tasks, string? resumeRoot) {
        var repaired = 0;
        foreach (var job in tasks ?? Enumerable.Empty<JobTask>()) {
            if (job.ResumeGenerated && File.Exists(job.ResumePath)) continue;

            var found = FindExistingResume(job, resumeRoot);
            if (found is null || found == job.ResumePath) continue;

            job.ResumePath = found;
            job.NotifyTrackingChanged();
            PerfLog.Line("TRACKING relinked resume " + job.JobId + " " + found);
            repaired++;
        }
        return repaired;
    }

    static bool Launch(string target) {
        try {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
            return true;
        } catch (Exception ex) {
            PerfLog.Line("TRACKING could not open " + target + ": " + ex.Message);
            return false;
        }
    }

    /// <summary>Persists the tracking data. Tasks live in one file, so this is the existing store.</summary>
    public static void SaveTrackingData(IEnumerable<JobTask> tasks) => Storage.SaveTasks(tasks);
}
