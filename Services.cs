using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace ResumeBuilder;

/// <summary>
/// settings.json and tasks.json — the user's configuration and every job.
///
/// Phase 1 (install-readiness) made both files crash-safe:
///   * WRITES are atomic: temp file, flushed to disk, then File.Replace, which keeps the previous
///     version as "&lt;name&gt;.bak". A crash mid-save leaves the old file, never a half-written one.
///   * A MISSING file is a legitimate first run: defaults / no jobs.
///   * A file that EXISTS but cannot be parsed is never treated as empty. Its exact bytes are kept
///     as "&lt;name&gt;.corrupt-&lt;timestamp&gt;.json", the .bak is tried, and if that fails too the app
///     runs on defaults with WRITES TO THAT FILE REFUSED for the session — so nothing can overwrite
///     the user's real data. Before this, any read error returned defaults and the next save wiped
///     every job. <see cref="Problems"/> is shown to the user at startup.
///   * A transient read error (file briefly locked) after a good load returns the last good copy.
/// All path-level methods take the path, so tests run on temporary files only.
/// </summary>
public static class Storage {
    static readonly JsonSerializerOptions Opt = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    /// <summary>The active profile folder, or the global Resume Builder folder when no profile is open.</summary>
    public static string DataDir => ProfileContext.ProfileRoot;
    public static string SettingsPath => Path.Combine(DataDir, "settings.json");
    public static string TasksPath => Path.Combine(DataDir, "tasks.json");
    public static string EmailTasksPath => ProfileContext.EmailTasksPath;
    /// <summary>ChatGPT WebView2 data for the open profile. Another profile uses another folder.</summary>
    public static string WebViewUserDataFolder => Path.Combine(ProfileContext.ProfileRoot, "WebView2");

    static readonly object Gate = new();
    static readonly Dictionary<string, string> Blocked = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, string> LastGood = new(StringComparer.OrdinalIgnoreCase);
    static readonly List<string> ProblemList = new();

    /// <summary>What went wrong reading settings/tasks this session, in plain words, for the startup notice.</summary>
    public static IReadOnlyList<string> Problems { get { lock (Gate) return ProblemList.ToList(); } }

    /// <summary>Why saving to <paramref name="path"/> is refused this session, or null when it is allowed.</summary>
    public static string? WriteBlockedReason(string path) { lock (Gate) return Blocked.GetValueOrDefault(Path.GetFullPath(path)); }

    /// <summary>Lifts a refusal once the file has been dealt with (tests restore a file this way).</summary>
    internal static void ClearWriteBlock(string path) { lock (Gate) { Blocked.Remove(Path.GetFullPath(path)); LastGood.Remove(Path.GetFullPath(path)); } }

    public static AppSettings LoadSettings() => LoadSettingsFrom(SettingsPath);
    public static bool SaveSettings(AppSettings s) => SaveSettingsTo(SettingsPath, s);
    /// <summary>Jobs come from SQLite. An existing tasks.json file is never read.</summary>
    public static List<JobTask> LoadTasks() {
        try { return JobStore.GetJobs(); }
        catch (Exception ex) {
            PerfLog.Line("JOBSTORE load failed " + ex.GetType().Name);
            return new List<JobTask>();
        }
    }

    /// <summary>Writes jobs, applications and stored resume paths to SQLite. Does not touch tasks.json.</summary>
    public static bool SaveTasks(IEnumerable<JobTask> tasks) {
        try {
            JobStore.SaveJobs(tasks);
            return true;
        } catch (Exception ex) {
            PerfLog.Line("JOBSTORE sync failed " + ex.GetType().Name);
            return false;
        }
    }

    /// <summary>Settings from <paramref name="path"/>, with first-run defaults filled in (never over a set value).</summary>
    public static AppSettings LoadSettingsFrom(string path) {
        var settings = LoadFrom(path, () => new AppSettings());
        AppPaths.ApplyFirstRunDefaults(settings);
        return settings;
    }

    public static List<JobTask> LoadTasksFrom(string path) => LoadFrom(path, () => new List<JobTask>());

    /// <summary>Atomic save. Returns false (and writes nothing) when the file's load failed this session.</summary>
    public static bool SaveSettingsTo(string path, AppSettings settings) => SaveTo(path, JsonSerializer.Serialize(settings, Opt));

    /// <summary>Atomic save. Returns false (and writes nothing) when the file's load failed this session.</summary>
    public static bool SaveTasksTo(string path, IEnumerable<JobTask> tasks) => SaveTo(path, JsonSerializer.Serialize(tasks, Opt));

    public static List<EmailTask> LoadEmailTasks() => LoadFrom(EmailTasksPath, () => new List<EmailTask>());

    public static bool SaveEmailTasks(IEnumerable<EmailTask> tasks) => SaveTo(EmailTasksPath, JsonSerializer.Serialize(tasks, Opt));

    static T LoadFrom<T>(string path, Func<T> empty) where T : class {
        var full = Path.GetFullPath(path);
        var read = SafeJsonFile.Read<T>(full, Opt);
        var name = Path.GetFileName(full);

        switch (read.State) {
            case SafeReadState.Missing:
                return empty();

            case SafeReadState.Loaded:
                Remember(full, read.Text!);
                return read.Value!;

            case SafeReadState.FromBackup:
                Remember(full, read.Text!);
                Report($"{name} could not be read ({read.Problem}). It was restored from {name}.bak; " +
                       $"the unreadable copy was kept as {Path.GetFileName(read.QuarantinePath) ?? "(not kept)"}.");
                return read.Value!;

            default: {
                // A lock or a brief I/O error after a good load: carry on with the last good copy.
                if (!read.Corrupt) {
                    string? cached;
                    lock (Gate) cached = LastGood.GetValueOrDefault(full);
                    if (cached is not null && JsonSerializer.Deserialize<T>(cached, Opt) is T value) {
                        PerfLog.Line($"STORAGE {name} read failed ({read.Problem}); using the last good copy");
                        return value;
                    }
                }

                var reason = $"{name} could not be read ({read.Problem}). Resume Builder will NOT save " +
                             $"{name} this session, so your file is not overwritten" +
                             (read.QuarantinePath is null ? "." : $"; a copy was kept as {Path.GetFileName(read.QuarantinePath)}.");
                lock (Gate) Blocked[full] = reason;
                Report(reason);
                return empty();
            }
        }
    }

    static bool SaveTo(string path, string json) {
        var full = Path.GetFullPath(path);
        if (WriteBlockedReason(full) is string blocked) {
            PerfLog.Line($"STORAGE save refused {Path.GetFileName(full)} - {blocked}");
            return false;
        }
        SafeJsonFile.Write(full, json);
        Remember(full, json);
        return true;
    }

    static void Remember(string full, string json) { lock (Gate) LastGood[full] = json; }

    static void Report(string problem) {
        PerfLog.Line("STORAGE " + problem);
        lock (Gate) if (!ProblemList.Contains(problem)) ProblemList.Add(problem);
    }
}

public enum SafeReadState { Missing, Loaded, FromBackup, Unreadable }

/// <summary>The outcome of reading one JSON state file.</summary>
public sealed class SafeReadResult<T> {
    public SafeReadState State { get; init; }
    public T? Value { get; init; }
    public string? Text { get; init; }

    /// <summary>True when the file's CONTENT is bad (not JSON, wrong shape) rather than briefly unreadable.</summary>
    public bool Corrupt { get; init; }
    public string? Problem { get; init; }

    /// <summary>Where the unreadable file's exact bytes were kept, if they were.</summary>
    public string? QuarantinePath { get; init; }
}

/// <summary>
/// Crash-safe read and write of one JSON file. Holds no state: <see cref="Storage"/> decides policy.
/// </summary>
public static class SafeJsonFile {
    /// <summary>
    /// temp file -> flush to disk -> File.Replace (previous version kept as .bak), or a move when the
    /// file is new. The temp file is always removed. Throws on failure, as File.WriteAllText did.
    /// </summary>
    public static void Write(string path, string content) {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var temp = path + ".tmp";
        var backup = path + ".bak";
        try {
            var bytes = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            if (!File.Exists(path)) {
                File.Move(temp, path);
                return;
            }

            try {
                File.Replace(temp, path, backup, ignoreMetadataErrors: true);
            } catch (IOException) {
                // Some file systems refuse Replace; fall back to backup-copy + overwriting move.
                File.Copy(path, backup, overwrite: true);
                File.Move(temp, path, overwrite: true);
            }
        } finally {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
        }
    }

