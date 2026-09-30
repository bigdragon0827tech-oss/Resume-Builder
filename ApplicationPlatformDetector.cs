using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResumeBuilder;

/// <summary>
/// The application platform (ATS) a job's <see cref="JobTask.ApplyUrl"/> points at. Always derived
/// from that address by <see cref="ApplicationPlatformDetector"/>, never entered by hand.
/// Unknown = no application address, or a host that is not one of the ATS platforms below.
/// Other remains so an older saved value still loads. Stored by name, so order here does not scramble saved data.
/// </summary>
public enum ApplicationPlatform {
    Unknown,
    Greenhouse,
    Workday,
    Lever,
    LinkedIn,
    Ashby,
    SmartRecruiters,
    ICims,
    Other,
    Jobright,
    Indeed,
    Wellfound,
    Dice,
    Taleo,
    BambooHr,
    Jobvite,
    SuccessFactors,
    AdpRecruiting,
    OracleRecruitingCloud,
    UkgPro,
    JazzHr,
    Recruitee,
    BreezyHr,
    Pinpoint,
    Teamtailor,
    Workable,
    RipplingRecruiting,
    DayforceRecruiting,
    CornerstoneRecruiting,
    Avature,
    Phenom,
    Eightfold,
    Beamery,
    Bullhorn,
    JobAdder,
    ZohoRecruit,
    Cats,
    ApplicantStack,
    ClearCompany,
    PaylocityRecruiting,
    PaycomRecruiting,
    PaycorRecruiting,
    IsolvedTalent,
    Fountain,
    Paradox,
    Comeet,
    Manatal,
    RecruitCrm,
    Recruiterflow,
    JobScore,
    Homerun,
    PersonioRecruiting,
    TeamEngine,
    TrakstarHire,
    Neogov,
    GovernmentJobs,
    SymplrRecruiting
}

public enum PlatformConfidence {
    Unknown,
    Medium,
    High
}

/// <summary>
/// In-memory detection. Only <see cref="ApplicationPlatform"/> is stored.
/// </summary>
public readonly record struct PlatformDetectionResult(
    ApplicationPlatform Platform,
    PlatformConfidence Confidence,
    string Evidence);

/// <summary>A few resource addresses already on a loaded page. Not a page scrape.</summary>
public sealed class PlatformPageSignals {
    public static readonly PlatformPageSignals Empty = new();
    public string[] Scripts { get; set; } = [];
    public string[] Actions { get; set; } = [];
    public string[] Metas { get; set; } = [];
    public string[] Links { get; set; } = [];

    public IEnumerable<string> All() {
        foreach (var value in Scripts) yield return value;
        foreach (var value in Actions) yield return value;
        foreach (var value in Metas) yield return value;
        foreach (var value in Links) yield return value;
    }
}

/// <summary>
/// Maps an application address to its recruiting system. Pure: address in, value out, never throws.
/// A path fingerprint wins over the hostname, so a company career site can still name the ATS.
/// Hosts match exactly or as a subdomain, so a look-alike such as notgreenhouse.io is not Greenhouse.
/// </summary>
public static class ApplicationPlatformDetector {
    /// <summary>One row per signal. The same platform may have a path row and a host row.</summary>
    sealed class AtsRule {
        public ApplicationPlatform Platform { get; init; }
        public string[] Paths { get; init; } = [];
        public string[] Hosts { get; init; } = [];
        public string[] HostContains { get; init; } = [];
        public string[] HostStartsWith { get; init; } = [];
        public string[] WhenHostPaths { get; init; } = [];
        public string[] QueryKeys { get; init; } = [];
        public string[] PageMarks { get; init; } = [];
    }

