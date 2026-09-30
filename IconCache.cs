using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ResumeBuilder;

/// <summary>
/// Real website icons on disk under %LOCALAPPDATA%\ResumeBuilder\IconCache.
/// A missing or unreadable file means the generic fallback. Nothing here draws letters or invents a logo.
/// </summary>
public static class IconCache {
    public const int MaxImageBytes = 512 * 1024;
    const int MaxPageBytes = 256 * 1024;

    static readonly string[] Extensions = { ".png", ".ico", ".jpg", ".jpeg", ".gif", ".webp" };

    static readonly string DefaultRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ResumeBuilder", "IconCache");

    /// <summary>Tests point this at a temporary folder. The app leaves it unset.</summary>
    internal static string? RootOverride { get; set; }

    public static string Root => RootOverride ?? DefaultRoot;

    /// <summary>How many platform icons this process has actually requested. A cached file does not count.</summary>
    internal static int PlatformDownloadCount { get; private set; }

    /// <summary>When set, platform icons are fetched from this address instead of the official host.</summary>
    internal static Func<ApplicationPlatform, string?>? SiteForTest { get; set; }

    internal static void ResetPlatformDownloads() => PlatformDownloadCount = 0;

    internal static Task FetchPlatformForTest(JobTask task) => FetchPlatform(task);

    public static string CompaniesFolder => Path.Combine(Root, "Companies");
    public static string PlatformsFolder => Path.Combine(Root, "Platforms");

    static HttpClient? _http;
    static HttpMessageHandler? _handlerForTest;

    /// <summary>Tests supply responses without contacting a live site. The app leaves this unset.</summary>
    internal static HttpMessageHandler? HandlerForTest {
        get => _handlerForTest;
        set {
            _handlerForTest = value;
            _http = null;
        }
    }

