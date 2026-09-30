namespace ResumeBuilder;

public enum ApplyCaptureResult {
    /// <summary>The browser was not on a single Jobright job page, so the job is unknown.</summary>
    NotJobPage,
    /// <summary>Not an application address: not http/https, on jobright.ai, or a known non-application link.</summary>
    NotApplicationUrl,
    /// <summary>The job is not in Resume Builder. Nothing is created — importing stays a separate action.</summary>
    UnknownJob,
    /// <summary>The same address was already recorded for this job.</summary>
    Unchanged,
    /// <summary>The address was written to the job. The caller saves.</summary>
    Recorded
}

/// <summary>
/// Records the real application address when the USER clicks Apply in the job browser.
///
/// Jobright's page data carries no application address; it becomes known only when the user's own
/// click opens or navigates to the application site. The job browser reports that destination and
/// the job page it came from; this class decides whether it is an application address and which
/// task it belongs to. It never clicks, never reads the page, never creates a task and never saves —
/// MainWindow saves through Storage when the result is <see cref="ApplyCaptureResult.Recorded"/>.
/// </summary>
public static class ApplyCapture {
    /// <summary>
    /// Outbound links a Jobright job page shows that are NOT applications (verified on a live page:
    /// people's and the company's LinkedIn pages, X/Twitter, Crunchbase, Glassdoor). Matched as
    /// host suffix + path prefix. LinkedIn job postings (/jobs/) are deliberately not listed.
    /// </summary>
    static readonly (string Host, string PathPrefix)[] NotApplicationLinks = {
        ("linkedin.com", "/in/"),
        ("linkedin.com", "/company/"),
        ("linkedin.com", "/school/"),
        ("x.com", "/"),
        ("twitter.com", "/"),
        ("crunchbase.com", "/"),
        ("glassdoor.com", "/"),
        ("facebook.com", "/"),
        ("instagram.com", "/"),
        ("youtube.com", "/"),
    };

    /// <summary>An absolute http/https address outside jobright.ai that is not a known non-application link.</summary>
    public static bool IsApplicationUrl(string? url) {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        var host = uri.Host.ToLowerInvariant();
        if (HostIs(host, "jobright.ai")) return false;

        foreach (var (linkHost, prefix) in NotApplicationLinks)
            if (HostIs(host, linkHost) && uri.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;

        return true;
    }

    static bool HostIs(string host, string domain) => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);

    /// <summary>
    /// Import-time discovery: gives a task an application address only when it has none. Same rules as
    /// a click capture (<see cref="IsApplicationUrl"/>, stored normalized, platform detected), but it
    /// never replaces an address already recorded. Returns true when it wrote something; the caller saves.
    /// </summary>
    public static bool FillIfEmpty(JobTask task, string? applyUrl, DateTime now) {
        if (!string.IsNullOrWhiteSpace(task.ApplyUrl) || !IsApplicationUrl(applyUrl)) return false;

        var url = JobUrls.Normalize(applyUrl) ?? applyUrl!.Trim();
        task.ApplyUrl = url;
        task.ApplyUrlCapturedAt = now;
        task.ApplicationPlatform = ApplicationPlatformDetector.Resolve(url, null);
        return true;
    }

    /// <summary>The task whose job link is the same Jobright job (by Jobright job id), or null.</summary>
    public static JobTask? FindTask(IEnumerable<JobTask> tasks, string jobrightJobId) {
        var task = tasks.FirstOrDefault(t => JobrightPageExtractor.JobIdFromUrl(t.Link) == jobrightJobId);
        if (task is not null)
            PerfLog.Line("IDENTITY lookup source=jobright id=" + jobrightJobId);
        return task;
    }

    /// <summary>
    /// Applies the rules and, when they all pass, writes ApplyUrl and ApplyUrlCapturedAt on the matching
    /// task. The address is stored in the form <see cref="JobUrls.Normalize"/> gives it (tracking
    /// parameters dropped). Nothing is written for any other result.
    /// </summary>
    public static ApplyCaptureResult Record(IEnumerable<JobTask> tasks, string? jobPageUrl, string? applyUrl, DateTime now) {
        var jobId = JobrightPageExtractor.JobIdFromUrl(jobPageUrl);
        if (jobId is null) return ApplyCaptureResult.NotJobPage;
        if (!IsApplicationUrl(applyUrl)) return ApplyCaptureResult.NotApplicationUrl;

        var task = FindTask(tasks, jobId);
        if (task is null) return ApplyCaptureResult.UnknownJob;

        var url = JobUrls.Normalize(applyUrl) ?? applyUrl!.Trim();
        if (string.Equals(task.ApplyUrl, url, StringComparison.Ordinal)) return ApplyCaptureResult.Unchanged;

        task.ApplyUrl = url;
        task.ApplyUrlCapturedAt = now;
        task.ApplicationPlatform = ApplicationPlatformDetector.Resolve(url, null);
        return ApplyCaptureResult.Recorded;
    }
}