    static readonly AtsRule[] Rules = {
        new() { Platform = ApplicationPlatform.OracleRecruitingCloud, Paths = ["/hcmUI/CandidateExperience/"], PageMarks = ["hcmUI/CandidateExperience", "oraclecloud.com/hcmUI"] },
        new() { Platform = ApplicationPlatform.Taleo, Paths = ["/careersection/"], PageMarks = ["taleo.net", "/careersection/"] },
        new() { Platform = ApplicationPlatform.Workday, Paths = ["/wday/cxs/"], PageMarks = ["myworkdayjobs.com", "/wday/cxs/"] },
        new() { Platform = ApplicationPlatform.DayforceRecruiting, Paths = ["/CandidatePortal"] },
        new() { Platform = ApplicationPlatform.Greenhouse, Hosts = ["greenhouse.io"], QueryKeys = ["gh_jid"], PageMarks = ["greenhouse.io", "grnhse.com"] },
        new() { Platform = ApplicationPlatform.Workday, Hosts = ["myworkdayjobs.com", "myworkdaysite.com", "myworkday.com", "workday.com"] },
        new() { Platform = ApplicationPlatform.Lever, Hosts = ["lever.co"], PageMarks = ["jobs.lever.co"] },
        new() { Platform = ApplicationPlatform.Ashby, Hosts = ["ashbyhq.com"], QueryKeys = ["ashby_jid"], PageMarks = ["ashbyhq.com"] },
        new() { Platform = ApplicationPlatform.SmartRecruiters, Hosts = ["smartrecruiters.com"], PageMarks = ["smartrecruiters.com"] },
        new() { Platform = ApplicationPlatform.ICims, Hosts = ["icims.com"], PageMarks = ["icims.com"] },
        new() { Platform = ApplicationPlatform.Taleo, Hosts = ["taleo.net"] },
        new() { Platform = ApplicationPlatform.BambooHr, Hosts = ["bamboohr.com"], PageMarks = ["bamboohr.com"] },
        new() { Platform = ApplicationPlatform.Jobvite, Hosts = ["jobvite.com"], PageMarks = ["jobvite.com"] },
        new() { Platform = ApplicationPlatform.SuccessFactors, Hosts = ["successfactors.com", "successfactors.eu", "sapsf.com", "sapsf.eu"], PageMarks = ["successfactors.com", "sapsf.com"] },
        new() { Platform = ApplicationPlatform.AdpRecruiting, Hosts = ["workforcenow.adp.com", "recruiting.adp.com"], PageMarks = ["workforcenow.adp.com", "recruiting.adp.com"] },
        new() { Platform = ApplicationPlatform.OracleRecruitingCloud, Hosts = ["oraclecloud.com"], HostContains = [".fa."], HostStartsWith = ["fa."], WhenHostPaths = ["hcm", "candidateexperience", "recruiting", "/job"] },
        new() { Platform = ApplicationPlatform.UkgPro, Hosts = ["ultipro.com"], HostContains = ["recruiting"], PageMarks = ["recruiting.ultipro.com"] },
        new() { Platform = ApplicationPlatform.UkgPro, Hosts = ["ukg.net"], HostContains = [".rec.", "recruiting"], HostStartsWith = ["rec."] },
        new() { Platform = ApplicationPlatform.UkgPro, Hosts = ["ukg.net"], WhenHostPaths = ["/recruiting"] },
        new() { Platform = ApplicationPlatform.JazzHr, Hosts = ["jazz.co", "applytojob.com"], PageMarks = ["applytojob.com", "jazz.co"] },
        new() { Platform = ApplicationPlatform.Recruitee, Hosts = ["recruitee.com"], PageMarks = ["recruitee.com"] },
        new() { Platform = ApplicationPlatform.BreezyHr, Hosts = ["breezy.hr"], PageMarks = ["breezy.hr"] },
        new() { Platform = ApplicationPlatform.Pinpoint, Hosts = ["pinpoint.com", "pinpointhq.com"], PageMarks = ["pinpointhq.com", "pinpoint.com"] },
        new() { Platform = ApplicationPlatform.Teamtailor, Hosts = ["teamtailor.com"], PageMarks = ["teamtailor.com"] },
        new() { Platform = ApplicationPlatform.Workable, Hosts = ["workable.com"], PageMarks = ["workable.com"] },
        new() { Platform = ApplicationPlatform.RipplingRecruiting, Hosts = ["rippling.com"], WhenHostPaths = ["/jobs", "/recruiting"], PageMarks = ["ats.rippling.com"] },
        new() { Platform = ApplicationPlatform.DayforceRecruiting, Hosts = ["dayforcehcm.com"], PageMarks = ["dayforcehcm.com"] },
        new() { Platform = ApplicationPlatform.CornerstoneRecruiting, Hosts = ["csod.com"], PageMarks = ["csod.com"] },
        new() { Platform = ApplicationPlatform.Avature, Hosts = ["avature.net"], PageMarks = ["avature.net"] },
        new() { Platform = ApplicationPlatform.Phenom, Hosts = ["phenompeople.com", "phenompro.com"], PageMarks = ["phenompeople.com"] },
        new() { Platform = ApplicationPlatform.Eightfold, Hosts = ["eightfold.ai"], PageMarks = ["eightfold.ai"] },
        new() { Platform = ApplicationPlatform.Beamery, Hosts = ["app.beamery.com"] },
        new() { Platform = ApplicationPlatform.Beamery, Hosts = ["beamery.com"], WhenHostPaths = ["/jobs"], PageMarks = ["app.beamery.com"] },
        new() { Platform = ApplicationPlatform.Bullhorn, Hosts = ["bullhornstaffing.com"], PageMarks = ["bullhornstaffing.com"] },
        new() { Platform = ApplicationPlatform.JobAdder, Hosts = ["jobadder.com"], PageMarks = ["jobadder.com"] },
        new() { Platform = ApplicationPlatform.ZohoRecruit, Hosts = ["recruit.zoho.com", "recruit.zoho.eu", "recruiter.zoho.com"], PageMarks = ["recruit.zoho.com"] },
        new() { Platform = ApplicationPlatform.Cats, Hosts = ["catsone.com"], PageMarks = ["catsone.com"] },
        new() { Platform = ApplicationPlatform.ApplicantStack, Hosts = ["applicantstack.com"], PageMarks = ["applicantstack.com"] },
        new() { Platform = ApplicationPlatform.ClearCompany, Hosts = ["clearcompany.com"], PageMarks = ["clearcompany.com"] },
        new() { Platform = ApplicationPlatform.PaylocityRecruiting, Hosts = ["recruiting.paylocity.com"], PageMarks = ["recruiting.paylocity.com"] },
        new() { Platform = ApplicationPlatform.PaylocityRecruiting, Hosts = ["paylocity.com"], WhenHostPaths = ["/Recruiting"] },
        new() { Platform = ApplicationPlatform.PaycomRecruiting, Hosts = ["paycomonline.net", "paycomonline.com"], PageMarks = ["paycomonline.net"] },
        new() { Platform = ApplicationPlatform.PaycorRecruiting, Hosts = ["recruiting.paycor.com"], PageMarks = ["recruiting.paycor.com"] },
        new() { Platform = ApplicationPlatform.PaycorRecruiting, Hosts = ["paycor.com"], WhenHostPaths = ["/recruiting"] },
        new() { Platform = ApplicationPlatform.IsolvedTalent, Hosts = ["isolvedhire.com"], PageMarks = ["isolvedhire.com"] },
        new() { Platform = ApplicationPlatform.Fountain, Hosts = ["web.fountain.com"] },
        new() { Platform = ApplicationPlatform.Fountain, Hosts = ["fountain.com"], WhenHostPaths = ["/apply", "/c/"], PageMarks = ["web.fountain.com"] },
        new() { Platform = ApplicationPlatform.Paradox, Hosts = ["paradox.ai"], PageMarks = ["paradox.ai"] },
        new() { Platform = ApplicationPlatform.Comeet, Hosts = ["comeet.com", "comeet.co"], PageMarks = ["comeet.com", "comeet.co"] },
        new() { Platform = ApplicationPlatform.Manatal, Hosts = ["manatal.com", "careers-page.com"], PageMarks = ["manatal.com"] },
        new() { Platform = ApplicationPlatform.RecruitCrm, Hosts = ["recruitcrm.io"], PageMarks = ["recruitcrm.io"] },
        new() { Platform = ApplicationPlatform.Recruiterflow, Hosts = ["recruiterflow.com"], PageMarks = ["recruiterflow.com"] },
        new() { Platform = ApplicationPlatform.JobScore, Hosts = ["jobscore.com"], PageMarks = ["jobscore.com"] },
        new() { Platform = ApplicationPlatform.Homerun, Hosts = ["homerun.hr"], PageMarks = ["homerun.hr"] },
        new() { Platform = ApplicationPlatform.PersonioRecruiting, Hosts = ["jobs.personio.com", "jobs.personio.de"], PageMarks = ["jobs.personio.com", "jobs.personio.de"] },
        new() { Platform = ApplicationPlatform.PersonioRecruiting, Hosts = ["personio.com", "personio.de"], WhenHostPaths = ["/job"] },
        new() { Platform = ApplicationPlatform.TeamEngine, Hosts = ["teamengine.io"], PageMarks = ["teamengine.io"] },
        new() { Platform = ApplicationPlatform.TrakstarHire, Hosts = ["recruiterbox.com", "hire.trakstar.com"], PageMarks = ["recruiterbox.com", "hire.trakstar.com"] },
        new() { Platform = ApplicationPlatform.Neogov, Hosts = ["neogov.com"], PageMarks = ["neogov.com"] },
        new() { Platform = ApplicationPlatform.GovernmentJobs, Hosts = ["governmentjobs.com", "schooljobs.com"], PageMarks = ["governmentjobs.com"] },
        new() { Platform = ApplicationPlatform.SymplrRecruiting, Hosts = ["careers.symplr.com"] },
        new() { Platform = ApplicationPlatform.SymplrRecruiting, Hosts = ["symplr.com"], WhenHostPaths = ["/careers", "/jobs"], PageMarks = ["careers.symplr.com"] },
    };