    /// <summary>Reads and parses <paramref name="path"/>; on bad content keeps a copy and tries the .bak.</summary>
    public static SafeReadResult<T> Read<T>(string path, JsonSerializerOptions options, int attempts = 3) where T : class {
        if (!File.Exists(path)) return new SafeReadResult<T> { State = SafeReadState.Missing };

        string? text = null;
        string problem = "";
        for (var attempt = 1; attempt <= attempts && text is null; attempt++) {
            try {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                text = reader.ReadToEnd();
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                problem = ex.GetType().Name;
                if (attempt < attempts) Thread.Sleep(60 * attempt);
            }
        }

        if (text is null)
            return new SafeReadResult<T> { State = SafeReadState.Unreadable, Corrupt = false, Problem = problem };

        if (TryParse<T>(text, options, out var value, out var parseProblem))
            return new SafeReadResult<T> { State = SafeReadState.Loaded, Value = value, Text = text };

        // Bad content: keep the exact bytes before anything else can touch the file.
        var quarantine = Quarantine(path);

        var backup = path + ".bak";
        if (File.Exists(backup)) {
            try {
                var backupText = File.ReadAllText(backup);
                if (TryParse<T>(backupText, options, out var restored, out _))
                    return new SafeReadResult<T> {
                        State = SafeReadState.FromBackup, Value = restored, Text = backupText,
                        Corrupt = true, Problem = parseProblem, QuarantinePath = quarantine
                    };
            } catch { /* the backup is unusable too */ }
        }

        return new SafeReadResult<T> {
            State = SafeReadState.Unreadable, Corrupt = true, Problem = parseProblem, QuarantinePath = quarantine
        };
    }

    static bool TryParse<T>(string text, JsonSerializerOptions options, out T? value, out string problem) where T : class {
        value = null;
        problem = "";
        try {
            value = JsonSerializer.Deserialize<T>(text, options);
            if (value is not null) return true;
            problem = "the file is empty";
        } catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException) {
            problem = "it is not valid JSON";
        }
        return false;
    }

    /// <summary>
    /// "tasks.json" -> "tasks.corrupt-20260921-123456.json", byte for byte. An identical copy already
    /// kept is reused, so relaunching on the same bad file does not pile up duplicates.
    /// </summary>
    static string? Quarantine(string path) {
        try {
            var dir = Path.GetDirectoryName(path) ?? ".";
            var stem = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            var bytes = File.ReadAllBytes(path);

            foreach (var existing in Directory.GetFiles(dir, stem + ".corrupt-*" + ext))
                if (File.ReadAllBytes(existing).AsSpan().SequenceEqual(bytes)) return existing;

            var target = Path.Combine(dir, $"{stem}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}{ext}");
            for (var n = 2; File.Exists(target); n++)
                target = Path.Combine(dir, $"{stem}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}-{n}{ext}");
            File.WriteAllBytes(target, bytes);
            return target;
        } catch {
            return null;
        }
    }
}

public sealed class ImportResult {
    public int FilesImported { get; set; }
    public int JobsQueued { get; set; }
    public int JobsExisting { get; set; }

    /// <summary>Files whose job the user's import filters refused. Not an error and not a failure.</summary>
    public int JobsSkipped { get; set; }
    public List<string> Errors { get; } = new();
}

/// <summary>
/// The one place a job URL is made comparable. Duplicate detection never compares raw strings.
/// </summary>
public static class JobUrls {
    /// <summary>
    /// Query parameters that only record how the visitor arrived. Verified on Jobright, whose job
    /// addresses are /jobs/info/&lt;id&gt; and whose only query parameter seen is utm_source. Anything
    /// not on this list is kept, because on another site a parameter can be what names the job.
    /// </summary>
    static readonly string[] TrackingParameters = {
        "gclid", "gbraid", "wbraid", "fbclid", "msclkid", "yclid", "igshid",
        "mc_cid", "mc_eid", "_hsenc", "_hsmi", "li_fat_id"
    };

    static bool IsTracking(string key) =>
        key.StartsWith("utm_", StringComparison.OrdinalIgnoreCase)
        || TrackingParameters.Contains(key, StringComparer.OrdinalIgnoreCase);

    /// <summary>True for an absolute http or https address.</summary>
    public static bool IsWebUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// A job URL in one comparable form, or null when it is not a web address: trimmed, scheme and
    /// host lower-cased, default port dropped, fragment dropped, trailing slash dropped (except for
    /// the root), tracking parameters dropped and the rest sorted so their order does not matter.
    /// The path keeps its case — on the web it can be significant.
    /// </summary>
    public static string? Normalize(string? url) {
        if (!IsWebUrl(url)) return null;
        var uri = new Uri(url!.Trim());

        var path = uri.AbsolutePath;
        if (path.Length > 1) path = path.TrimEnd('/');
        if (path.Length == 0) path = "/";

        var query = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(pair => !IsTracking(pair.Split('=', 2)[0]))
            .OrderBy(pair => pair, StringComparer.Ordinal)
            .ToList();

        var authority = uri.IsDefaultPort ? uri.Host : uri.Host + ":" + uri.Port;
        return uri.Scheme.ToLowerInvariant() + "://" + authority.ToLowerInvariant() + path
               + (query.Count == 0 ? "" : "?" + string.Join("&", query));
    }

    /// <summary>
    /// Whether two links are the same posting. A Jobright job matches by its stable external job id.
    /// Any other posting matches by the normalized URL. Company and title are never compared.
    /// </summary>
    public static bool SamePosting(string? storedLink, string? incomingUrl) {
        var incomingId = JobrightPageExtractor.JobIdFromUrl(incomingUrl);
        var storedId = JobrightPageExtractor.JobIdFromUrl(storedLink);
        if (incomingId is not null && storedId is not null)
            return incomingId == storedId;

        var stored = Normalize(storedLink);
        var incoming = Normalize(incomingUrl);
        return stored is not null && stored == incoming;
    }
}

/// <summary>
/// Turns ONE job into ONE task. Incoming JSON files and the built-in job browser both come through
/// <see cref="ImportOne"/>, so validation and duplicate handling cannot drift apart. There is no
/// batch path: a file holding more than one job is refused, never partly imported.
/// </summary>
public static class JobImporter {
    public const string IncomingSource = "incoming-json";
    public const string BrowserSource = "jobright-browser";
    public const string OneJobOnly = "Only one job per input file is supported.";