    static HttpClient Http {
        get {
            if (_http is not null) return _http;
            var handler = _handlerForTest ?? new HttpClientHandler { AllowAutoRedirect = true };
            _http = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(8) };
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "ResumeBuilder/1.0");
            return _http;
        }
    }

    static readonly Regex LinkTag = new(@"<link\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly object Gate = new();
    static readonly Dictionary<string, List<JobTask>> Pending = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, ImageSource> Images = new(StringComparer.OrdinalIgnoreCase);

    static readonly Dictionary<ApplicationPlatform, string> PlatformSites = new() {
        [ApplicationPlatform.LinkedIn] = "https://www.linkedin.com/",
        [ApplicationPlatform.Jobright] = "https://jobright.ai/",
        [ApplicationPlatform.Greenhouse] = "https://www.greenhouse.io/",
        [ApplicationPlatform.Lever] = "https://www.lever.co/",
        [ApplicationPlatform.Ashby] = "https://www.ashbyhq.com/",
        [ApplicationPlatform.Workday] = "https://www.workday.com/",
        [ApplicationPlatform.Indeed] = "https://www.indeed.com/",
        [ApplicationPlatform.Wellfound] = "https://wellfound.com/",
        [ApplicationPlatform.Dice] = "https://www.dice.com/",
        [ApplicationPlatform.SmartRecruiters] = "https://www.smartrecruiters.com/",
        [ApplicationPlatform.ICims] = "https://www.icims.com/",
        [ApplicationPlatform.Taleo] = "https://www.taleo.net/",
        [ApplicationPlatform.BambooHr] = "https://www.bamboohr.com/",
        [ApplicationPlatform.Jobvite] = "https://www.jobvite.com/",
        [ApplicationPlatform.SuccessFactors] = "https://www.successfactors.com/",
        [ApplicationPlatform.AdpRecruiting] = "https://workforcenow.adp.com/",
        [ApplicationPlatform.OracleRecruitingCloud] = "https://www.oracle.com/",
        [ApplicationPlatform.UkgPro] = "https://www.ukg.com/",
        [ApplicationPlatform.JazzHr] = "https://www.jazzhr.com/",
        [ApplicationPlatform.Recruitee] = "https://recruitee.com/",
        [ApplicationPlatform.BreezyHr] = "https://breezy.hr/",
        [ApplicationPlatform.Pinpoint] = "https://www.pinpointhq.com/",
        [ApplicationPlatform.Teamtailor] = "https://www.teamtailor.com/",
        [ApplicationPlatform.Workable] = "https://www.workable.com/",
        [ApplicationPlatform.RipplingRecruiting] = "https://www.rippling.com/",
        [ApplicationPlatform.DayforceRecruiting] = "https://www.dayforce.com/",
        [ApplicationPlatform.CornerstoneRecruiting] = "https://www.cornerstoneondemand.com/",
        [ApplicationPlatform.Avature] = "https://www.avature.net/",
        [ApplicationPlatform.Phenom] = "https://www.phenom.com/",
        [ApplicationPlatform.Eightfold] = "https://eightfold.ai/",
        [ApplicationPlatform.Beamery] = "https://beamery.com/",
        [ApplicationPlatform.Bullhorn] = "https://www.bullhorn.com/",
        [ApplicationPlatform.JobAdder] = "https://www.jobadder.com/",
        [ApplicationPlatform.ZohoRecruit] = "https://www.zoho.com/recruit/",
        [ApplicationPlatform.Cats] = "https://www.catsone.com/",
        [ApplicationPlatform.ApplicantStack] = "https://www.applicantstack.com/",
        [ApplicationPlatform.ClearCompany] = "https://www.clearcompany.com/",
        [ApplicationPlatform.PaylocityRecruiting] = "https://www.paylocity.com/",
        [ApplicationPlatform.PaycomRecruiting] = "https://www.paycom.com/",
        [ApplicationPlatform.PaycorRecruiting] = "https://www.paycor.com/",
        [ApplicationPlatform.IsolvedTalent] = "https://www.isolvedhcm.com/",
        [ApplicationPlatform.Fountain] = "https://www.fountain.com/",
        [ApplicationPlatform.Paradox] = "https://www.paradox.ai/",
        [ApplicationPlatform.Comeet] = "https://www.comeet.com/",
        [ApplicationPlatform.Manatal] = "https://www.manatal.com/",
        [ApplicationPlatform.RecruitCrm] = "https://recruitcrm.io/",
        [ApplicationPlatform.Recruiterflow] = "https://recruiterflow.com/",
        [ApplicationPlatform.JobScore] = "https://www.jobscore.com/",
        [ApplicationPlatform.Homerun] = "https://www.homerun.co/",
        [ApplicationPlatform.PersonioRecruiting] = "https://www.personio.com/",
        [ApplicationPlatform.TeamEngine] = "https://www.teamengine.io/",
        [ApplicationPlatform.TrakstarHire] = "https://www.trakstar.com/",
        [ApplicationPlatform.Neogov] = "https://www.neogov.com/",
        [ApplicationPlatform.GovernmentJobs] = "https://www.governmentjobs.com/",
        [ApplicationPlatform.SymplrRecruiting] = "https://www.symplr.com/"
    };

    static readonly Regex WebsiteInJob = new(
        @"Its website is (https?://[^\s<>""']+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static int _collectStarted;
    static HashSet<string>? _missingCompanies;

    /// <summary>
    /// The page the company icon is fetched from. The stored company URL wins. When that is empty,
    /// the company website stated in the job text is used. A company name is never turned into a URL.
    /// </summary>
    public static string? CompanyPageUrl(JobTask? task) {
        if (task is null) return null;
        if (JobUrls.IsWebUrl(task.CompanyUrl)) return task.CompanyUrl.Trim();
        var match = WebsiteInJob.Match(task.Jd ?? "");
        if (!match.Success) return null;
        var url = match.Groups[1].Value.TrimEnd('.', ',', ';', ')', ']', '>');
        return JobUrls.IsWebUrl(url) ? url : null;
    }

    /// <summary>Host of a company site, without www. A company name is never used.</summary>
    public static string? CompanyDomain(string? url) {
        if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        var host = uri.IdnHost.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        if (host.Length == 0 || !host.Contains('.') || host == "localhost") return null;
        foreach (var c in host)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-')) return null;
        return host;
    }

    /// <summary>File name for a recognised platform or job site, without an extension. Other and Unknown have none.</summary>
    public static string? PlatformKey(ApplicationPlatform platform) => platform switch {
        ApplicationPlatform.LinkedIn => "linkedin",
        ApplicationPlatform.Jobright => "jobright",
        ApplicationPlatform.Greenhouse => "greenhouse",
        ApplicationPlatform.Lever => "lever",
        ApplicationPlatform.Ashby => "ashby",
        ApplicationPlatform.Workday => "workday",
        ApplicationPlatform.Indeed => "indeed",
        ApplicationPlatform.Wellfound => "wellfound",
        ApplicationPlatform.Dice => "dice",
        ApplicationPlatform.SmartRecruiters => "smartrecruiters",
        ApplicationPlatform.ICims => "icims",
        ApplicationPlatform.Taleo => "taleo",
        ApplicationPlatform.BambooHr => "bamboohr",
        ApplicationPlatform.Jobvite => "jobvite",
        ApplicationPlatform.SuccessFactors => "successfactors",
        ApplicationPlatform.AdpRecruiting => "adp-recruiting",
        ApplicationPlatform.OracleRecruitingCloud => "oracle-recruiting-cloud",
        ApplicationPlatform.UkgPro => "ukg-pro",
        ApplicationPlatform.JazzHr => "jazzhr",
        ApplicationPlatform.Recruitee => "recruitee",
        ApplicationPlatform.BreezyHr => "breezy-hr",
        ApplicationPlatform.Pinpoint => "pinpoint",
        ApplicationPlatform.Teamtailor => "teamtailor",
        ApplicationPlatform.Workable => "workable",
        ApplicationPlatform.RipplingRecruiting => "rippling-recruiting",
        ApplicationPlatform.DayforceRecruiting => "dayforce-recruiting",
        ApplicationPlatform.CornerstoneRecruiting => "cornerstone-recruiting",
        ApplicationPlatform.Avature => "avature",
        ApplicationPlatform.Phenom => "phenom",
        ApplicationPlatform.Eightfold => "eightfold",
        ApplicationPlatform.Beamery => "beamery",
        ApplicationPlatform.Bullhorn => "bullhorn",
        ApplicationPlatform.JobAdder => "jobadder",
        ApplicationPlatform.ZohoRecruit => "zoho-recruit",
        ApplicationPlatform.Cats => "cats",
        ApplicationPlatform.ApplicantStack => "applicantstack",
        ApplicationPlatform.ClearCompany => "clearcompany",
        ApplicationPlatform.PaylocityRecruiting => "paylocity-recruiting",
        ApplicationPlatform.PaycomRecruiting => "paycom-recruiting",
        ApplicationPlatform.PaycorRecruiting => "paycor-recruiting",
        ApplicationPlatform.IsolvedTalent => "isolved-talent",
        ApplicationPlatform.Fountain => "fountain",
        ApplicationPlatform.Paradox => "paradox",
        ApplicationPlatform.Comeet => "comeet",
        ApplicationPlatform.Manatal => "manatal",
        ApplicationPlatform.RecruitCrm => "recruit-crm",
        ApplicationPlatform.Recruiterflow => "recruiterflow",
        ApplicationPlatform.JobScore => "jobscore",
        ApplicationPlatform.Homerun => "homerun",
        ApplicationPlatform.PersonioRecruiting => "personio-recruiting",
        ApplicationPlatform.TeamEngine => "team-engine",
        ApplicationPlatform.TrakstarHire => "trakstar-hire",
        ApplicationPlatform.Neogov => "neogov",
        ApplicationPlatform.GovernmentJobs => "governmentjobs",
        ApplicationPlatform.SymplrRecruiting => "symplr-recruiting",
        _ => null
    };

    /// <summary>Stable cache key. The file on disk is this name with the colon replaced by a hyphen.</summary>
    public static string? PlatformCacheKey(ApplicationPlatform platform) {
        var name = PlatformKey(platform);
        return name is null ? null : "platform:" + name;
    }

    public static ImageSource? CompanyImage(string? companyUrl) {
        var domain = CompanyDomain(companyUrl);
        return domain is null ? null : Load(CompaniesFolder, "c:" + domain, domain);
    }

    public static ImageSource? PlatformImage(ApplicationPlatform platform) {
        var key = PlatformKey(platform);
        return key is null ? null : Load(PlatformsFolder, "platform:" + key, key);
    }

    /// <summary>
    /// Starts a background fetch for a newly imported job. Returns immediately. A cached file is reused
    /// and a failed domain is not requested again this run.
    /// </summary>
    public static void Collect(JobTask task) {
        try {
            _ = CollectAsync(task);
        } catch (Exception ex) {
            PerfLog.Line("ICON collect failed " + ex.GetType().Name);
        }
    }

    static async Task CollectAsync(JobTask task) {
        try {
            await FetchCompany(task);
            await FetchPlatform(task);
        } catch (Exception ex) {
            PerfLog.Line("ICON collect failed " + ex.GetType().Name);
        }
    }

    /// <summary>
    /// Fetches company icons that are not already on disk. One pass per process, off the UI thread.
    /// A cached file and a domain that already failed are not requested again.
    /// </summary>
    public static void CollectUncached(IEnumerable<JobTask> tasks) {
        if (Interlocked.Exchange(ref _collectStarted, 1) == 1) return;
        var list = tasks.ToList();
        _ = Task.Run(async () => {
            foreach (var task in list) {
                try {
                    await FetchCompany(task, notifyIfCached: false);
                    await FetchPlatform(task, notifyIfCached: false);
                } catch (Exception ex) { PerfLog.Line("ICON collect failed " + ex.GetType().Name); }
            }
        });
    }

    static async Task FetchCompany(JobTask task, bool notifyIfCached = true) {
        var page = task.LogoUrl;
        var domain = CompanyDomain(page);
        if (domain is null) return;
        if (FindFile(CompaniesFolder, domain) is not null) {
            if (notifyIfCached) Notify(task);
            return;
        }
        if (IsKnownMissing(domain)) return;
        if (!Lead("c:" + domain, task)) return;
        var saved = await DownloadSiteIcon("c:" + domain, page!, CompaniesFolder, domain, MissingCompaniesPath);
        Finish("c:" + domain, saved);
    }

    static bool IsKnownMissing(string domain) {
        lock (Gate) return MissingCompanies().Contains(domain);
    }

    static HashSet<string> MissingCompanies() {
        if (_missingCompanies is not null) return _missingCompanies;
        _missingCompanies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try {
            if (!File.Exists(MissingCompaniesPath)) return _missingCompanies;
            var items = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(MissingCompaniesPath));
            if (items is null) return _missingCompanies;
            foreach (var item in items)
                if (!string.IsNullOrWhiteSpace(item)) _missingCompanies.Add(item);
        } catch {
            // An unreadable miss list only means a domain may be requested once more.
        }
        return _missingCompanies;
    }

    /// <summary>
    /// Icons captured for this job: the application platform when it has one, then the job site.
    /// A Jobright posting with no ATS still captures the Jobright icon.
    /// </summary>
    public static IReadOnlyList<ApplicationPlatform> PlatformsFor(JobTask? task) {
        var found = new List<ApplicationPlatform>();
        if (task is null) return found;
        Add(task.ApplicationPlatform);
        Add(SitePlatform(task));
        return found;

        void Add(ApplicationPlatform platform) {
            if (PlatformKey(platform) is null || found.Contains(platform)) return;
            found.Add(platform);
        }
    }

    static ApplicationPlatform SitePlatform(JobTask task) => task.JobSite switch {
        "Jobright" => ApplicationPlatform.Jobright,
        "LinkedIn" => ApplicationPlatform.LinkedIn,
        "Indeed" => ApplicationPlatform.Indeed,
        "Dice" => ApplicationPlatform.Dice,
        "Wellfound" => ApplicationPlatform.Wellfound,
        _ => ApplicationPlatform.Unknown
    };

    static async Task FetchPlatform(JobTask task, bool notifyIfCached = true) {
        foreach (var platform in PlatformsFor(task))
            await FetchOnePlatform(task, platform, notifyIfCached);
    }

    static string? SiteFor(ApplicationPlatform platform) {
        var over = SiteForTest?.Invoke(platform);
        if (!string.IsNullOrWhiteSpace(over)) return over;
        return PlatformSites.TryGetValue(platform, out var site) ? site : null;
    }

    static async Task FetchOnePlatform(JobTask task, ApplicationPlatform platform, bool notifyIfCached) {
        var key = PlatformKey(platform);
        var cacheKey = PlatformCacheKey(platform);
        var site = SiteFor(platform);
        if (key is null || cacheKey is null || site is null) return;
        if (FindFile(PlatformsFolder, key) is not null) {
            if (notifyIfCached) Notify(task);
            return;
        }
        if (!Lead(cacheKey, task)) return;
        PlatformDownloadCount++;
        var saved = await DownloadSiteIcon(cacheKey, site, PlatformsFolder, "platform-" + key, MissingPlatformsPath);
        Finish(cacheKey, saved);
    }

    static bool Lead(string attemptKey, JobTask task) {
        lock (Gate) {
            if (!Pending.TryGetValue(attemptKey, out var waiting)) {
                Pending[attemptKey] = new List<JobTask> { task };
                return true;
            }
            waiting.Add(task);
            return false;
        }
    }

    static void Finish(string attemptKey, bool saved) {
        List<JobTask>? waiting;
        lock (Gate) {
            if (!Pending.Remove(attemptKey, out waiting)) return;
        }
        if (!saved || waiting is null) return;
        foreach (var task in waiting) Notify(task);
    }

    static async Task<bool> DownloadSiteIcon(string cacheKey, string pageUrl, string folder, string key, string missingFile) {
        try {
            if (!Uri.TryCreate(pageUrl.Trim(), UriKind.Absolute, out var page)) return false;
            var html = await ReadText(page);
            var candidates = new List<Uri>();
            if (html is not null && BestIcon(html, page) is Uri declared) candidates.Add(declared);
            candidates.Add(new Uri(page.GetLeftPart(UriPartial.Authority) + "/favicon.ico"));

            foreach (var candidate in candidates.Distinct()) {
                var bytes = await ReadImage(candidate);
                if (bytes is null || ExtensionFor(bytes) is not string ext) continue;
                if (!Decode(bytes, out var image) || image is null) continue;
                Directory.CreateDirectory(folder);
                // Company saves use the collector base (ford.com -> ford-com). Platform names stay as-is.
                var fileBase = folder == CompaniesFolder ? key.Replace('.', '-') : key;
                var path = Path.Combine(folder, fileBase + ext);
                if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
                lock (Gate) Images[cacheKey] = image;
                Remember(folder == CompaniesFolder ? "companies" : "platforms", key, fileBase + ext);
                PerfLog.Line($"ICON saved {(folder == CompaniesFolder ? "company" : "platform")} {key}");
                return true;
            }
        } catch (Exception ex) {
            PerfLog.Line("ICON download failed " + ex.GetType().Name);
        }

        RememberMissing(missingFile, key);
        PerfLog.Line($"ICON missing {(missingFile.Contains("companies", StringComparison.OrdinalIgnoreCase) ? "company" : "platform")} {key}");
        return false;
    }

    static async Task<string?> ReadText(Uri url) {
        var bytes = await ReadLimited(url, MaxPageBytes, allowTruncation: true);
        return bytes is null ? null : System.Text.Encoding.UTF8.GetString(bytes);
    }

    static Task<byte[]?> ReadImage(Uri url) => ReadLimited(url, MaxImageBytes, allowTruncation: false);

    static async Task<byte[]?> ReadLimited(Uri url, int max, bool allowTruncation) {
        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps) return null;
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        if (!allowTruncation && response.Content.Headers.ContentLength is long length && length > max) return null;
        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (buffer.Length < max) {
            var n = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, max - buffer.Length))).ConfigureAwait(false);
            if (n == 0) break;
            buffer.Write(chunk, 0, n);
        }
        if (buffer.Length == 0) return null;
        if (!allowTruncation && buffer.Length >= max) return null;
        return buffer.ToArray();
    }

    static Uri? BestIcon(string html, Uri page) {
        Uri? apple = null, icon = null, shortcut = null;
        foreach (Match match in LinkTag.Matches(html)) {
            var tag = match.Value;
            var rel = Attribute(tag, "rel");
            var href = Attribute(tag, "href");
            if (rel is null || href is null || href.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Uri.TryCreate(page, href, out var absolute)) continue;
            if (absolute.Scheme != Uri.UriSchemeHttp && absolute.Scheme != Uri.UriSchemeHttps) continue;
            if (rel.Contains("apple-touch-icon", StringComparison.OrdinalIgnoreCase)) apple ??= absolute;
            else if (rel.Contains("shortcut", StringComparison.OrdinalIgnoreCase)) shortcut ??= absolute;
            else if (rel.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                        .Any(part => part.Equals("icon", StringComparison.OrdinalIgnoreCase)))
                icon ??= absolute;
        }
        return apple ?? shortcut ?? icon;
    }

    static string? Attribute(string tag, string name) {
        var match = Regex.Match(tag, name + @"\s*=\s*(?:""([^""]*)""|'([^']*)')", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        var value = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    static string? ExtensionFor(byte[] bytes) {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return ".png";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8) return ".jpg";
        if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 1 && bytes[3] == 0) return ".ico";
        if (bytes.Length >= 6 && bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F') return ".gif";
        if (bytes.Length >= 12 && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
            && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
            return ".webp";
        return null;
    }

    static ImageSource? Load(string folder, string cacheKey, string fileKey) {
        var started = _measure ? Stopwatch.GetTimestamp() : 0;
        try {
            lock (Gate) {
                if (Images.TryGetValue(cacheKey, out var cached)) return cached;
            }
            var path = FindFile(folder, fileKey);
            if (path is null) return null;
            try {
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length == 0 || bytes.Length > MaxImageBytes || !Decode(bytes, out var image) || image is null) return null;
                lock (Gate) Images[cacheKey] = image;
                return image;
            } catch {
                return null;
            }
        } finally {
            if (_measure) {
                _measureLoads++;
                _measureTicks += Stopwatch.GetTimestamp() - started;
            }
        }
    }

    static bool _measure;
    static int _measureLoads;
    static long _measureTicks;

    public static void BeginMeasure() {
        _measure = true;
        _measureLoads = 0;
        _measureTicks = 0;
    }

    public static void EndMeasure(out int loads, out long ms) {
        _measure = false;
        loads = _measureLoads;
        ms = _measureTicks * 1000 / Stopwatch.Frequency;
    }

    static string? FindFile(string folder, string key) {
        try {
            // Company files may be ford.com.png or the collector's ford-com.ico. Platform keys have no dot.
            var found = MatchKey(folder, key)
                ?? MatchKey(folder, "platform-" + key)
                ?? (key.StartsWith("platform-", StringComparison.Ordinal) ? MatchKey(folder, key["platform-".Length..]) : null)
                ?? (key.Contains('.') ? MatchKey(folder, key.Replace('.', '-')) : null);
            if (found is not null) return found;
            var fromManifest = ManifestName(folder == CompaniesFolder ? "companies" : "platforms", key);
            if (fromManifest is null) return null;
            var named = Path.Combine(folder, Path.GetFileName(fromManifest));
            return File.Exists(named) ? named : null;
        } catch {
            return null;
        }
    }

    static string? MatchKey(string folder, string key) {
        foreach (var ext in Extensions) {
            var path = Path.Combine(folder, key + ext);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    static bool Decode(byte[] bytes, out ImageSource? image) {
        image = null;
        try {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = new MemoryStream(bytes);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.EndInit();
            bitmap.Freeze();
            image = bitmap;
            return true;
        } catch {
            return false;
        }
    }

    static void Notify(JobTask task) {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        if (dispatcher.CheckAccess()) task.NotifyIconsChanged();
        else dispatcher.BeginInvoke(task.NotifyIconsChanged);
    }

    static string ManifestPath => Path.Combine(Root, "icon-manifest.json");
    static string MissingCompaniesPath => Path.Combine(Root, "missing-companies.json");
    static string MissingPlatformsPath => Path.Combine(Root, "missing-platforms.json");

    static void Remember(string group, string key, string fileName) {
        try {
            Directory.CreateDirectory(Root);
            lock (Gate) {
                var root = ReadObject(ManifestPath);
                if (!root.TryGetValue(group, out var section) || section is not Dictionary<string, string> map) {
                    map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    root[group] = map;
                }
                map[key] = fileName;
                File.WriteAllText(ManifestPath, JsonSerializer.Serialize(root));
            }
        } catch {
            // A manifest problem never affects the saved image or the job.
        }
    }

    static string? ManifestName(string group, string key) {
        try {
            if (!File.Exists(ManifestPath)) return null;
            lock (Gate) {
                var root = ReadObject(ManifestPath);
                if (root.TryGetValue(group, out var section) && section is Dictionary<string, string> map &&
                    map.TryGetValue(key, out var name))
                    return name;
            }
        } catch {
            // An unfamiliar manifest is left untouched.
        }
        return null;
    }

    static Dictionary<string, object> ReadObject(string path) {
        if (!File.Exists(path)) return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in doc.RootElement.EnumerateObject()) {
            if (prop.Value.ValueKind != JsonValueKind.Object) continue;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in prop.Value.EnumerateObject())
                if (entry.Value.ValueKind == JsonValueKind.String)
                    map[entry.Name] = entry.Value.GetString() ?? "";
            result[prop.Name] = map;
        }
        return result;
    }

    static void RememberMissing(string path, string key) {
        try {
            Directory.CreateDirectory(Root);
            lock (Gate) {
                var items = new List<string>();
                if (File.Exists(path)) {
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
                    items.AddRange(doc.RootElement.EnumerateArray()
                        .Where(item => item.ValueKind == JsonValueKind.String)
                        .Select(item => item.GetString() ?? "")
                        .Where(item => item.Length > 0));
                }
                if (items.Contains(key, StringComparer.OrdinalIgnoreCase)) return;
                items.Add(key);
                MissingCompanies().Add(key);
                File.WriteAllText(path, JsonSerializer.Serialize(items));
            }
        } catch {
            // Recording a miss is optional.
        }
    }
}