    static readonly HashSet<ApplicationPlatform> AtsPlatforms = Rules.Select(rule => rule.Platform).ToHashSet();
    static readonly JsonSerializerOptions SignalJson = new() { PropertyNameCaseInsensitive = true };
    static readonly PlatformDetectionResult None = new(ApplicationPlatform.Unknown, PlatformConfidence.Unknown, "");

    /// <summary>
    /// Reads script, form, stylesheet and meta addresses already in the page. It does not read the job text.
    /// </summary>
    public const string PageFingerprintScript = """
        (() => {
          try {
            const take = (list, n) => Array.prototype.slice.call(list || [], 0, n);
            const scripts = take(document.scripts, 30).map(s => s.src || "").filter(Boolean);
            const actions = take(document.forms, 15).map(f => f.getAttribute("action") || "").filter(Boolean);
            const metas = take(document.querySelectorAll("meta[content]"), 12).map(m => m.getAttribute("content") || "").filter(Boolean);
            const links = take(document.querySelectorAll("link[href]"), 15).map(l => l.getAttribute("href") || "").filter(Boolean);
            return JSON.stringify({ scripts, actions, metas, links });
          } catch (e) {
            return "{\"scripts\":[],\"actions\":[],\"metas\":[],\"links\":[]}";
          }
        })()
        """;

