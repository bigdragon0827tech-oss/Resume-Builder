using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace ResumeBuilder;

/// <summary>What the Applications Apply button does for one job.</summary>
public enum ApplyRoute {
    /// <summary>Not Ready, or Ready with no application address and no Jobright posting to capture from.</summary>
    Unavailable,
    /// <summary>A verified external ApplyUrl is already stored. Open that address.</summary>
    OpenStored,
    /// <summary>Open the Jobright posting in the Job Browser and record the external destination.</summary>
    CaptureOnJobright
}

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
    public const string Failed = "Failed";
    public const string Done = "Done";

    /// <summary>The filter's "no filter" entry. Never stored on a task.</summary>
    public const string All = "All";

    /// <summary>The linear flow. Failed is a branch, not a later stage, so it is not in this list.</summary>
    public static readonly string[] Ordered = { Viewed, Ready, Applied, Interview, Done };

    /// <summary>Every stored status, including the Failed branch. Used for recognition, not for backfill.</summary>
    public static readonly string[] Known = { Viewed, Ready, Applied, Interview, Failed, Done };

    public static readonly string[] Filters = { All, Viewed, Ready, Applied, Interview, Failed, Done };

    /// <summary>
    /// Values the STATUS FILTER may take that are not statuses. They are never stored on a task and are
    /// never produced by <see cref="Normalize"/>; <see cref="Ordered"/>, the pipeline, the board columns
    /// and the statistics keep exactly the five real statuses.
    /// </summary>
    public static class Filter {
        /// <summary>A group, not a status: Viewed or Ready — the stages before an application exists.</summary>
        public const string NotAppliedYet = "Not applied yet";

        /// <summary>The statuses the group covers.</summary>
        public static readonly string[] NotAppliedYetStatuses = { Viewed, Ready };

        /// <summary>The status dropdown's choices: All, the group, then the five real statuses.</summary>
        public static readonly string[] Options = { All, NotAppliedYet, Viewed, Ready, Applied, Interview, Failed, Done };

        public static bool IsGroup(string? value) =>
            NotAppliedYet.Equals((value ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

        /// <summary>True when a job's stored status falls inside the group.</summary>
        public static bool Covers(string? group, string? applicationStatus) =>
            IsGroup(group) && NotAppliedYetStatuses.Contains(Normalize(applicationStatus));
    }

    public static bool IsKnown(string? value) =>
        value is not null && Known.Any(s => s.Equals(value, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Anything unrecognised — missing, empty, or written by an older version — is Viewed. That is
    /// what keeps a tasks.json from before tracking existed loading without a migration step.
    /// Failed is recognised, so a saved Failed job is not read back as Viewed.
    /// </summary>
    public static string Normalize(string? value) =>
        Known.FirstOrDefault(s => s.Equals((value ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) ?? Viewed;

    /// <summary>
    /// 0-based position in the linear flow. Failed is not on that line, so it returns -1 and must
    /// not be used to backfill later stages.
    /// </summary>
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

/// <summary>The three labelled arrows on the Applications pipeline.</summary>
public sealed class ApplicationTransitions {
    public int AppliedToInterview { get; init; }
    public int AppliedToFailed { get; init; }
    public int InterviewToFailed { get; init; }
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

/// <summary>
/// Whether a job is ready to be applied for. Derived from ResumeGenerated and ApplyUrl only — display
/// only, never stored, and independent of both the queue status and the application status.
/// </summary>
public enum ApplicationReadiness { NeedsResume, NeedsApplyLink, ReadyToApply }

public static class JobTracker {
    /// <summary>
    /// No resume -> NeedsResume; resume but no usable (http/https) ApplyUrl -> NeedsApplyLink;
    /// both -> ReadyToApply. "Usable" is an http/https ApplyUrl. The Apply button also requires
    /// that this is a real application address and the job is still Viewed or Ready.
    /// </summary>
    public static ApplicationReadiness GetReadiness(JobTask job) =>
        !job.ResumeGenerated ? ApplicationReadiness.NeedsResume
        : IsOpenableUrl(job.ApplyUrl) ? ApplicationReadiness.ReadyToApply
        : ApplicationReadiness.NeedsApplyLink;

    /// <summary>
    /// The "Ready to apply" card: every job whose readiness is ReadyToApply, computed now — exactly the
    /// set the Readiness filter shows for that one state, so the card and the list always agree.
    /// </summary>
    public static int CountReadyToApply(IEnumerable<JobTask> tasks) =>
        (tasks ?? Enumerable.Empty<JobTask>()).Count(t => GetReadiness(t) == ApplicationReadiness.ReadyToApply);

    /// <summary>
    /// The action queue: ready to apply AND not applied for yet (Viewed or Ready). Readiness itself is
    /// unchanged — an applied job stays ReadyToApply, it simply has nothing left to do here.
    /// </summary>
    public static bool NeedsAction(JobTask job) =>
        GetReadiness(job) == ApplicationReadiness.ReadyToApply &&
        ApplicationStatus.Filter.Covers(ApplicationStatus.Filter.NotAppliedYet, job.ApplicationStatus);

    /// <summary>The "Ready to apply" card's number: exactly what the card's own filters show.</summary>
    public static int CountNeedsAction(IEnumerable<JobTask> tasks) =>
        (tasks ?? Enumerable.Empty<JobTask>()).Count(NeedsAction);

    /// <summary>Wording avoids "Ready" alone, which is already an application status.</summary>
    public static string ReadinessText(ApplicationReadiness readiness) => readiness switch {
        ApplicationReadiness.ReadyToApply => "Ready to apply",
        ApplicationReadiness.NeedsApplyLink => "Needs apply link",
        _ => "Needs resume"
    };

    public static string ReadinessHint(ApplicationReadiness readiness) => readiness switch {
        ApplicationReadiness.ReadyToApply => "A resume and an application link are both recorded.",
        ApplicationReadiness.NeedsApplyLink => "Click Apply on this job in the Job Browser to record its application link.",
        _ => "Generate this job's resume first."
    };

    /// <summary>
    /// Sets the application status by hand. Returns false when nothing changed, so a caller can skip
    /// a pointless save. Entering a stage stamps its time if it has none; a stamp already set is never
    /// rewritten or cleared, so moving a task back and forward keeps the real history.
    /// </summary>
    public static bool UpdateStatus(JobTask job, string status, DateTime? at = null) {
        // A filter-only value is not a status: refuse it rather than let Normalize store it as Viewed.
        if (ApplicationStatus.Filter.IsGroup(status)) return false;

        var target = ApplicationStatus.Normalize(status);
        var previous = job.ApplicationStatus;
        if (previous == target) return false;

        var now = at ?? DateTime.Now;
        job.ApplicationStatus = target;
        job.UpdatedAt = now;
        if (target == ApplicationStatus.Failed) {
            job.FailedAt ??= now;
            // Only a move from Applied or Interview is a real branch. Anything else is a status
            // correction and must not invent an Applied → Failed or Interview → Failed count.
            if (string.IsNullOrEmpty(job.FailedFrom) &&
                (previous == ApplicationStatus.Applied || previous == ApplicationStatus.Interview))
                job.FailedFrom = previous;
        } else {
            StampStage(job, target, now);
        }

        PerfLog.Line("APPLICATION STATUS id=" + job.JobId + " old=" + previous + " new=" + target);
        job.NotifyTrackingChanged();
        return true;
    }

    /// <summary>
    /// Mark Applied is offered only before Applied (Viewed or Ready), so a one-click action can never
    /// move an Interview or Done job backwards. The status dropdown remains for deliberate corrections.
    /// </summary>
    public static bool CanMarkApplied(string? applicationStatus) =>
        ApplicationStatus.Index(ApplicationStatus.Normalize(applicationStatus)) < ApplicationStatus.Index(ApplicationStatus.Applied);

    public static bool CanMarkApplied(JobTask job) => CanMarkApplied(job.ApplicationStatus);

    /// <summary>
    /// Apply is enabled when a real application address is stored, or otherwise when the job
    /// posting itself can be opened so that address can be captured. Application status is not consulted.
    /// </summary>
    public static bool CanApply(JobTask? job) =>
        job is not null && (ApplyCapture.IsApplicationUrl(job.ApplyUrl) || IsOpenableUrl(job.Link));

    /// <summary>
    /// Apply with a real external application address opens that address. A Jobright job
    /// that does not have one yet is sent through the Job Browser capture. The posting URL is
    /// never used as the application address.
    /// </summary>
    public static ApplyRoute RouteApply(JobTask? job) {
        if (!CanApply(job)) return ApplyRoute.Unavailable;
        if (ApplyCapture.IsApplicationUrl(job!.ApplyUrl)) return ApplyRoute.OpenStored;
        if (JobrightPageExtractor.IsJobPage(job.Link)) return ApplyRoute.CaptureOnJobright;
        return ApplyRoute.Unavailable;
    }

    /// <summary>
    /// The external application address Apply opens directly. Null when there is no verified
    /// ApplyUrl — the Jobright posting is not a substitute.
    /// </summary>
    public static string? ApplyTarget(JobTask? job) =>
        RouteApply(job) == ApplyRoute.OpenStored ? job!.ApplyUrl.Trim() : null;

    /// <summary>
    /// The user's own "I applied" — never inferred. Goes through <see cref="UpdateStatus"/>, so AppliedAt
    /// is stamped only if it has none (never rewritten), a skipped ReadyAt is backfilled, and the queue
    /// Status is not touched. Returns false (no change) from Applied, Interview or Done.
    /// </summary>
    public static bool MarkApplied(JobTask job, DateTime? at = null) =>
        CanMarkApplied(job) && UpdateStatus(job, ApplicationStatus.Applied, at);

    /// <summary>
    /// Every stage date recorded for a job, one per line ("Applied Sep 18, 2026, 3:40 PM"), for the
    /// List's Date tooltip. A task saved before tracking existed falls back to when it was added.
    /// </summary>
    public static string StageDatesText(JobTask job) {
        static string When(DateTime at) => at.ToString("MMM d, yyyy, h:mm tt", CultureInfo.InvariantCulture);
        var lines = new List<string>();
        void Add(string stage, DateTime? at) { if (at is DateTime t) lines.Add($"{stage} {When(t)}"); }
        Add(ApplicationStatus.Viewed, job.ViewedAt);
        Add(ApplicationStatus.Ready, job.ReadyAt);
        Add(ApplicationStatus.Applied, job.AppliedAt);
        Add(ApplicationStatus.Interview, job.InterviewAt);
        Add(ApplicationStatus.Failed, job.FailedAt);
        Add(ApplicationStatus.Done, job.DoneAt);
        if (lines.Count == 0 && job.CreatedAt != default) lines.Add($"Added {When(job.CreatedAt)}");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>The Board card's date: "Applied Sep 18" once AppliedAt exists, else the tracking date.</summary>
    public static string BoardDateText(JobTask job) =>
        job.AppliedAt is DateTime applied
            ? "Applied " + applied.ToString("MMM d", CultureInfo.InvariantCulture)
            : job.TrackingDateDisplay;

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
            PerfLog.Line("APPLICATION STATUS id=" + job.JobId + " old=" + ApplicationStatus.Viewed + " new=" + ApplicationStatus.Ready);
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

        // The filter-only group is answered before Normalize, which would otherwise read an unknown
        // value as Viewed and silently filter to the wrong thing.
        if (ApplicationStatus.Filter.IsGroup(status))
            return all.Where(t => ApplicationStatus.Filter.Covers(status, t.ApplicationStatus)).ToList();

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
    /// The three pipeline arrows. Applied → Interview is every job that already has an Interview
    /// date. The Failed arrows count only a recorded move from that exact status.
    /// </summary>
    public static ApplicationTransitions GetTransitions(IEnumerable<JobTask> tasks) {
        var all = (tasks ?? Enumerable.Empty<JobTask>()).ToList();
        return new ApplicationTransitions {
            AppliedToInterview = all.Count(t => t.InterviewAt.HasValue),
            AppliedToFailed = all.Count(t => t.FailedFrom == ApplicationStatus.Applied),
            InterviewToFailed = all.Count(t => t.FailedFrom == ApplicationStatus.Interview)
        };
    }

    public static int CountStatus(IEnumerable<JobTask> tasks, string status) {
        var wanted = ApplicationStatus.Normalize(status);
        return (tasks ?? Enumerable.Empty<JobTask>()).Count(t => ApplicationStatus.Normalize(t.ApplicationStatus) == wanted);
    }

    /// <summary>
    /// The list and board share one filter path: the status filter, free text, platforms, readiness,
    /// then either one exact date or a quick range over each job's tracking date. Order is never
    /// changed. <paramref name="platforms"/> and <paramref name="readiness"/> are optional: null or
    /// empty means no restriction.
    /// </summary>
    public static List<JobTask> ApplyFilters(IEnumerable<JobTask> tasks, string? search, string? status,
                                             string? dateFilter, DateTime? now = null, DateTime? exactDate = null,
                                             IReadOnlyCollection<ApplicationPlatform>? platforms = null,
                                             IReadOnlyCollection<ApplicationReadiness>? readiness = null) {
        IEnumerable<JobTask> result =
            FilterByReadiness(FilterByPlatforms(Search(GetTasksByStatus(tasks, status), search), platforms), readiness);

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

    // ---------- platform filter ----------

    /// <summary>
    /// ATS platforms that appear on at least one job, in <see cref="PlatformFilterOrder"/>.
    /// Job boards, Unknown and Other are omitted. The same platform is listed once.
    /// </summary>
    public static List<ApplicationPlatform> PlatformsPresent(IEnumerable<JobTask> tasks) {
        var present = new HashSet<ApplicationPlatform>(
            (tasks ?? Enumerable.Empty<JobTask>())
                .Select(task => task.ApplicationPlatform)
                .Where(ApplicationPlatformDetector.IsAts));
        return PlatformFilterOrder.Where(present.Contains).ToList();
    }

    /// <summary>
    /// The order the platform filter lists its choices in — explicit, never the enum's declaration
    /// order, so reordering the enum cannot reshuffle the UI or the label.
    /// </summary>

    public static readonly IReadOnlyList<ApplicationPlatform> PlatformFilterOrder = new[] {
        ApplicationPlatform.Greenhouse, ApplicationPlatform.Workday, ApplicationPlatform.Lever,
        ApplicationPlatform.Ashby, ApplicationPlatform.SmartRecruiters, ApplicationPlatform.ICims,
        ApplicationPlatform.Taleo, ApplicationPlatform.BambooHr, ApplicationPlatform.Jobvite,
        ApplicationPlatform.SuccessFactors, ApplicationPlatform.AdpRecruiting,
        ApplicationPlatform.OracleRecruitingCloud, ApplicationPlatform.UkgPro, ApplicationPlatform.JazzHr,
        ApplicationPlatform.Recruitee, ApplicationPlatform.BreezyHr, ApplicationPlatform.Pinpoint,
        ApplicationPlatform.Teamtailor, ApplicationPlatform.Workable, ApplicationPlatform.RipplingRecruiting,
        ApplicationPlatform.DayforceRecruiting, ApplicationPlatform.CornerstoneRecruiting,
        ApplicationPlatform.Avature, ApplicationPlatform.Phenom, ApplicationPlatform.Eightfold,
        ApplicationPlatform.Beamery, ApplicationPlatform.Bullhorn, ApplicationPlatform.JobAdder,
        ApplicationPlatform.ZohoRecruit, ApplicationPlatform.Cats, ApplicationPlatform.ApplicantStack,
        ApplicationPlatform.ClearCompany, ApplicationPlatform.PaylocityRecruiting,
        ApplicationPlatform.PaycomRecruiting, ApplicationPlatform.PaycorRecruiting,
        ApplicationPlatform.IsolvedTalent, ApplicationPlatform.Fountain, ApplicationPlatform.Paradox,
        ApplicationPlatform.Comeet, ApplicationPlatform.Manatal, ApplicationPlatform.RecruitCrm,
        ApplicationPlatform.Recruiterflow, ApplicationPlatform.JobScore, ApplicationPlatform.Homerun,
        ApplicationPlatform.PersonioRecruiting, ApplicationPlatform.TeamEngine,
        ApplicationPlatform.TrakstarHire, ApplicationPlatform.Neogov, ApplicationPlatform.GovernmentJobs,
        ApplicationPlatform.SymplrRecruiting
    };

    public static string PlatformDisplayName(ApplicationPlatform platform) => platform switch {
        ApplicationPlatform.ICims => "iCIMS",
        ApplicationPlatform.BambooHr => "BambooHR",
        ApplicationPlatform.AdpRecruiting => "ADP Recruiting",
        ApplicationPlatform.OracleRecruitingCloud => "Oracle Recruiting Cloud",
        ApplicationPlatform.UkgPro => "UKG Pro Recruiting",
        ApplicationPlatform.JazzHr => "JazzHR",
        ApplicationPlatform.BreezyHr => "Breezy HR",
        ApplicationPlatform.RipplingRecruiting => "Rippling Recruiting",
        ApplicationPlatform.SuccessFactors => "SAP SuccessFactors",
        ApplicationPlatform.DayforceRecruiting => "Dayforce Recruiting",
        ApplicationPlatform.CornerstoneRecruiting => "Cornerstone Recruiting",
        ApplicationPlatform.Eightfold => "Eightfold AI",
        ApplicationPlatform.ZohoRecruit => "Zoho Recruit",
        ApplicationPlatform.Cats => "CATS",
        ApplicationPlatform.PaylocityRecruiting => "Paylocity Recruiting",
        ApplicationPlatform.PaycomRecruiting => "Paycom Recruiting",
        ApplicationPlatform.PaycorRecruiting => "Paycor Recruiting",
        ApplicationPlatform.IsolvedTalent => "isolved Talent Acquisition",
        ApplicationPlatform.RecruitCrm => "Recruit CRM",
        ApplicationPlatform.PersonioRecruiting => "Personio Recruiting",
        ApplicationPlatform.TeamEngine => "Team Engine",
        ApplicationPlatform.TrakstarHire => "Trakstar Hire",
        ApplicationPlatform.Neogov => "NEOGOV",
        ApplicationPlatform.SymplrRecruiting => "Symplr Recruiting",
        _ => platform.ToString()
    };

    /// <summary>OR matching: a job passes when its platform is any selected one. Null or empty selects all.</summary>
    public static List<JobTask> FilterByPlatforms(IEnumerable<JobTask> tasks, IReadOnlyCollection<ApplicationPlatform>? platforms) {
        var all = (tasks ?? Enumerable.Empty<JobTask>()).ToList();
        if (platforms is null || platforms.Count == 0) return all;
        return all.Where(task => platforms.Contains(task.ApplicationPlatform)).ToList();
    }

    /// <summary>
    /// "All platforms" for none or every choice; one or two names in <see cref="PlatformFilterOrder"/>;
    /// "N platforms" for three or more.
    /// </summary>
    public static string PlatformFilterLabel(IReadOnlyCollection<ApplicationPlatform>? selected) =>
        MultiSelectLabel(selected, PlatformFilterOrder, PlatformDisplayName, "All platforms", "platforms");

    /// <summary>
    /// The label of a multi-select filter button: <paramref name="allText"/> when nothing or every
    /// choice is selected (both mean "no restriction"); one or two names in <paramref name="order"/>;
    /// otherwise "N <paramref name="noun"/>". Shared by the platform and readiness filters.
    /// </summary>
    public static string MultiSelectLabel<T>(IReadOnlyCollection<T>? selected, IReadOnlyList<T> order,
                                             Func<T, string> displayName, string allText, string noun) {
        var chosen = order.Where(choice => selected?.Contains(choice) == true).ToList();
        if (chosen.Count == 0 || chosen.Count == order.Count) return allText;
        return chosen.Count <= 2
            ? string.Join(", ", chosen.Select(displayName))
            : $"{chosen.Count} {noun}";
    }

    // ---------- readiness filter ----------

    /// <summary>The readiness filter's choices, in an explicit order independent of the enum.</summary>
    public static readonly IReadOnlyList<ApplicationReadiness> ReadinessFilterOrder = new[] {
        ApplicationReadiness.ReadyToApply, ApplicationReadiness.NeedsApplyLink, ApplicationReadiness.NeedsResume
    };

    /// <summary>
    /// OR matching on <see cref="GetReadiness"/>, evaluated now — readiness is never stored. Null or
    /// empty selects all.
    /// </summary>
    public static List<JobTask> FilterByReadiness(IEnumerable<JobTask> tasks, IReadOnlyCollection<ApplicationReadiness>? readiness) {
        var all = (tasks ?? Enumerable.Empty<JobTask>()).ToList();
        if (readiness is null || readiness.Count == 0) return all;
        return all.Where(task => readiness.Contains(GetReadiness(task))).ToList();
    }

    public static string ReadinessFilterLabel(IReadOnlyCollection<ApplicationReadiness>? selected) =>
        MultiSelectLabel(selected, ReadinessFilterOrder, ReadinessText, "All readiness", "states");

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
            .Count(t => {
                var status = ApplicationStatus.Normalize(t.ApplicationStatus);
                return ApplicationStatus.Index(status) >= ApplicationStatus.Index(ApplicationStatus.Applied)
                    || (status == ApplicationStatus.Failed && t.AppliedAt.HasValue);
            });

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
    /// The application address to open for a job: its ApplyUrl when that is an http/https address,
    /// otherwise null. Never falls back to <see cref="JobTask.Link"/> — the Jobright posting is Open Job.
    /// </summary>
    public static string? ApplyUrlToOpen(JobTask? job) =>
        job is not null && IsOpenableUrl(job.ApplyUrl) ? job.ApplyUrl.Trim() : null;

    /// <summary>
    /// Opens the recorded application page (ApplyUrl) in the default browser, through the same
    /// validation and launch as Open Job. Logs the job id only, never the address.
    /// </summary>
    public static bool OpenApplyUrl(JobTask? job) {
        if (ApplyUrlToOpen(job) is not string url || !Launch(url)) return false;
        PerfLog.Line("TRACKING open apply URL " + job!.JobId);
        return true;
    }

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
    /// Reconnects a job to a resume already on disk. The link is the internal job id recorded in
    /// resume-info*.json, never the company, the title, or the folder name. The newest dated folder
    /// that names this job wins, and inside it the newest DOCX. Returns null when the id is missing
    /// or no metadata names it.
    /// </summary>
    public static string? FindExistingResume(JobTask job, string? resumeRoot) {
        var id = (job.JobId ?? "").Trim();
        if (id.Length == 0 || string.IsNullOrWhiteSpace(resumeRoot) || !Directory.Exists(resumeRoot)) return null;

        PerfLog.Line("IDENTITY lookup source=internal id=" + id);
        var unsafeName = ResumeOutputManager.JobFolderName(job.Company ?? "", job.Title ?? "");
        var loggedUnsafe = false;

        try {
            var datesStarted = Stopwatch.GetTimestamp();
            var datedFolders = Directory.GetDirectories(resumeRoot);
            _relink?.AddScan(datesStarted);
            _relink?.AddDir();
            foreach (var dated in datedFolders.OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)) {
                string? best = null;
                var bestTime = DateTime.MinValue;
                var jobsStarted = Stopwatch.GetTimestamp();
                var jobFolders = Directory.GetDirectories(dated);
                _relink?.AddScan(jobsStarted);
                _relink?.AddDir();
                foreach (var jobFolder in jobFolders) {
                    var docs = DocxForJob(jobFolder, id);
                    if (docs.Count == 0) {
                        if (!loggedUnsafe
                            && ((job.Company ?? "").Trim().Length > 0 || (job.Title ?? "").Trim().Length > 0)
                            && string.Equals(Path.GetFileName(jobFolder), unsafeName, StringComparison.OrdinalIgnoreCase)) {
                            PerfLog.Line("IDENTITY unsafe-match-removed context=resume-relink");
                            loggedUnsafe = true;
                        }
                        continue;
                    }

                    foreach (var doc in docs) {
                        DateTime written;
                        try { written = File.GetLastWriteTimeUtc(doc); }
                        catch { continue; }
                        if (best is null || written > bestTime
                            || (written == bestTime && string.Compare(doc, best, StringComparison.OrdinalIgnoreCase) > 0)) {
                            best = doc;
                            bestTime = written;
                        }
                    }
                }
                if (best is not null) return best;
            }
        } catch (Exception ex) {
            PerfLog.Line("TRACKING could not search for an existing resume: " + ex.Message);
        }
        return null;
    }

    /// <summary>
    /// DOCX files that resume-info*.json attributes to this internal job id. A folder whose metadata
    /// all names this job contributes every DOCX in it (revisions). A folder shared with another job
    /// contributes only the file that this job's metadata names.
    /// </summary>
    static List<string> DocxForJob(string jobFolder, string jobId) {
        var infos = new List<(string Id, string? Docx)>();
        string[] files;
        try {
            var listed = Stopwatch.GetTimestamp();
            files = Directory.GetFiles(jobFolder, ResumeOutputManager.MetadataBaseName + "*.json");
            _relink?.AddScan(listed);
        }
        catch { return new List<string>(); }

        foreach (var info in files) {
            try {
                var read = Stopwatch.GetTimestamp();
                using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(info));
                _relink?.AddJson(read);
                var recorded = json.RootElement.TryGetProperty("jobId", out var idEl)
                               && idEl.ValueKind == System.Text.Json.JsonValueKind.String
                    ? (idEl.GetString() ?? "").Trim() : "";
                string? docx = null;
                if (json.RootElement.TryGetProperty("docxFile", out var name)
                    && name.ValueKind == System.Text.Json.JsonValueKind.String)
                    docx = Path.GetFileName(name.GetString());
                if (recorded.Length > 0) infos.Add((recorded, docx));
            } catch {
                // An unreadable metadata file just contributes nothing.
            }
        }

        var mine = infos.Where(i => string.Equals(i.Id, jobId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (mine.Count == 0) return new List<string>();

        var mixed = infos.Any(i => !string.Equals(i.Id, jobId, StringComparison.OrdinalIgnoreCase));
        if (mixed) {
            var named = new List<string>();
            foreach (var item in mine) {
                if (string.IsNullOrEmpty(item.Docx)) continue;
                var path = Path.Combine(jobFolder, item.Docx);
                if (File.Exists(path)) named.Add(path);
            }
            return named;
        }

        try {
            return Directory.GetFiles(jobFolder, "*.docx")
                .Where(f => !Path.GetFileName(f).StartsWith("~$", StringComparison.Ordinal))
                .ToList();
        } catch { return new List<string>(); }
    }

    /// <summary>
    /// One-time repair across a task list: fills in a missing or stale ResumePath from what is on
    /// disk. Returns the number of tasks changed, so the caller can save only when something moved.
    /// Applications navigation does not call this. After the SQLite migration, resume links are read
    /// from <see cref="JobStore"/> instead of walking the resume folders once per job.
    /// </summary>
    public static int RelinkResumes(IEnumerable<JobTask> tasks, string? resumeRoot) {
        var repaired = 0;
        var clock = Stopwatch.StartNew();
        var previous = _relink;
        _relink = new RelinkClock();
        try {
            foreach (var job in tasks ?? Enumerable.Empty<JobTask>()) {
                var existsStarted = Stopwatch.GetTimestamp();
                var present = job.ResumeGenerated && File.Exists(job.ResumePath);
                _relink.AddExists(existsStarted);
                if (present) continue;

                _relink.Finds++;
                var found = FindExistingResume(job, resumeRoot);
                if (found is null) { _relink.Misses++; continue; }
                if (found == job.ResumePath) continue;

                job.ResumePath = found;
                job.NotifyTrackingChanged();
                PerfLog.Line("TRACKING relinked resume " + job.JobId + " " + found);
                repaired++;
            }
        } finally {
            var done = _relink;
            _relink = previous;
            PerfLog.Line(
                "APPLICATIONS PERF relink ms=" + clock.ElapsedMilliseconds +
                " exists=" + done.ExistsChecks + "/" + RelinkClock.Ms(done.ExistsTicks) + "ms" +
                " scans=" + done.Finds + " misses=" + done.Misses +
                " dirs=" + done.Dirs + "/" + RelinkClock.Ms(done.ScanTicks) + "ms" +
                " resume-info=" + done.JsonFiles + "/" + RelinkClock.Ms(done.JsonTicks) + "ms");
        }
        return repaired;
    }

    sealed class RelinkClock {
        public int ExistsChecks, Finds, Misses, Dirs, JsonFiles;
        public long ExistsTicks, ScanTicks, JsonTicks;
        public void AddDir() => Dirs++;
        public void AddExists(long started) { ExistsChecks++; ExistsTicks += Stopwatch.GetTimestamp() - started; }
        public void AddScan(long started) { ScanTicks += Stopwatch.GetTimestamp() - started; }
        public void AddJson(long started) { JsonFiles++; JsonTicks += Stopwatch.GetTimestamp() - started; }
        public static long Ms(long ticks) => ticks * 1000 / Stopwatch.Frequency;
    }

    [ThreadStatic] static RelinkClock? _relink;

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