    static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };
    static readonly JsonDocumentOptions ParseOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public static ImportResult Import(AppSettings settings, List<JobTask> tasks) {
        var result = new ImportResult();
        if (string.IsNullOrWhiteSpace(settings.IncomingFolder) || !Directory.Exists(settings.IncomingFolder)) {
            result.Errors.Add("Incoming folder is not configured or does not exist."); return result;
        }
        if (string.IsNullOrWhiteSpace(settings.ImportedFolder)) {
            result.Errors.Add("Imported folder is not configured."); return result;
        }
        Directory.CreateDirectory(settings.ImportedFolder);

        foreach (var file in Directory.GetFiles(settings.IncomingFolder, "*.json")) {
            try {
                // The Incoming folder shares the one gate, so it inherits the same import filters.
                var outcome = ImportOne(ReadSingleJob(File.ReadAllText(file)), IncomingSource, tasks, settings);
                switch (outcome.Kind) {
                    case JobImportKind.Imported: result.JobsQueued++; break;
                    case JobImportKind.Duplicate: result.JobsExisting++; break;
                    // A filtered job is not a broken file: archive it like any handled input.
                    case JobImportKind.Skipped: result.JobsSkipped++; break;
                    // A refused file stays in Incoming, so it can be fixed and picked up again.
                    default: throw new InvalidDataException(outcome.Reason);
                }

                var dest = UniqueDestination(settings.ImportedFolder, Path.GetFileName(file));
                File.Move(file, dest);
                result.FilesImported++;
            } catch (Exception ex) {
                result.Errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        return result;
    }

    /// <summary>
    /// Reads one Incoming file as exactly one job. An array, or an object carrying a "jobs" list (the
    /// retired batch format), is refused outright rather than having its first job quietly imported.
    /// </summary>
    public static JobImportData ReadSingleJob(string json) {
        JsonNode? node;
        try { node = JsonNode.Parse(json, null, ParseOptions); }
        catch (JsonException) { throw new InvalidDataException("The file is not valid JSON."); }

        switch (node) {
            case JsonArray:
                throw new InvalidDataException(OneJobOnly);
            case JsonObject o when o.Any(p => p.Key.Equals("jobs", StringComparison.OrdinalIgnoreCase)):
                throw new InvalidDataException(OneJobOnly +
                    " This file uses the old batch format; write one { company, title, jobUrl, companyUrl, description } object per file.");
            case JsonObject o:
                return o.Deserialize<JobImportData>(ReadOptions) ?? throw new InvalidDataException("The file does not contain a job.");
            default:
                throw new InvalidDataException("The file must contain one job object.");
        }
    }

    /// <summary>
    /// Validate, normalize the job URL, refuse a duplicate, apply the user's import filters,
    /// otherwise create one task with a fresh internal id and save. The task starts Queued and, by
    /// JobTask's own default, application-Viewed. Nothing is written for an invalid job, a duplicate
    /// (beyond the existing ApplyUrl backfill) or a filtered job.
    ///
    /// This is THE import gate. Every path — Import Current Job, Import Found / Auto Import and the
    /// Incoming folder — arrives here, so the filters cannot be bypassed and are never duplicated.
    /// <paramref name="settings"/> carries the five switches; pass
    /// <see cref="JobImportFilter.NoFilters"/> to import without any.
    /// </summary>
    public static JobImportOutcome ImportOne(JobImportData data, string source, List<JobTask> tasks, AppSettings settings) {
        var company = (data.Company ?? "").Trim();
        var title = (data.Title ?? "").Trim();
        var description = (data.Description ?? "").Trim();

        string? Refuse() =>
            company.Length == 0 ? "Company is required."
            : title.Length == 0 ? "Job title is required."
            : string.IsNullOrWhiteSpace(data.JobUrl) ? "Job URL is required."
            : !JobUrls.IsWebUrl(data.JobUrl) ? "Job URL is not a valid web address."
            : description.Length == 0 ? "Job description is required."
            : null;

        if (Refuse() is string reason)
            return new JobImportOutcome { Kind = JobImportKind.Invalid, Title = title, Company = company, Reason = reason };

        var jobUrl = JobUrls.Normalize(data.JobUrl)!;

        // One posting, one task. Jobright matches by its external job id; every other site by the
        // normalized URL. Company and title are never the key.
        var existing = tasks.FirstOrDefault(t => JobUrls.SamePosting(t.Link, jobUrl));
        if (existing is not null)
        {
            var jobrightId = JobrightPageExtractor.JobIdFromUrl(jobUrl);
            PerfLog.Line(jobrightId is not null
                ? "IDENTITY lookup source=jobright id=" + jobrightId
                : "IDENTITY lookup source=internal id=" + existing.JobId);
            PerfLog.Line(
            $"DUPLICATE FOUND\n" +
            $"incoming: {jobUrl}\n" +
            $"existing: {existing.Link}\n" +
            $"jobId: {existing.JobId}\n" +
            $"title: {existing.Title}"
        );

            // An existing job may gain an application address it lacks — never lose or replace one.
            var filled = ApplyCapture.FillIfEmpty(existing, data.ApplyUrl, DateTime.Now);
            if (filled) {
                Storage.SaveTasks(tasks);
                PerfLog.Line("JOB UPSERT id=" + existing.JobId + " reason=import");
                PerfLog.Line($"IMPORT apply link added {existing.JobId} {existing.ApplicationPlatform}");
            }
            LogJobImport(existing);
            // Returns immediately. A cached icon is reused; a missing company or platform icon is fetched
            // in the background. This does not depend on the apply link having just been filled.
            IconCache.Collect(existing);

            return new JobImportOutcome {
                Kind = JobImportKind.Duplicate, JobId = existing.JobId, Title = existing.Title,
                Company = existing.Company, ApplicationStatus = existing.ApplicationStatus,
                ApplyUrlRecorded = filled
            };
        }

        // The import filter runs AFTER the duplicate decision on purpose: a job already in Resume
        // Builder keeps its task and still gains an application address it lacks, whatever the
        // filters now say. Filters gate what BECOMES a job, they never remove one.
        var decision = JobImportFilter.Evaluate(data, settings);
        if (!decision.Accepted) {
            PerfLog.Line($"IMPORT skipped {SafeJobRef(jobUrl)} reason={JobImportFilter.LogReason(decision.Reason)}");
            return new JobImportOutcome {
                Kind = JobImportKind.Skipped, Title = title, Company = company,
                Reason = JobImportFilter.Describe(decision.Reason), FilterReason = decision.Reason
            };
        }

        var task = new JobTask {
            JobId = NewInternalId(tasks),
            Source = source,
            Company = company,
            Title = title,
            Location = "",                   // not part of the input; never invented
            Jd = description,
            Link = jobUrl,
            // Optional: kept only when it is a real web address, so a bad value cannot fail the job.
            CompanyUrl = JobUrls.IsWebUrl(data.CompanyUrl) ? data.CompanyUrl!.Trim() : "",
            About = "",
            Status = "Queued"
        };
        // Optional, like CompanyUrl: a missing or invalid application address leaves it empty and
        // never fails the import. The Apply-click capture stays the fallback.
        var applyRecorded = ApplyCapture.FillIfEmpty(task, data.ApplyUrl, DateTime.Now);

        tasks.Add(task);
        Storage.SaveTasks(tasks);
        PerfLog.Line("JOB UPSERT id=" + task.JobId + " reason=import");
        LogJobImport(task);
        if (applyRecorded) PerfLog.Line($"IMPORT apply link found {task.JobId} {task.ApplicationPlatform}");
        IconCache.Collect(task);

        return new JobImportOutcome {
            Kind = JobImportKind.Imported, JobId = task.JobId, Title = task.Title,
            Company = task.Company, ApplicationStatus = task.ApplicationStatus,
            ApplyUrlRecorded = applyRecorded
        };
    }

    /// <summary>
    /// How a skipped job is named in diagnostics.log: the Jobright job id when the address carries
    /// one, otherwise scheme + host + path. Never a query string, never any job text.
    /// </summary>
    static string SafeJobRef(string? jobUrl) =>
        JobrightPageExtractor.JobIdFromUrl(jobUrl) ?? JobBrowser.SafeForLog(jobUrl);

    /// <summary>
    /// One line per imported or matched job. Query strings are dropped: they can carry a session token.
    /// </summary>
    static void LogJobImport(JobTask job) =>
        PerfLog.Line("JOB IMPORT id=" + job.JobId
            + " profile=" + ProfileContext.LogToken
            + " jobUrl=" + JobBrowser.SafeForLog(job.Link)
            + " applyUrl=" + JobBrowser.SafeForLog(job.ApplyUrl)
            + " platform=" + job.ApplicationPlatform);

    /// <summary>
    /// A new internal task id: "RB-yyyyMMdd-HHmmss-xxxxxxxx". The time makes it readable in a folder
    /// listing, the random part makes it collision-safe, and it is checked against the queue anyway.
    /// </summary>
    public static string NewInternalId(IEnumerable<JobTask> tasks) {
        var taken = new HashSet<string>(tasks.Select(t => t.JobId), StringComparer.OrdinalIgnoreCase);
        string id;
        do { id = $"RB-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..27]; }
        while (taken.Contains(id));
        return id;
    }

    static string UniqueDestination(string folder,string name) {
        var p=Path.Combine(folder,name); if(!File.Exists(p)) return p;
        var stem=Path.GetFileNameWithoutExtension(name); var ext=Path.GetExtension(name); int n=2;
        do { p=Path.Combine(folder,$"{stem}_{n++}{ext}"); } while(File.Exists(p));
        return p;
    }
}

public static class RequestPreparation {
 public static string PreparedPath => Path.Combine(Storage.DataDir,"prepared-request.json");
 /// <summary>Plain-text copy of the prepared input, so the request survives any clipboard failure.</summary>
 public static string PreparedTextPath => Path.Combine(Storage.DataDir,"prepared-request.txt");

 /// <summary>Persists the prepared request as JSON plus a plain-text sidecar. Used by every prepare path.</summary>
 public static void Save(PreparedRequest prepared) {
  Directory.CreateDirectory(Storage.DataDir);
  File.WriteAllText(PreparedPath,System.Text.Json.JsonSerializer.Serialize(prepared,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
  try { File.WriteAllText(PreparedTextPath,prepared.Text); } catch { /* the JSON copy is the authoritative one */ }
 }
 /// <summary>
 /// THE entry point for every prepare path (manual run, queue, retry). It chooses the prompt file by
 /// <see cref="AppSettings.PromptMode"/>; everything after it — clipboard, capture, fresh conversation,
 /// validation, documents — is identical for both modes, so no caller needs to know the mode.
 /// </summary>
 public static PreparedRequest Prepare(JobTask job, AppSettings settings) =>
  PromptModes.IsNormal(settings.PromptMode) ? PrepareNormal(job,settings) : PrepareResume(job,settings);

 /// <summary>Resume mode: the Master Prompt file. Its prepared text is pinned by a golden-fixture test.</summary>
 public static PreparedRequest PrepareResume(JobTask job, AppSettings settings) =>
  Build(job,settings,settings.MasterPrompt,resumeMode:true,
        "Configure an existing Master Prompt text file in Settings.");

 /// <summary>
 /// Normal mode: the user's own prompt file, plus the SAME job payload and the SAME output contract,
 /// so an arbitrary resume prompt still produces something the existing pipeline can consume.
 /// The user's text is never edited or reordered — it simply comes first.
 /// </summary>
 public static PreparedRequest PrepareNormal(JobTask job, AppSettings settings) =>
  Build(job,settings,settings.NormalPrompt,resumeMode:false,
        "Configure an existing Normal Prompt text file in Settings, or switch Prompt Mode back to Resume.");

 static PreparedRequest Build(JobTask job, AppSettings settings, string? promptPath, bool resumeMode, string missingPrompt) {
  if(string.IsNullOrWhiteSpace(promptPath)||!File.Exists(promptPath)) throw new InvalidOperationException(missingPrompt);
  var profilePath = !string.IsNullOrWhiteSpace(settings.CandidateProfile) && File.Exists(settings.CandidateProfile)
      ? settings.CandidateProfile : CandidateProfileStore.CandidateProfilePath;
  if(!File.Exists(profilePath)) throw new InvalidOperationException("The structured Candidate Profile has not been initialized yet. Create and save the baseline profile first.");
  if(BaselineProfileImporter.SameResumePath(profilePath, CandidateProfileStore.CandidateProfilePath))
      CandidateProfileStore.EnsureMatchesResume(settings.OriginalResume);
  var promptText = PromptConversion.RequireText(promptPath!);
  using var doc=System.Text.Json.JsonDocument.Parse(File.ReadAllText(profilePath));
  var profile=doc.RootElement;
  if(profile.ValueKind==System.Text.Json.JsonValueKind.Object && profile.TryGetProperty("profile",out var nested)) profile=nested;
  if(profile.ValueKind!=System.Text.Json.JsonValueKind.Object) throw new InvalidDataException("Candidate Profile JSON must contain a profile object (either at the root or under a top-level \"profile\" property).");
  var prepared=new PreparedRequest{JobId=job.JobId ?? "",Company=job.Company ?? "",Title=job.Title ?? "",
                                   PromptMode=resumeMode?PromptModes.Resume:PromptModes.Normal,
                                   Text=PromptContract.Assemble(promptText,PromptContract.JobPayloadText(job,profile),resumeMode)};
  Save(prepared);
  return prepared;
 }
 public static PreparedRequest? Load(){try{return File.Exists(PreparedPath)?System.Text.Json.JsonSerializer.Deserialize<PreparedRequest>(File.ReadAllText(PreparedPath)):null;}catch{return null;}}
}

// A6.6.7: the clipboard implementation now lives in Clipboard.cs (ClipboardService) so there is
// exactly one STA-aware, retrying implementation for the whole application.

public static class BaselineProfileImporter {
    public static string BaselineProfilePath => Path.Combine(Storage.DataDir, "baseline-profile.json");

    public static string ExtractDocxText(string path) {
        using var archive=System.IO.Compression.ZipFile.OpenRead(path);
        var entry=archive.GetEntry("word/document.xml") ?? throw new InvalidDataException("DOCX is missing word/document.xml.");
        using var stream=entry.Open();
        var doc=System.Xml.Linq.XDocument.Load(stream);
        System.Xml.Linq.XNamespace w="http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var paragraphs=doc.Descendants(w+"p").Select(p =>
            string.Concat(p.Descendants(w+"t").Select(t => t.Value)).Trim())
            .Where(x=>!string.IsNullOrWhiteSpace(x));
        return string.Join(Environment.NewLine,paragraphs);
    }

    public static void CreateBaselineFromDocx(string docxPath) {
        if(!File.Exists(docxPath)) throw new FileNotFoundException("Original Resume file was not found.",docxPath);
        if(Path.GetExtension(docxPath).ToLowerInvariant()!=".docx")
            throw new InvalidOperationException("Baseline import currently supports DOCX. Select your original .docx resume.");

        var text=ExtractDocxText(docxPath);
        if(string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("No readable text was found in the DOCX.");

        // This is intentionally a lossless baseline container, not fabricated structured resume data.
        // GPT will convert this source text to the required profile object in the next controlled step.
        // The experience list is the locked metadata, in source order, so a later merge does not
        // have to guess by job title.
        var experience=ExperienceSource.Parse(text);
        var baseline=new {
            schemaVersion="1.0",
            sourceFile=Path.GetFileName(docxPath),
            sourcePath=NormalizeResumePath(docxPath),
            importedAt=DateTime.Now,
            sourceText=text,
            experience
        };
        Directory.CreateDirectory(Storage.DataDir);
        File.WriteAllText(BaselineProfilePath,
            System.Text.Json.JsonSerializer.Serialize(baseline,new System.Text.Json.JsonSerializerOptions{
                WriteIndented=true,
                PropertyNamingPolicy=JsonNamingPolicy.CamelCase
            }));
    }

    /// <summary>Full path, so Settings and the imported baseline can be compared on Windows without case surprises.</summary>
    public static string NormalizeResumePath(string path) => Path.GetFullPath(path.Trim());

    public static bool SameResumePath(string? a, string? b) {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try {
            return string.Equals(NormalizeResumePath(a), NormalizeResumePath(b), StringComparison.OrdinalIgnoreCase);
        } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) {
            return false;
        }
    }

    /// <summary>The DOCX path stored by the last import, or null for a baseline written before sourcePath existed.</summary>
    public static string? ReadSourcePath() {
        if (!File.Exists(BaselineProfilePath)) return null;
        try {
            using var doc = JsonDocument.Parse(File.ReadAllText(BaselineProfilePath));
            if (doc.RootElement.TryGetProperty("sourcePath", out var path) && path.ValueKind == JsonValueKind.String)
                return path.GetString();
        } catch (JsonException) { }
        catch (IOException) { }
        return null;
    }

    public static string BuildProfileCreationRequest(string masterPromptPath) {
        if(!File.Exists(BaselineProfilePath)) throw new InvalidOperationException("Import the Original Resume first.");
        var baseline=File.ReadAllText(BaselineProfilePath);
        return
@"Create the initial candidate profile JSON from the original resume source below.
This is a baseline-import operation, not job tailoring.
Preserve factual information from the source. Do not invent employers, dates, schools, contact details, certifications, technologies, or metrics.
Use empty strings or arrays where the source does not support a value.

STRICT OUTPUT CONTRACT
Return the profile object itself. Do NOT wrap it in { ""profile"": ... }.
Return valid JSON only; do not add prose or comments.
Use EXACTLY this schema and field spelling:

{
  ""info"": {
    ""name"": """",
    ""title"": """",
    ""location"": """",
    ""email"": """",
    ""phone"": """",
    ""linkedin"": """"
  },
  ""summary"": """",
  ""skills"": [
    {
      ""category"": """",
      ""skills"": []
    }
  ],
  ""experience"": [
    {
      ""title"": """",
      ""company"": """",
      ""startDate"": """",
      ""endDate"": """",
      ""location"": """",
      ""employmentType"": """",
      ""workArrangement"": """",
      ""descriptionLines"": []
    }
  ],
  ""certifications"": [
    {
      ""name"": """",
      ""issuer"": """",
      ""date"": """"
    }
  ],
  ""education"": [
    {
      ""degree"": """",
      ""major"": """",
      ""school"": """",
      ""startDate"": """",
      ""endDate"": """"
    }
  ]
}

SCHEMA IS IMMUTABLE
The JSON template above is not an example. It is the exact serialization contract consumed by the application.
Do not rename, combine, omit, or invent properties. Do not convert arrays into objects.
For every source field that does not map directly, map its value into the canonical field or omit it; never preserve the source field name.

MANDATORY VALIDATION RULES
- The first six top-level properties must be info, summary, skills, experience, certifications, education.
- skills MUST begin with [ and be an array, never an object/dictionary keyed by category.
- Every skills item contains only category and skills; skills is an array of strings.
- Experience uses startDate and endDate, NEVER start_date or end_date.
- Experience uses employmentType and workArrangement. Copy them exactly from the source metadata line. A blank value stays blank.
- Experience company, startDate, endDate, and location are also copied exactly from the source. Title and descriptionLines may change. Do not drop or replace the locked fields.
- Experience uses descriptionLines, NEVER bullets.
- Do NOT output dates, start_date, end_date, bullets, employment_type, work_arrangement, work_mode, or institution.
- Education uses school, NEVER institution.
- Education uses startDate and endDate, NEVER start_date or end_date.
- Every education item includes major; use an empty string if the source does not establish it.
- Email and LinkedIn values must be plain text, NEVER Markdown links or mailto syntax.
- Do not add fields outside the schema.
- Before answering, internally verify info is an object, summary is a string, and skills, experience, certifications, and education are arrays.
- Before answering, search your draft for these forbidden property names and remove/convert every occurrence: dates, start_date, end_date, bullets, employment_type, work_arrangement, work_mode, institution.
- If your draft contains ""skills"": { then it is INVALID. Rewrite it as ""skills"": [ { ""category"": ""..."", ""skills"": [...] } ].
- Return the canonical JSON even if the source resume itself uses a different structure.

===== ORIGINAL RESUME SOURCE =====
"+baseline;
    }
}

/// <summary>
/// One experience record copied from the original resume, in source order.
/// Company, dates, location, employment type, and work arrangement are locked.
/// Title and bullets are not stored here because GPT may change them.
/// </summary>
public sealed class SourceExperience {
    public string Company { get; init; } = "";
    public string StartDate { get; init; } = "";
    public string EndDate { get; init; } = "";
    public string Location { get; init; } = "";
    public string EmploymentType { get; init; } = "";
    public string WorkArrangement { get; init; } = "";
}

/// <summary>
/// Reads locked experience metadata from the original resume text and writes it back
/// onto a generated profile by source order. Job title is never used as a match.
/// </summary>
public static class ExperienceSource {
    public static IReadOnlyList<SourceExperience> Parse(string? resumeText) {
        if (string.IsNullOrWhiteSpace(resumeText)) return Array.Empty<SourceExperience>();
        var lines = resumeText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var start = -1;
        for (var i = 0; i < lines.Length; i++) {
            if (!IsExperienceHeading(lines[i])) continue;
            start = i + 1;
            break;
        }
        if (start < 0) return Array.Empty<SourceExperience>();

        var jobs = new List<SourceExperience>();
        for (var i = start; i < lines.Length; i++) {
            var line = lines[i].Trim();
            if (line.Length == 0 || IsBullet(line)) continue;
            if (IsSectionEnd(line)) break;
            if (!TryHeading(line, out var company, out var startDate, out var endDate)) continue;

            var location = "";
            var employmentType = "";
            var workArrangement = "";
            if (i + 1 < lines.Length) {
                var next = lines[i + 1].Trim();
                if (next.Length > 0 && !IsBullet(next) && !IsSectionEnd(next)
                    && !TryHeading(next, out _, out _, out _)
                    && TryMetadata(next, out location, out employmentType, out workArrangement))
                    i++;
            }
            jobs.Add(new SourceExperience {
                Company = company,
                StartDate = startDate,
                EndDate = endDate,
                Location = location,
                EmploymentType = employmentType,
                WorkArrangement = workArrangement
            });
        }
        return jobs;
    }

    /// <summary>
    /// The experience list stored with the last resume import, or a fresh parse of its source text.
    /// Empty when no original resume has been imported.
    /// </summary>
    public static IReadOnlyList<SourceExperience> Load() {
        var path = BaselineProfileImporter.BaselineProfilePath;
        if (!File.Exists(path)) return Array.Empty<SourceExperience>();
        try {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.TryGetProperty("experience", out var stored) && stored.ValueKind == JsonValueKind.Array && stored.GetArrayLength() > 0)
                return ReadStored(stored);
            if (root.TryGetProperty("sourceText", out var text) && text.ValueKind == JsonValueKind.String)
                return Parse(text.GetString());
        } catch (JsonException) { }
        catch (IOException) { }
        return Array.Empty<SourceExperience>();
    }

    /// <summary>
    /// Copies locked fields onto the generated experience list when both lists have the same length.
    /// A different length is left alone so a shorter answer is not paired with the wrong employer.
    /// Title and description lines are not touched.
    /// </summary>
    public static void Restore(JsonObject profile, IReadOnlyList<SourceExperience> source) {
        if (source.Count == 0 || profile["experience"] is not JsonArray jobs || jobs.Count != source.Count) return;
        for (var i = 0; i < jobs.Count; i++) {
            if (jobs[i] is not JsonObject job) continue;
            var locked = source[i];
            job["company"] = locked.Company;
            job["startDate"] = locked.StartDate;
            job["endDate"] = locked.EndDate;
            job["location"] = locked.Location;
            job["employmentType"] = locked.EmploymentType;
            job["workArrangement"] = locked.WorkArrangement;
            PerfLog.Line("PROFILE experience-metadata-preserved company=" + locked.Company
                + " employmentType=" + locked.EmploymentType
                + " workArrangement=" + locked.WorkArrangement);
        }
    }

    /// <summary>
    /// Puts the locked source values on a resume block. Title and bullets stay as generated.
    /// Logs only when a locked value had to be put back.
    /// </summary>
    public static void Apply(ExperienceBlock block, SourceExperience locked) {
        var changed = block.Company != locked.Company
            || block.StartDate != locked.StartDate
            || block.EndDate != locked.EndDate
            || block.Location != locked.Location
            || block.EmploymentType != locked.EmploymentType
            || block.WorkArrangement != locked.WorkArrangement;
        block.Company = locked.Company;
        block.StartDate = locked.StartDate;
        block.EndDate = locked.EndDate;
        block.Location = locked.Location;
        block.EmploymentType = locked.EmploymentType;
        block.WorkArrangement = locked.WorkArrangement;
        if (changed)
            PerfLog.Line("PROFILE experience-metadata-preserved company=" + locked.Company
                + " employmentType=" + locked.EmploymentType
                + " workArrangement=" + locked.WorkArrangement);
    }

    static IReadOnlyList<SourceExperience> ReadStored(JsonElement array) {
        var jobs = new List<SourceExperience>();
        foreach (var item in array.EnumerateArray()) {
            if (item.ValueKind != JsonValueKind.Object) continue;
            jobs.Add(new SourceExperience {
                Company = Str(item, "company"),
                StartDate = Str(item, "startDate"),
                EndDate = Str(item, "endDate"),
                Location = Str(item, "location"),
                EmploymentType = Str(item, "employmentType"),
                WorkArrangement = Str(item, "workArrangement")
            });
        }
        return jobs;
    }

    static string Str(JsonElement item, string name) {
        foreach (var prop in item.EnumerateObject()) {
            if (!prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (prop.Value.ValueKind == JsonValueKind.String) return prop.Value.GetString()?.Trim() ?? "";
        }
        return "";
    }

    static bool IsExperienceHeading(string line) {
        var text = line.Trim();
        if (text.Length == 0 || text.Length > 40 || IsBullet(text)) return false;
        var lower = text.ToLowerInvariant();
        return lower.Contains("experience") || lower.Contains("employment") || lower.Contains("work history");
    }

    static bool IsSectionEnd(string line) {
        var text = line.Trim();
        if (text.Length == 0 || text.Length > 40 || IsBullet(text)) return false;
        var lower = text.ToLowerInvariant();
        return lower.Contains("education") || lower.Contains("certif") || lower.Contains("skill")
            || lower.Contains("project") || lower.Contains("award") || lower.Contains("publication");
    }

    static bool IsBullet(string line) {
        var i = 0;
        while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
        if (i >= line.Length) return false;
        var c = line[i];
        if (c is '•' or '·' or '●' or '◦' or '▪' or '▸' or '►' or '\u2023' or '\u2043' or '\u2219' or '\uF0B7')
            return true;
        return c is '-' or '*' or '–' or '—' && i + 1 < line.Length && line[i + 1] == ' ';
    }

    static bool TryHeading(string line, out string company, out string start, out string end) {
        company = "";
        start = "";
        end = "";
        var parts = SplitParts(line);
        if (parts.Count < 2) return false;
        if (!LooksLikeDateRange(parts[^1])) return false;
        company = parts[0];
        var dates = ProfileNormalizer.SplitDateRange(parts[^1]);
        start = dates.Start;
        end = dates.End;
        return company.Length > 0 && (start.Length > 0 || end.Length > 0);
    }

    /// <summary>
    /// "California, United States | Full-time | Hybrid", "Full-time | Hybrid", or "Contract | Remote".
    /// A part is classified by what it says. Anything left over is the location, which may be blank.
    /// </summary>
    static bool TryMetadata(string line, out string location, out string employmentType, out string workArrangement) {
        location = "";
        employmentType = "";
        workArrangement = "";
        var parts = SplitParts(line);
        if (parts.Count == 0 || LooksLikeDateRange(parts[^1])) return false;
        var places = new List<string>();
        var recognized = false;
        foreach (var part in parts) {
            if (employmentType.Length == 0 && IsEmploymentType(part)) { employmentType = part; recognized = true; continue; }
            if (workArrangement.Length == 0 && IsWorkArrangement(part)) { workArrangement = part; recognized = true; continue; }
            places.Add(part);
        }
        if (!recognized) {
            if (parts.Count != 1 || line.Length > 80) return false;
            location = parts[0];
            return true;
        }
        location = string.Join(" | ", places);
        return true;
    }

    static List<string> SplitParts(string line) =>
        line.Split('|').Select(part => part.Trim()).Where(part => part.Length > 0).ToList();

    static bool LooksLikeDateRange(string value) =>
        value.Any(char.IsDigit)
        || value.Equals("present", StringComparison.OrdinalIgnoreCase)
        || value.Equals("current", StringComparison.OrdinalIgnoreCase);

    static string Fold(string value) =>
        string.Join(' ', value.Trim().ToLowerInvariant().Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries));

    static bool IsEmploymentType(string value) {
        var key = Fold(value);
        return key is "full time" or "part time" or "contract" or "contractor" or "internship" or "intern"
            or "temporary" or "freelance" or "permanent" or "consultant";
    }

    static bool IsWorkArrangement(string value) {
        var key = Fold(value);
        return key is "remote" or "hybrid" or "on site" or "in office" or "in person";
    }
}

/// <summary>Read-only review text. It is not written back to candidate-profile.json.</summary>
public sealed class ProfileReviewText {
    public string Skills { get; init; } = "None";
    public string Experience { get; init; } = "None";
    public string Education { get; init; } = "None";
    public string Certifications { get; init; } = "None";
}

/// <summary>The contact and summary values Settings can edit. Every other profile field stays as stored.</summary>
public sealed class EditableProfileFields {
    public string Name { get; init; } = "";
    public string Title { get; init; } = "";
    public string Location { get; init; } = "";
    public string Email { get; init; } = "";
    public string Phone { get; init; } = "";
    public string Linkedin { get; init; } = "";
    public string Summary { get; init; } = "";
}

public static class CandidateProfileStore {
    public static string CandidateProfilePath => ProfileContext.CandidateProfilePath;

    /// <summary>
    /// Which resume produced candidate-profile.json. Kept outside that file so a contact-field edit
    /// and the profile schema cannot change it. Missing means the profile is from an unknown resume.
    /// </summary>
    public static string SourceMarkerPath => Path.Combine(Storage.DataDir, "candidate-profile.source.json");

    public const string StaleProfileMessage =
        "Candidate profile is based on a different resume. Import the current resume and regenerate the candidate profile before preparing jobs.";

    /// <summary>
    /// Refuses job preparation when candidate-profile.json was not saved from the resume now selected
    /// in Settings. Does not delete the profile.
    /// </summary>
    public static void EnsureMatchesResume(string? originalResume) {
        if (!SameSource(ReadRecordedSourcePath(), originalResume))
            throw new InvalidOperationException(StaleProfileMessage);
    }

    /// <summary>
    /// Called only after a BASELINE answer replaces candidate-profile.json. Copies sourcePath from
    /// baseline-profile.json. A contact-field save must not call this.
    /// </summary>
    public static void RecordSourceFromBaseline() {
        var source = BaselineProfileImporter.ReadSourcePath();
        if (string.IsNullOrWhiteSpace(source)) {
            try { if (File.Exists(SourceMarkerPath)) File.Delete(SourceMarkerPath); } catch (IOException) { }
            return;
        }
        Directory.CreateDirectory(Storage.DataDir);
        File.WriteAllText(SourceMarkerPath,
            JsonSerializer.Serialize(new { sourcePath = source }, new JsonSerializerOptions { WriteIndented = true }));
    }

    static string? ReadRecordedSourcePath() {
        if (!File.Exists(SourceMarkerPath)) return null;
        try {
            using var doc = JsonDocument.Parse(File.ReadAllText(SourceMarkerPath));
            if (doc.RootElement.TryGetProperty("sourcePath", out var path) && path.ValueKind == JsonValueKind.String)
                return path.GetString();
        } catch (JsonException) { }
        catch (IOException) { }
        return null;
    }

    static bool SameSource(string? recorded, string? originalResume) =>
        BaselineProfileImporter.SameResumePath(recorded, originalResume);

    /// <summary>
    /// Reads the editable contact and summary fields from candidate-profile.json.
    /// Returns null when the file does not exist. A file that cannot be read is also null, with <paramref name="error"/> set.
    /// </summary>
    public static EditableProfileFields? LoadEditableFields(out string? error) {
        error = null;
        if (!File.Exists(CandidateProfilePath)) return null;
        try {
            var profile = ProfileObject(ProfileNormalizer.Parse(File.ReadAllText(CandidateProfilePath)));
            var info = profile["info"] as JsonObject;
            return new EditableProfileFields {
                Name = ReadString(info, "name"),
                Title = ReadString(info, "title"),
                Location = ReadString(info, "location"),
                Email = ReadString(info, "email"),
                Phone = ReadString(info, "phone"),
                Linkedin = ReadString(info, "linkedin"),
                Summary = ReadString(profile, "summary")
            };
        } catch (Exception ex) {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Read-only text for the four sections Settings does not edit. Empty sections read as "None".
    /// Does not write the profile.
    /// </summary>
    public static ProfileReviewText LoadReviewText() {
        var profile = ProfileObject(ProfileNormalizer.Parse(File.ReadAllText(CandidateProfilePath)));
        return new ProfileReviewText {
            Skills = FormatSkills(profile["skills"] as JsonArray),
            Experience = FormatExperience(profile["experience"] as JsonArray),
            Education = FormatEducation(profile["education"] as JsonArray),
            Certifications = FormatCertifications(profile["certifications"] as JsonArray)
        };
    }

    static string FormatSkills(JsonArray? items) {
        if (items is null || items.Count == 0) return "None";
        var blocks = new List<string>();
        foreach (var item in items) {
            if (item is not JsonObject o) continue;
            var category = ReadString(o, "category");
            var names = new List<string>();
            if (o["skills"] is JsonArray skills) {
                foreach (var skill in skills) {
                    var name = SkillName(skill);
                    if (name.Length > 0) names.Add(name);
                }
            }
            var line = string.Join(", ", names);
            if (category.Length == 0 && line.Length == 0) continue;
            blocks.Add(category.Length == 0 ? line : (line.Length == 0 ? category : category + "\n" + line));
        }
        return blocks.Count == 0 ? "None" : string.Join("\n\n", blocks);
    }

    static string SkillName(JsonNode? skill) {
        if (skill is JsonValue value && value.TryGetValue<string>(out var text)) return text.Trim();
        if (skill is JsonObject o) return ReadString(o, "name");
        return "";
    }

    static string FormatExperience(JsonArray? items) {
        if (items is null || items.Count == 0) return "None";
        var blocks = new List<string>();
        foreach (var item in items) {
            if (item is not JsonObject o) continue;
            var heading = JoinParts(" | ",
                ReadString(o, "title"),
                ReadString(o, "company"),
                JoinParts(" - ", ReadString(o, "startDate"), ReadString(o, "endDate")));
            var lines = new List<string>();
            if (heading.Length > 0) lines.Add(heading);
            var meta = JoinParts(" | ",
                ReadString(o, "location"),
                ReadString(o, "employmentType"),
                ReadString(o, "workArrangement"));
            if (meta.Length > 0) lines.Add(meta);
            if (o["descriptionLines"] is JsonArray descriptions) {
                foreach (var line in descriptions) {
                    var text = DescriptionText(line);
                    if (text.Length > 0) lines.Add("• " + text);
                }
            }
            if (lines.Count > 0) blocks.Add(string.Join("\n", lines));
        }
        return blocks.Count == 0 ? "None" : string.Join("\n\n", blocks);
    }

    static string FormatEducation(JsonArray? items) {
        if (items is null || items.Count == 0) return "None";
        var blocks = new List<string>();
        foreach (var item in items) {
            if (item is not JsonObject o) continue;
            var degree = JoinParts(" ", ReadString(o, "degree"), ReadString(o, "major"));
            var line = JoinParts(" | ",
                degree,
                ReadString(o, "school"),
                JoinParts(" - ", ReadString(o, "startDate"), ReadString(o, "endDate")));
            if (line.Length > 0) blocks.Add(line);
        }
        return blocks.Count == 0 ? "None" : string.Join("\n", blocks);
    }

    static string FormatCertifications(JsonArray? items) {
        if (items is null || items.Count == 0) return "None";
        var blocks = new List<string>();
        foreach (var item in items) {
            if (item is JsonValue value && value.TryGetValue<string>(out var text) && text.Trim().Length > 0) {
                blocks.Add(text.Trim());
                continue;
            }
            if (item is not JsonObject o) continue;
            var line = JoinParts(" | ", ReadString(o, "name"), ReadString(o, "issuer"), ReadString(o, "date"));
            if (line.Length > 0) blocks.Add(line);
        }
        return blocks.Count == 0 ? "None" : string.Join("\n", blocks);
    }

    static string DescriptionText(JsonNode? line) {
        if (line is JsonValue value && value.TryGetValue<string>(out var text)) return text.Trim();
        if (line is not JsonObject o || o["segments"] is not JsonArray segments) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var segment in segments)
            if (segment is JsonObject so) sb.Append(ReadString(so, "text"));
        return sb.ToString().Trim();
    }

    static string JoinParts(string separator, params string[] parts) {
        var kept = new List<string>();
        foreach (var part in parts)
            if (!string.IsNullOrWhiteSpace(part)) kept.Add(part.Trim());
        return string.Join(separator, kept);
    }

    /// <summary>
    /// Updates only info contact fields and summary, then runs <see cref="Validate"/> before replacing the file.
    /// Skills, experience, education, certifications, style, and every other property are left as they were.
    /// Does not update the source-resume marker. Throws without writing when validation fails.
    /// </summary>
    public static void SaveEditableFields(EditableProfileFields fields) {
        if (!File.Exists(CandidateProfilePath))
            throw new InvalidOperationException("Create the candidate profile before editing it.");
        var root = ProfileNormalizer.Parse(File.ReadAllText(CandidateProfilePath));
        var profile = ProfileObject(root);
        if (profile["info"] is not JsonObject info) {
            info = new JsonObject();
            profile["info"] = info;
        }
        info["name"] = fields.Name ?? "";
        info["title"] = fields.Title ?? "";
        info["location"] = fields.Location ?? "";
        info["email"] = fields.Email ?? "";
        info["phone"] = fields.Phone ?? "";
        info["linkedin"] = fields.Linkedin ?? "";
        profile["summary"] = fields.Summary ?? "";

        var canonical = ProfileNormalizer.ToCanonicalJson(root);
        using (var doc = JsonDocument.Parse(canonical))
            Validate(ProfileElement(doc.RootElement));
        File.WriteAllText(CandidateProfilePath, canonical);
    }

    static JsonObject ProfileObject(JsonNode root) {
        if (root is not JsonObject obj)
            throw new InvalidDataException("candidate-profile.json must contain a JSON object.");
        if (obj["info"] is null && obj["profile"] is JsonObject nested) return nested;
        return obj;
    }

    static JsonElement ProfileElement(JsonElement root) {
        if (root.ValueKind == JsonValueKind.Object && !root.TryGetProperty("info", out _)
            && root.TryGetProperty("profile", out var nested) && nested.ValueKind == JsonValueKind.Object)
            return nested;
        return root;
    }

    static string ReadString(JsonObject? obj, string name) {
        if (obj?[name] is JsonValue value && value.TryGetValue<string>(out var text)) return text;
        return "";
    }

    public static string ExtractJson(string text) {
        if(string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("ChatGPT result is empty.");
        var t=text.Trim();
        var fenceStart=t.IndexOf("```",StringComparison.Ordinal);
        if(fenceStart>=0) {
            var firstNewline=t.IndexOf('\n',fenceStart);
            var fenceEnd=t.IndexOf("```",firstNewline>=0?firstNewline+1:fenceStart+3,StringComparison.Ordinal);
            if(firstNewline>=0 && fenceEnd>firstNewline) t=t.Substring(firstNewline+1,fenceEnd-firstNewline-1).Trim();
        }
        var a=t.IndexOf('{'); var b=t.LastIndexOf('}');
        if(a<0||b<=a) throw new InvalidDataException("No JSON object was found in the ChatGPT result.");
        // A code block's visible line wrap is a raw newline inside a JSON string. That is not
        // legal JSON, so a finished answer was rejected and the same request was sent again.
        return RepairRawBreaksInsideStrings(t.Substring(a,b-a+1));
    }

    /// <summary>
    /// Removes line breaks that a rendered code block inserts inside JSON strings.
    /// A break sitting against the closing quote is dropped. Any other break becomes a space.
    /// </summary>
    static string RepairRawBreaksInsideStrings(string json) {
        var sb=new System.Text.StringBuilder(json.Length);
        var inString=false;
        var escape=false;
        for(var i=0;i<json.Length;i++) {
            var c=json[i];
            if(!inString) {
                if(c=='"') inString=true;
                sb.Append(c);
                continue;
            }
            if(escape) { sb.Append(c); escape=false; continue; }
            if(c=='\\') { sb.Append(c); escape=true; continue; }
            if(c=='"') { inString=false; sb.Append(c); continue; }
            if(c=='\r') continue;
            if(c=='\n') {
                var j=i+1;
                while(j<json.Length && (json[j]==' ' || json[j]=='\t' || json[j]=='\r' || json[j]=='\n')) j++;
                if(j<json.Length && json[j]=='"') continue;
                sb.Append(' ');
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Normalizes the AI response into the canonical schema, validates the normalized result, then saves it.
    /// Known AI output variations (skills as an object, bullets, dates, institution, start_date/end_date,
    /// Markdown contact links, extra non-schema fields) are converted instead of rejected.
    /// </summary>
    public static NormalizationReport NormalizeAndSave(string resultText) => NormalizeAndSaveTo(resultText,CandidateProfilePath);

    /// <summary>
    /// Same pipeline, writing to a caller-chosen file. A6.6.8 uses this so a tailored per-job result
    /// lands in results\&lt;jobId&gt;.json while candidate-profile.json stays the baseline that every
    /// future job is tailored from.
    /// </summary>
    public static NormalizationReport NormalizeAndSaveTo(string resultText,string targetPath,bool requireStyle=false,IReadOnlyList<SourceExperience>? lockedExperience=null) {
        var json=ExtractJson(resultText);
        System.Text.Json.Nodes.JsonNode root;
        try { root=ProfileNormalizer.Parse(json); }
        catch(System.Text.Json.JsonException ex) { throw new InvalidDataException("The result is not valid JSON: "+ex.Message); }

        var report=ProfileNormalizer.Normalize(root);
        if(lockedExperience is { Count: > 0 })
            ExperienceSource.Restore(report.Profile, lockedExperience);

        // Normal Prompt mode: the answer must carry its own style. Checked before anything is saved,
        // so a style-less answer writes nothing and goes down the ordinary invalid-output path.
        // Resume mode passes false and keeps the promV4.12 fallback exactly as before.
        if(requireStyle && !report.StyleSupplied) throw new InvalidDataException(MissingStyleMessage);
        var canonical=ProfileNormalizer.ToCanonicalJson(report.Profile);
        using(var doc=System.Text.Json.JsonDocument.Parse(canonical)) Validate(doc.RootElement);

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? Storage.DataDir);
        File.WriteAllText(targetPath,canonical);
        return report;
    }

    /// <summary>Why a Normal Prompt answer without a style object is refused.</summary>
    public const string MissingStyleMessage="Normal Prompt mode requires style: the answer has no top-level style object.";

    /// <summary>Kept for callers that only need pass/fail behaviour.</summary>
    public static void ValidateAndSave(string resultText) => NormalizeAndSave(resultText);

    /// <summary>Strict canonical-schema check, run against the normalized profile.</summary>
    public static void Validate(System.Text.Json.JsonElement profile) {
        if(profile.ValueKind!=System.Text.Json.JsonValueKind.Object)
            throw new InvalidDataException("Result must be a JSON object.");

        var errors=new List<string>();
        string[] required={"info","summary","skills","experience","certifications","education"};
        foreach(var name in required)
            if(!profile.TryGetProperty(name,out _)) errors.Add($"Missing top-level field: {name}");

        if(profile.TryGetProperty("info",out var info) && info.ValueKind!=System.Text.Json.JsonValueKind.Object) errors.Add("info must be an object");
        if(profile.TryGetProperty("summary",out var summary) && summary.ValueKind!=System.Text.Json.JsonValueKind.String) errors.Add("summary must be a string");
        foreach(var name in new[]{"skills","experience","certifications","education"})
            if(profile.TryGetProperty(name,out var value) && value.ValueKind!=System.Text.Json.JsonValueKind.Array) errors.Add($"{name} must be an array");

        if(profile.TryGetProperty("skills",out var skillsRoot) && skillsRoot.ValueKind==System.Text.Json.JsonValueKind.Array) {
            int i=0; foreach(var item in skillsRoot.EnumerateArray()) {
                if(item.ValueKind!=System.Text.Json.JsonValueKind.Object) errors.Add($"skills[{i}] must be an object");
                else {
                    if(!item.TryGetProperty("category",out var cat) || cat.ValueKind!=System.Text.Json.JsonValueKind.String) errors.Add($"skills[{i}].category is missing or not a string");
                    if(!item.TryGetProperty("skills",out var vals) || vals.ValueKind!=System.Text.Json.JsonValueKind.Array) errors.Add($"skills[{i}].skills is missing or not an array");
                } i++;
            }
        }

        if(profile.TryGetProperty("experience",out var expRoot) && expRoot.ValueKind==System.Text.Json.JsonValueKind.Array) {
            int i=0; foreach(var item in expRoot.EnumerateArray()) {
                if(item.ValueKind!=System.Text.Json.JsonValueKind.Object) { errors.Add($"experience[{i}] must be an object"); i++; continue; }
                foreach(var f in new[]{"title","company","startDate","endDate","location","descriptionLines"}) if(!item.TryGetProperty(f,out _)) errors.Add($"experience[{i}].{f} is missing");
                foreach(var f in new[]{"dates","start_date","end_date","bullets","employment_type","work_arrangement","work_mode"}) if(item.TryGetProperty(f,out _)) errors.Add($"experience[{i}].{f} is not allowed");
                if(item.TryGetProperty("descriptionLines",out var dl)) {
                    if(dl.ValueKind!=System.Text.Json.JsonValueKind.Array) errors.Add($"experience[{i}].descriptionLines must be an array");
                    else ValidateDescriptionLines(dl,i,errors);
                }
                i++;
            }
        }

        if(profile.TryGetProperty("education",out var eduRoot) && eduRoot.ValueKind==System.Text.Json.JsonValueKind.Array) {
            int i=0; foreach(var item in eduRoot.EnumerateArray()) {
                if(item.ValueKind!=System.Text.Json.JsonValueKind.Object) { errors.Add($"education[{i}] must be an object"); i++; continue; }
                foreach(var f in new[]{"degree","major","school","startDate","endDate"}) if(!item.TryGetProperty(f,out _)) errors.Add($"education[{i}].{f} is missing");
                foreach(var f in new[]{"institution","dates","start_date","end_date"}) if(item.TryGetProperty(f,out _)) errors.Add($"education[{i}].{f} is not allowed");
                i++;
            }
        }

        // Style system: the optional style block, checked against the same limits the normalizer clamps to.
        if(profile.TryGetProperty("style",out var style)) {
            if(style.ValueKind!=System.Text.Json.JsonValueKind.Object) errors.Add("style must be an object");
            else errors.AddRange(StyleValidator.Validate(System.Text.Json.Nodes.JsonNode.Parse(style.GetRawText())));
        }

        if(errors.Count>0) throw new InvalidDataException("Profile schema errors:\n\n• "+string.Join("\n• ",errors.Take(30))+(errors.Count>30?$"\n• ...and {errors.Count-30} more":""));
    }

    /// <summary>
    /// Style system: a description line is either plain text or a segmented line carrying inline emphasis.
    /// Markdown is never accepted in resume text — emphasis is structural, so it cannot be mistaken
    /// for content.
    /// </summary>
    static void ValidateDescriptionLines(System.Text.Json.JsonElement lines,int experienceIndex,List<string> errors) {
        int i=0;
        foreach(var line in lines.EnumerateArray()) {
            var path=$"experience[{experienceIndex}].descriptionLines[{i}]";
            if(line.ValueKind==System.Text.Json.JsonValueKind.String) { i++; continue; }
            if(line.ValueKind!=System.Text.Json.JsonValueKind.Object) {
                errors.Add($"{path} must be a string or a {{ \"segments\": [...] }} object");
                i++; continue;
            }
            if(!line.TryGetProperty("segments",out var segments) || segments.ValueKind!=System.Text.Json.JsonValueKind.Array) {
                errors.Add($"{path}.segments is missing or not an array");
                i++; continue;
            }
            int s=0;
            foreach(var segment in segments.EnumerateArray()) {
                var segmentPath=$"{path}.segments[{s}]";
                if(segment.ValueKind!=System.Text.Json.JsonValueKind.Object) errors.Add($"{segmentPath} must be an object");
                else {
                    if(!segment.TryGetProperty("text",out var text) || text.ValueKind!=System.Text.Json.JsonValueKind.String)
                        errors.Add($"{segmentPath}.text is missing or not a string");
                    if(segment.TryGetProperty("bold",out var bold)
                       && bold.ValueKind!=System.Text.Json.JsonValueKind.True && bold.ValueKind!=System.Text.Json.JsonValueKind.False)
                        errors.Add($"{segmentPath}.bold must be true or false");
                }
                s++;
            }
            i++;
        }
    }

    /// <summary>
    /// The documented contract check: content and style together, reported as a list instead of an
    /// exception. Used by the tests and by anyone checking an AI answer before it is rendered.
    /// Unlike the capture pipeline, nothing here is repaired — an out-of-range style is an error.
    /// </summary>
    public static List<string> ValidateResumeJson(string json) {
        var errors=new List<string>();
        System.Text.Json.JsonDocument doc;
        try { doc=System.Text.Json.JsonDocument.Parse(json,new System.Text.Json.JsonDocumentOptions{AllowTrailingCommas=true,CommentHandling=System.Text.Json.JsonCommentHandling.Skip}); }
        catch(System.Text.Json.JsonException ex) { errors.Add("The resume JSON could not be parsed: "+ex.Message); return errors; }

        using(doc) {
            try { Validate(doc.RootElement); }
            catch(InvalidDataException ex) {
                foreach(var line in ex.Message.Split('\n')) {
                    var trimmed=line.TrimStart().TrimStart('•').Trim();
                    if(trimmed.Length>0 && !trimmed.StartsWith("Profile schema errors",StringComparison.Ordinal)) errors.Add(trimmed);
                }
            }
        }
        return errors;
    }
}