    /// <summary>Where the posting was found. Never written into <see cref="ApplicationPlatform"/>.</summary>
    static readonly (string Domain, string Name)[] JobSites = {
        ("jobright.ai", "Jobright"),
        ("linkedin.com", "LinkedIn"),
        ("indeed.com", "Indeed"),
        ("dice.com", "Dice"),
        ("wellfound.com", "Wellfound"),
        ("angel.co", "Wellfound"),
    };

    /// <summary>Recognises a host for the import filter and other callers. A job board is not an ATS.</summary>
    public static ApplicationPlatform Detect(string? applyUrl) => Classify(applyUrl);

    public static bool IsAts(ApplicationPlatform platform) => AtsPlatforms.Contains(platform);

    public static int SupportedCount => AtsPlatforms.Count;

    /// <summary>Path, then host, then a host that also needs a recruiting path, then an embed query.</summary>
    public static PlatformDetectionResult Inspect(string? url) {
        if (!TryUri(url, out var uri)) return None;
        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath;

        foreach (var rule in Rules)
            foreach (var needle in rule.Paths)
                if (path.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    return Hit(rule.Platform, PlatformConfidence.High, "path:" + needle);

        foreach (var rule in Rules) {
            if (rule.WhenHostPaths.Length > 0 || rule.Hosts.Length == 0) continue;
            if (!OnHost(host, rule)) continue;
            return Hit(rule.Platform, PlatformConfidence.High, "host:" + MatchedHost(host, rule));
        }

        foreach (var rule in Rules) {
            if (rule.WhenHostPaths.Length == 0 || !OnHost(host, rule)) continue;
            foreach (var needle in rule.WhenHostPaths)
                if (path.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    return Hit(rule.Platform, PlatformConfidence.High, "host:" + MatchedHost(host, rule));
        }

        foreach (var rule in Rules)
            foreach (var key in rule.QueryKeys)
                if (HasQueryKey(uri, key))
                    return Hit(rule.Platform, PlatformConfidence.High, "query:" + key);

        return None;
    }

    /// <summary>
    /// Uses resource addresses only when the URL itself is inconclusive.
    /// A URL that already names a platform is returned unchanged.
    /// </summary>
    public static PlatformDetectionResult InspectPage(string? url, PlatformPageSignals? signals) {
        var fromUrl = Inspect(url);
        if (fromUrl.Confidence != PlatformConfidence.Unknown) return fromUrl;
        if (signals is null) return fromUrl;
        foreach (var rule in Rules)
            foreach (var mark in rule.PageMarks) {
                if (mark.Length < 8) continue;
                foreach (var value in signals.All())
                    if (!string.IsNullOrEmpty(value) && value.Contains(mark, StringComparison.OrdinalIgnoreCase))
                        return Hit(rule.Platform, PlatformConfidence.Medium, "page:" + mark);
            }
        return fromUrl;
    }

    public static PlatformPageSignals ParsePageSignals(string? scriptResult) {
        if (string.IsNullOrWhiteSpace(scriptResult)) return PlatformPageSignals.Empty;
        try {
            var text = scriptResult.Trim();
            if (text.StartsWith("\"", StringComparison.Ordinal))
                text = JsonSerializer.Deserialize<string>(text) ?? "";
            return JsonSerializer.Deserialize<PlatformPageSignals>(text, SignalJson) ?? PlatformPageSignals.Empty;
        } catch (JsonException) {
            return PlatformPageSignals.Empty;
        }
    }

    /// <summary>
    /// The ATS stored on a job. The application URL wins. The job URL is used only when it is itself
    /// an ATS address. Jobright, LinkedIn, Indeed and Dice stay empty (Unknown) rather than a platform.
    /// </summary>
    public static ApplicationPlatform Resolve(string? applyUrl, string? jobUrl) =>
        ResolveResult(applyUrl, jobUrl).Platform;

    static PlatformDetectionResult ResolveResult(string? applyUrl, string? jobUrl) {
        var fromApply = Inspect(applyUrl);
        if (IsAts(fromApply.Platform)) return fromApply;
        if (Classify(applyUrl) == ApplicationPlatform.Other)
            return new PlatformDetectionResult(ApplicationPlatform.Other, PlatformConfidence.High, "host:linkedin.com");
        var fromJob = Inspect(jobUrl);
        return IsAts(fromJob.Platform) ? fromJob : None;
    }

    /// <summary>
    /// A stored ATS is kept when the new result is Unknown, Other, or only a page-level guess.
    /// A high-confidence read of the current ApplyUrl may replace a disagreeing stored platform.
    /// </summary>
    public static bool ShouldUpdate(ApplicationPlatform current, PlatformDetectionResult next) {
        if (next.Platform == current) return false;
        if (next.Confidence == PlatformConfidence.Unknown || !IsAts(next.Platform)) return false;
        if (!IsAts(current)) return true;
        return next.Confidence == PlatformConfidence.High;
    }

    public static bool TryAssign(JobTask task, PlatformDetectionResult next) {
        if (task is null || !ShouldUpdate(task.ApplicationPlatform, next)) return false;
        PerfLog.Line(
            $"PLATFORM DETECTED platform={JobTracker.PlatformDisplayName(next.Platform)} confidence={next.Confidence} evidence={next.Evidence}");
        task.ApplicationPlatform = next.Platform;
        return true;
    }

    /// <summary>Job board name from the posting address, then from the import source. Empty when neither matches.</summary>
    public static string JobSiteName(string? jobUrl, string? source) {
        var text = (source ?? "").Trim();
        if (text.Equals("email", StringComparison.OrdinalIgnoreCase)) return "Email";
        if (TryHost(jobUrl, out var host)) {
            foreach (var (domain, name) in JobSites)
                if (HostIs(host, domain)) return name;
        }
        if (text.Length == 0) return "";
        foreach (var (_, name) in JobSites)
            if (text.Contains(name, StringComparison.OrdinalIgnoreCase)) return name;
        return "";
    }

    public static ApplicationPlatform Classify(string? url) {
        var found = Inspect(url);
        if (found.Platform != ApplicationPlatform.Unknown) return found.Platform;
        if (!TryUri(url, out var uri)) return ApplicationPlatform.Unknown;

        var host = uri.Host.ToLowerInvariant();
        foreach (var (domain, name) in JobSites) {
            if (!HostIs(host, domain)) continue;
            if (name == "LinkedIn") break;
            return name switch {
                "Jobright" => ApplicationPlatform.Jobright,
                "Indeed" => ApplicationPlatform.Indeed,
                "Dice" => ApplicationPlatform.Dice,
                "Wellfound" => ApplicationPlatform.Wellfound,
                _ => ApplicationPlatform.Unknown
            };
        }

        // LinkedIn is recognised only for its job postings; a profile or company page is not one.
        if (HostIs(host, "linkedin.com"))
            return uri.AbsolutePath.StartsWith("/jobs/", StringComparison.OrdinalIgnoreCase)
                ? ApplicationPlatform.LinkedIn : ApplicationPlatform.Other;

        return ApplicationPlatform.Unknown;
    }

    /// <summary>
    /// Re-derives every task's platform from its ApplyUrl. A stored ATS is not replaced by Unknown
    /// or by a page-level guess. Returns how many changed, so the caller saves only when something did.
    /// </summary>
    public static int Refresh(IEnumerable<JobTask> tasks) {
        var changed = 0;
        foreach (var task in tasks)
            if (TryAssign(task, ResolveResult(task.ApplyUrl, task.Link))) changed++;
        return changed;
    }

    static PlatformDetectionResult Hit(ApplicationPlatform platform, PlatformConfidence confidence, string evidence) =>
        new(platform, confidence, evidence);

    static bool OnHost(string host, AtsRule rule) {
        if (rule.Hosts.Length == 0 || !rule.Hosts.Any(domain => HostIs(host, domain))) return false;
        if (rule.HostContains.Length == 0 && rule.HostStartsWith.Length == 0) return true;
        if (rule.HostContains.Any(needle => host.Contains(needle, StringComparison.Ordinal))) return true;
        return rule.HostStartsWith.Any(prefix => host.StartsWith(prefix, StringComparison.Ordinal));
    }

    static string MatchedHost(string host, AtsRule rule) {
        foreach (var domain in rule.Hosts)
            if (HostIs(host, domain)) return domain;
        return host;
    }

    static bool TryUri(string? url, out Uri uri) {
        uri = null!;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;
        uri = parsed;
        return true;
    }

    static bool TryHost(string? url, out string host) {
        host = "";
        if (!TryUri(url, out var uri)) return false;
        host = uri.Host.ToLowerInvariant();
        return host.Length > 0;
    }

    static bool HostIs(string host, string domain) =>
        host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);

    static bool HasQueryKey(Uri uri, string key) =>
        uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Any(pair => pair.Split('=', 2)[0].Equals(key, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Reads <see cref="ApplicationPlatform"/> without ever throwing. A missing, null, misspelled or
/// future value becomes Unknown instead of an exception — Storage.LoadTasks turns any exception into
/// an empty task list, which the next save would write over the user's jobs.
/// </summary>
public sealed class TolerantPlatformConverter : JsonConverter<ApplicationPlatform> {
    public override bool HandleNull => true;

    public override ApplicationPlatform Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        switch (reader.TokenType) {
            case JsonTokenType.String:
                var text = reader.GetString();
                return Enum.TryParse<ApplicationPlatform>(text, ignoreCase: true, out var named)
                       && Enum.IsDefined(named) && !int.TryParse(text, out _)
                    ? named : ApplicationPlatform.Unknown;
            case JsonTokenType.Number:
                return reader.TryGetInt32(out var number) && Enum.IsDefined((ApplicationPlatform)number)
                    ? (ApplicationPlatform)number : ApplicationPlatform.Unknown;
            case JsonTokenType.StartObject:
            case JsonTokenType.StartArray:
                reader.Skip();
                return ApplicationPlatform.Unknown;
            default:
                return ApplicationPlatform.Unknown;
        }
    }

    public override void Write(Utf8JsonWriter writer, ApplicationPlatform value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
