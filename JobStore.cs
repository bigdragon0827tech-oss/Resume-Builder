using System.Diagnostics;
using System.Globalization;
#pragma warning disable CS0105 // The style-test project already imports System.IO globally.
using System.IO;
#pragma warning restore CS0105
using Microsoft.Data.Sqlite;

namespace ResumeBuilder;

/// <summary>
/// Canonical runtime store for jobs, applications and generated resume files.
/// The internal job id is the only identity. Company, title and folder name are never a key.
/// Email tasks, settings, the candidate profile and licensing are not stored here.
/// </summary>
public static class JobStore {
    public const int SchemaVersion = 2;
    public const string FileName = "resumebuilder.db";

    /// <summary>Tests point this at a temporary file. The app leaves it unset.</summary>
    public static string? DatabasePathOverride { get; set; }

    /// <summary>How many empty ApplyUrl values the last <see cref="ApplyResumeLinks"/> filled from SQLite.</summary>
    public static int LastApplyUrlsFilled { get; private set; }

    public static string DatabasePath =>
        DatabasePathOverride ?? ProfileContext.DatabasePath;

    /// <summary>Creates the schema at <paramref name="databasePath"/> without switching the open profile.</summary>
    public static MigrationReport EnsureReadyAt(string databasePath) {
        var previous = DatabasePathOverride;
        DatabasePathOverride = databasePath;
        try { return EnsureReady(); }
        finally { DatabasePathOverride = previous; }
    }

    /// <summary>
    /// Counts directory listings performed by the one-time resume migration.
    /// Applications loading must leave this unchanged.
    /// </summary>
    public static int DirectoryEnumerations { get; private set; }

    public static void ResetDirectoryEnumerations() => DirectoryEnumerations = 0;

    public static MigrationReport EnsureReady() {
        try {
            using var conn = Open();
            var counts = Counts(conn);
            return new MigrationReport {
                AlreadyComplete = true,
                Jobs = counts.Jobs,
                Applications = counts.Applications,
                ResumeOutputs = counts.Outputs
            };
        } catch (Exception ex) {
            PerfLog.Line("DB MIGRATION failed " + ex.GetType().Name);
            return new MigrationReport { Failed = true };
        }
    }

    /// <summary>Schema only. Jobs are not read from a task file and resume folders are not scanned.</summary>
    public static MigrationReport MigrateIfNeeded() => EnsureReady();

    /// <summary>
    /// Schema only. <paramref name="tasks"/> and <paramref name="resumeRoot"/> are ignored so a
    /// task file can never rebuild the database.
    /// </summary>
    public static MigrationReport MigrateIfNeeded(IReadOnlyList<JobTask> tasks, string? resumeRoot) => EnsureReady();

    /// <summary>
    /// Replaces the job and application rows with <paramref name="tasks"/>.
    /// Resume outputs for a job that is still present are kept. Removed jobs cascade away.
    /// </summary>
    public static void SyncTasks(IEnumerable<JobTask> tasks) {
        var list = tasks.ToList();
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using (var reset = conn.CreateCommand()) {
            reset.Transaction = tx;
            reset.CommandText = "CREATE TEMP TABLE IF NOT EXISTS JobStoreKeep (Id TEXT PRIMARY KEY); DELETE FROM JobStoreKeep;";
            reset.ExecuteNonQuery();
        }
        using var keep = conn.CreateCommand();
        keep.Transaction = tx;
        keep.CommandText = "INSERT OR IGNORE INTO JobStoreKeep (Id) VALUES ($id);";
        keep.Parameters.Add("$id", SqliteType.Text);
        foreach (var job in list) {
            if (string.IsNullOrWhiteSpace(job.JobId)) continue;
            UpsertJob(conn, tx, job, keepJobOnExternalConflict: true);
            UpsertApplication(conn, tx, job);
            WriteStoredResume(conn, tx, job);
            keep.Parameters["$id"].Value = job.JobId.Trim();
            keep.ExecuteNonQuery();
        }
        using (var delete = conn.CreateCommand()) {
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM Jobs WHERE Id NOT IN (SELECT Id FROM JobStoreKeep);";
            delete.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Replaces the stored jobs with <paramref name="tasks"/> in one transaction.</summary>
    public static void SaveJobs(IEnumerable<JobTask> tasks) => SyncTasks(tasks);

    /// <summary>Every job, its application and its latest resume, in one query.</summary>
    public static List<JobTask> GetJobs() {
        var sw = Stopwatch.StartNew();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            WITH latest AS (
                SELECT JobId, DocxPath, PdfPath, OutputFolder, CreatedAt, Id,
                       ROW_NUMBER() OVER (PARTITION BY JobId ORDER BY CreatedAt DESC, Id DESC) AS n
                FROM ResumeOutputs
            )
            SELECT j.Id, j.Source, j.ExternalJobId, j.Company, j.Title, j.Location, j.JobDescription,
                   j.JobUrl, j.CompanyUrl, j.ApplyUrl, j.ApplyUrlCapturedAt, j.ApplicationPlatform,
                   j.JobSite, j.QueueStatus, j.FailureReason, j.CreatedAt, j.UpdatedAt,
                   a.Status, a.ViewedAt, a.ReadyAt, a.AppliedAt, a.InterviewAt, a.FailedAt, a.DoneAt, a.FailedFrom,
                   l.DocxPath, l.PdfPath
            FROM Jobs j
            LEFT JOIN Applications a ON a.JobId = j.Id
            LEFT JOIN latest l ON l.JobId = j.Id AND l.n = 1
            ORDER BY j.CreatedAt;
            """;
        var jobs = new List<JobTask>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) {
            var job = new JobTask {
                JobId = reader.GetString(0),
                Source = Text(reader, 1),
                Company = Text(reader, 3),
                Title = Text(reader, 4),
                Location = Text(reader, 5),
                Jd = Text(reader, 6),
                Link = Text(reader, 7),
                CompanyUrl = Text(reader, 8),
                ApplyUrl = Text(reader, 9),
                ApplyUrlCapturedAt = OptionalTime(reader, 10),
                CreatedAt = RequiredTime(reader, 15),
                UpdatedAt = RequiredTime(reader, 16),
                ViewedAt = OptionalTime(reader, 18),
                ReadyAt = OptionalTime(reader, 19),
                AppliedAt = OptionalTime(reader, 20),
                InterviewAt = OptionalTime(reader, 21),
                FailedAt = OptionalTime(reader, 22),
                DoneAt = OptionalTime(reader, 23),
                FailedFrom = Text(reader, 24)
            };
            var platform = Text(reader, 11);
            if (Enum.TryParse<ApplicationPlatform>(platform, out var parsed)) job.ApplicationPlatform = parsed;
            var queue = Text(reader, 13);
            job.Status = queue.Length == 0 ? "Queued" : queue;
            var failure = Text(reader, 14);
            if (failure.Length > 0) job.FailureReason = failure;
            var application = Text(reader, 17);
            if (application.Length > 0) job.ApplicationStatus = application;
            var docx = Text(reader, 25);
            var pdf = Text(reader, 26);
            job.ResumePath = docx.Length > 0 ? docx : pdf;
            jobs.Add(job);
        }
        PerfLog.Line("APPLICATIONS QUERY count=" + jobs.Count + " ms=" + sw.ElapsedMilliseconds);
        return jobs;
    }

    /// <summary>One job and its application. Returns false when Source + ExternalJobId is already taken.</summary>
    public static bool TryAddJob(JobTask job) {
        if (string.IsNullOrWhiteSpace(job.JobId)) return false;
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        if (!UpsertJob(conn, tx, job, keepJobOnExternalConflict: false)) return false;
        UpsertApplication(conn, tx, job);
        tx.Commit();
        return true;
    }

    public static void SyncJob(JobTask job) {
        if (string.IsNullOrWhiteSpace(job.JobId) || TryAddJob(job)) return;
        // The external id belongs to another job. Keep this job, without taking that id.
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        UpsertJob(conn, tx, job, keepJobOnExternalConflict: true);
        UpsertApplication(conn, tx, job);
        tx.Commit();
    }

    /// <summary>
    /// Writes this job and its application onto the existing rows when the id is already stored.
    /// Does not delete any other job and does not write tasks.json.
    /// </summary>
    public static bool UpsertTracked(JobTask job, string reason) {
        if (job is null || string.IsNullOrWhiteSpace(job.JobId)) return false;
        var id = job.JobId.Trim();
        try {
            SyncJob(job);
            PerfLog.Line("JOB UPSERT id=" + id + " reason=" + reason);
            PerfLog.Line("APPLICATION UPSERT id=" + id);
            return true;
        } catch (Exception ex) {
            PerfLog.Line("JOBSTORE sync failed " + ex.GetType().Name);
            return false;
        }
    }

    /// <summary>
    /// The latest resume for this job. The first successful generation inserts a row;
    /// a later generation updates that latest row's paths.
    /// </summary>
    public static bool TryLatestResume(string jobId, out string docx, out string pdf, out string folder) {
        docx = "";
        pdf = "";
        folder = "";
        jobId = (jobId ?? "").Trim();
        if (jobId.Length == 0) return false;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DocxPath, PdfPath, OutputFolder
            FROM ResumeOutputs
            WHERE JobId = $id
            ORDER BY CreatedAt DESC, Id DESC
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", jobId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return false;
        docx = Text(reader, 0);
        pdf = Text(reader, 1);
        folder = Text(reader, 2);
        return true;
    }

    public static void RecordResumeOutput(string jobId, string? docxPath, string? pdfPath, string? outputFolder) {
        jobId = (jobId ?? "").Trim();
        if (jobId.Length == 0) return;
        if (string.IsNullOrWhiteSpace(docxPath) && string.IsNullOrWhiteSpace(pdfPath)) return;
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        WriteResumeOutput(conn, tx, jobId, docxPath, pdfPath, outputFolder);
        tx.Commit();
        PerfLog.Line("RESUME OUTPUT UPSERT id=" + jobId);
    }

    /// <summary>
    /// Saves the job, its application and the new resume in one transaction.
    /// A second call updates those same rows.
    /// </summary>
    public static void CommitResume(JobTask job, string? docxPath, string? pdfPath, string? outputFolder) {
        if (job is null || string.IsNullOrWhiteSpace(job.JobId)) return;
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        UpsertJob(conn, tx, job, keepJobOnExternalConflict: true);
        UpsertApplication(conn, tx, job);
        if (!string.IsNullOrWhiteSpace(docxPath) || !string.IsNullOrWhiteSpace(pdfPath))
            WriteResumeOutput(conn, tx, job.JobId.Trim(), docxPath, pdfPath, outputFolder);
        tx.Commit();
        PerfLog.Line("RESUME OUTPUT UPSERT id=" + job.JobId.Trim());
    }

    static void WriteResumeOutput(SqliteConnection conn, SqliteTransaction tx, string jobId, string? docxPath, string? pdfPath, string? outputFolder) {
        var folder = string.IsNullOrWhiteSpace(outputFolder)
            ? Path.GetDirectoryName(string.IsNullOrWhiteSpace(docxPath) ? pdfPath : docxPath)
            : outputFolder;
        var created = Stamp(DateTime.Now);
        string? latest = null;
        using (var find = conn.CreateCommand()) {
            find.Transaction = tx;
            find.CommandText = "SELECT Id FROM ResumeOutputs WHERE JobId = $job ORDER BY CreatedAt DESC, Id DESC LIMIT 1;";
            find.Parameters.AddWithValue("$job", jobId);
            latest = find.ExecuteScalar() as string;
        }
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        if (latest is null) {
            cmd.CommandText = """
                INSERT INTO ResumeOutputs (Id, JobId, DocxPath, PdfPath, OutputFolder, CreatedAt)
                VALUES ($id, $job, $docx, $pdf, $folder, $created);
                """;
            cmd.Parameters.AddWithValue("$id", "out:" + Guid.NewGuid().ToString("N"));
        } else {
            cmd.CommandText = """
                UPDATE ResumeOutputs
                SET DocxPath = COALESCE($docx, DocxPath),
                    PdfPath = COALESCE($pdf, PdfPath),
                    OutputFolder = COALESCE($folder, OutputFolder),
                    CreatedAt = $created
                WHERE Id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", latest);
        }
        cmd.Parameters.AddWithValue("$job", jobId);
        cmd.Parameters.AddWithValue("$docx", (object?)NullIfBlank(docxPath) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pdf", (object?)NullIfBlank(pdfPath) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$folder", (object?)NullIfBlank(folder) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", created);
        cmd.ExecuteNonQuery();
    }

    static void WriteStoredResume(SqliteConnection conn, SqliteTransaction tx, JobTask job) {
        var path = job.ResumePath ?? "";
        if (path.Length == 0) return;
        string? docx = path.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) ? path : null;
        string? pdf = path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? path : null;
        if (docx is null && pdf is null) return;
        WriteResumeOutput(conn, tx, job.JobId.Trim(), docx, pdf, Path.GetDirectoryName(path));
    }

    /// <summary>One joined read of jobs, applications and the latest resume output. No disk walk.</summary>
    public static List<StoredApplication> LoadApplications() {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            WITH latest AS (
                SELECT JobId, DocxPath, PdfPath, OutputFolder, CreatedAt, Id,
                       ROW_NUMBER() OVER (PARTITION BY JobId ORDER BY CreatedAt DESC, Id DESC) AS n
                FROM ResumeOutputs
            )
            SELECT j.Id, j.Source, j.ExternalJobId, j.Company, j.Title, j.JobDescription, j.ApplyUrl,
                   a.Status, l.DocxPath, l.PdfPath, l.OutputFolder
            FROM Jobs j
            LEFT JOIN Applications a ON a.JobId = j.Id
            LEFT JOIN latest l ON l.JobId = j.Id AND l.n = 1;
            """;
        var rows = new List<StoredApplication>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) {
            rows.Add(new StoredApplication {
                JobId = reader.GetString(0),
                Source = reader.IsDBNull(1) ? "" : reader.GetString(1),
                ExternalJobId = reader.IsDBNull(2) ? null : reader.GetString(2),
                Company = reader.IsDBNull(3) ? "" : reader.GetString(3),
                Title = reader.IsDBNull(4) ? "" : reader.GetString(4),
                JobDescription = reader.IsDBNull(5) ? "" : reader.GetString(5),
                ApplyUrl = reader.IsDBNull(6) ? null : reader.GetString(6),
                ApplicationStatus = reader.IsDBNull(7) ? null : reader.GetString(7),
                DocxPath = reader.IsDBNull(8) ? null : reader.GetString(8),
                PdfPath = reader.IsDBNull(9) ? null : reader.GetString(9),
                OutputFolder = reader.IsDBNull(10) ? null : reader.GetString(10)
            });
        }
        return rows;
    }

    /// <summary>
    /// Copies the stored resume path onto the in-memory tasks. A missing file is left as stored;
    /// it does not start a folder search.
    /// </summary>
    public static int ApplyResumeLinks(IEnumerable<JobTask> tasks) {
        try {
            var sw = Stopwatch.StartNew();
            var rows = LoadApplications();
            PerfLog.Line("APPLICATIONS PERF db-query ms=" + sw.ElapsedMilliseconds + " rows=" + rows.Count);
            var byId = new Dictionary<string, StoredApplication>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows) byId[row.JobId] = row;
            var applied = 0;
            var filled = 0;
            foreach (var job in tasks) {
                if (string.IsNullOrWhiteSpace(job.JobId) || !byId.TryGetValue(job.JobId, out var row)) continue;
                if (ApplyCapture.FillIfEmpty(job, row.ApplyUrl, DateTime.Now)) {
                    filled++;
                    job.NotifyTrackingChanged();
                }
                var path = ResumePathOf(row);
                if (string.IsNullOrWhiteSpace(path)) continue;
                applied++;
                if (string.Equals(job.ResumePath, path, StringComparison.OrdinalIgnoreCase)) continue;
                job.ResumePath = path;
                job.NotifyTrackingChanged();
            }
            LastApplyUrlsFilled = filled;
            return applied;
        } catch (Exception ex) {
            LastApplyUrlsFilled = 0;
            PerfLog.Line("JOBSTORE query failed " + ex.GetType().Name);
            return 0;
        }
    }

    /// <summary>
    /// Counts identity, URL, status and resume-output problems. Reads the tables and the tasks
    /// already in memory. It does not list resume folders.
    /// </summary>
    public static StoreIntegrity Inspect(IEnumerable<JobTask> tasks) {
        var list = (tasks ?? Enumerable.Empty<JobTask>()).Where(job => job is not null).ToList();
        using var conn = Open();
        var statusById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = conn.CreateCommand()) {
            cmd.CommandText = "SELECT JobId, Status FROM Applications;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                statusById[reader.GetString(0)] = reader.IsDBNull(1) ? "" : reader.GetString(1);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicateIds = 0;
        var missingJobUrl = 0;
        var missingApplyUrl = 0;
        var invalidUrls = 0;
        var statusMismatches = 0;
        var jobSiteAsAts = 0;
        var statusCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var atsCounts = new Dictionary<ApplicationPlatform, int>();
        foreach (var job in list) {
            var id = ApplicationFields.InternalJobId(job);
            if (id.Length == 0) continue;
            if (!seen.Add(id)) { duplicateIds++; continue; }

            var jobUrl = ApplicationFields.JobUrl(job);
            var apply = ApplicationFields.ApplyUrl(job);
            if (string.IsNullOrWhiteSpace(jobUrl)) missingJobUrl++;
            else if (!JobTracker.IsOpenableUrl(jobUrl)) invalidUrls++;
            if (string.IsNullOrWhiteSpace(apply)) missingApplyUrl++;
            else if (!ApplyCapture.IsApplicationUrl(apply)) invalidUrls++;

            var status = ApplicationStatus.Normalize(job.ApplicationStatus);
            statusCounts[status] = statusCounts.GetValueOrDefault(status) + 1;
            if (statusById.TryGetValue(id, out var stored)
                && !string.Equals(ApplicationStatus.Normalize(stored), status, StringComparison.Ordinal))
                statusMismatches++;

            var platform = job.ApplicationPlatform;
            if (platform is ApplicationPlatform.Jobright or ApplicationPlatform.LinkedIn
                or ApplicationPlatform.Indeed or ApplicationPlatform.Wellfound or ApplicationPlatform.Dice)
                jobSiteAsAts++;
            if (ApplicationPlatformDetector.IsAts(platform))
                atsCounts[platform] = atsCounts.GetValueOrDefault(platform) + 1;
        }

        var order = new[] {
            ApplicationStatus.Viewed, ApplicationStatus.Ready, ApplicationStatus.Applied,
            ApplicationStatus.Interview, ApplicationStatus.Failed, ApplicationStatus.Done
        };
        var statusText = string.Join(",", order.Where(statusCounts.ContainsKey).Select(name => name + ":" + statusCounts[name]));
        var atsText = atsCounts.Count == 0
            ? "(none)"
            : string.Join(",", atsCounts.OrderBy(pair => JobTracker.PlatformDisplayName(pair.Key), StringComparer.OrdinalIgnoreCase)
                .Select(pair => JobTracker.PlatformDisplayName(pair.Key) + ":" + pair.Value));

        return new StoreIntegrity {
            Jobs = list.Count,
            DuplicateInternalJobIds = duplicateIds,
            DuplicateSourceExternal = Scalar(conn, """
                SELECT COUNT(*) FROM (
                    SELECT Source, ExternalJobId FROM Jobs
                    WHERE ExternalJobId IS NOT NULL AND TRIM(ExternalJobId) <> ''
                    GROUP BY Source, ExternalJobId
                    HAVING COUNT(*) > 1
                );
                """),
            MissingJobUrl = missingJobUrl,
            MissingApplyUrl = missingApplyUrl,
            InvalidUrls = invalidUrls,
            MissingApplications = Scalar(conn, """
                SELECT COUNT(*) FROM Jobs j
                LEFT JOIN Applications a ON a.JobId = j.Id
                WHERE a.JobId IS NULL;
                """),
            OrphanApplications = Scalar(conn, """
                SELECT COUNT(*) FROM Applications a
                LEFT JOIN Jobs j ON j.Id = a.JobId
                WHERE j.Id IS NULL;
                """),
            MissingResumeOutputs = Scalar(conn, """
                SELECT COUNT(*) FROM Jobs j
                LEFT JOIN ResumeOutputs r ON r.JobId = j.Id
                WHERE r.JobId IS NULL;
                """),
            OrphanResumeOutputs = Scalar(conn, """
                SELECT COUNT(*) FROM ResumeOutputs r
                LEFT JOIN Jobs j ON j.Id = r.JobId
                WHERE j.Id IS NULL;
                """),
            StatusMismatches = statusMismatches,
            JobSiteStoredAsAts = jobSiteAsAts,
            StatusDistribution = statusText,
            AtsDistribution = atsText
        };
    }

    /// <summary>
    /// Inserts a resume output for a job that already has a DOCX or PDF path and no stored output.
    /// An existing output is left as it is. This does not look through resume folders and does not
    /// invent a path.
    /// </summary>
    public static int RepairResumeOutputs(IEnumerable<JobTask> tasks) {
        var rows = LoadApplications();
        var byId = new Dictionary<string, StoredApplication>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows) byId[row.JobId] = row;
        var pending = new List<(string Id, string? Docx, string? Pdf, string? Folder)>();
        foreach (var job in tasks ?? Enumerable.Empty<JobTask>()) {
            if (job is null) continue;
            var id = ApplicationFields.InternalJobId(job);
            if (id.Length == 0 || !byId.TryGetValue(id, out var row)) continue;
            if (!string.IsNullOrWhiteSpace(ResumePathOf(row))) continue;
            var path = (job.ResumePath ?? "").Trim();
            var docx = path.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) ? path : null;
            var pdf = path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? path : null;
            if (docx is null && pdf is null) continue;
            pending.Add((id, docx, pdf, Path.GetDirectoryName(path)));
        }
        if (pending.Count == 0) return 0;

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        foreach (var item in pending) {
            string? latest = null;
            using (var find = conn.CreateCommand()) {
                find.Transaction = tx;
                find.CommandText = "SELECT Id FROM ResumeOutputs WHERE JobId = $job ORDER BY CreatedAt DESC, Id DESC LIMIT 1;";
                find.Parameters.AddWithValue("$job", item.Id);
                latest = find.ExecuteScalar() as string;
            }
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            if (latest is null) {
                cmd.CommandText = """
                    INSERT INTO ResumeOutputs (Id, JobId, DocxPath, PdfPath, OutputFolder, CreatedAt)
                    VALUES ($id, $job, $docx, $pdf, $folder, $created);
                    """;
                cmd.Parameters.AddWithValue("$id", "out:" + Guid.NewGuid().ToString("N"));
            } else {
                cmd.CommandText = """
                    UPDATE ResumeOutputs
                    SET DocxPath = $docx, PdfPath = $pdf, OutputFolder = $folder, CreatedAt = $created
                    WHERE Id = $id;
                    """;
                cmd.Parameters.AddWithValue("$id", latest);
            }
            cmd.Parameters.AddWithValue("$job", item.Id);
            cmd.Parameters.AddWithValue("$docx", (object?)item.Docx ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pdf", (object?)item.Pdf ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$folder", (object?)item.Folder ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$created", Stamp(DateTime.Now));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return pending.Count;
    }

    public static string? ResumePathOf(StoredApplication row) =>
        !string.IsNullOrWhiteSpace(row.DocxPath) ? row.DocxPath
        : !string.IsNullOrWhiteSpace(row.PdfPath) ? row.PdfPath
        : null;

    public static int UserVersion {
        get {
            using var conn = Open();
            return ReadUserVersion(conn);
        }
    }

    public static (int Jobs, int Applications, int Outputs) Counts() {
        using var conn = Open();
        return Counts(conn);
    }

    internal static void SetUserVersion(int version) {
        if (version < 0 || version > SchemaVersion) throw new ArgumentOutOfRangeException(nameof(version));
        using var conn = Open();
        SetUserVersion(conn, version);
    }

    public static string? ExternalJobIdFor(JobTask job) =>
        NullIfBlank(JobrightPageExtractor.JobIdFromUrl(job.Link));

    static SqliteConnection Open() {
        var path = DatabasePath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var builder = new SqliteConnectionStringBuilder {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true
        };
        var conn = new SqliteConnection(builder.ToString());
        conn.Open();
        ConnectionsOpened++;
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        EnsureSchema(conn);
        if (System.Threading.Interlocked.Exchange(ref _announced, 1) == 0) {
            PerfLog.Line("DB OPEN");
            var counts = Counts(conn);
            PerfLog.Line("DB INTEGRITY jobs=" + counts.Jobs + " applications=" + counts.Applications + " outputs=" + counts.Outputs);
        }
        return conn;
    }

    static int _announced;

    /// <summary>How many connections this process has opened. Tests use it to reject one connection per row.</summary>
    internal static int ConnectionsOpened;

    static void EnsureSchema(SqliteConnection conn) {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Jobs (
                Id TEXT PRIMARY KEY,
                Source TEXT NOT NULL DEFAULT '',
                ExternalJobId TEXT NULL,
                Company TEXT NOT NULL DEFAULT '',
                Title TEXT NOT NULL DEFAULT '',
                JobDescription TEXT NOT NULL DEFAULT '',
                ApplyUrl TEXT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS Applications (
                Id TEXT PRIMARY KEY,
                JobId TEXT NOT NULL UNIQUE,
                Status TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                FOREIGN KEY (JobId) REFERENCES Jobs(Id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS ResumeOutputs (
                Id TEXT PRIMARY KEY,
                JobId TEXT NOT NULL,
                DocxPath TEXT NULL,
                PdfPath TEXT NULL,
                OutputFolder TEXT NULL,
                CreatedAt TEXT NOT NULL,
                FOREIGN KEY (JobId) REFERENCES Jobs(Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_resumeoutputs_job_created ON ResumeOutputs(JobId, CreatedAt);
            CREATE UNIQUE INDEX IF NOT EXISTS idx_jobs_source_external
                ON Jobs(Source, ExternalJobId)
                WHERE ExternalJobId IS NOT NULL;
            """;
        cmd.ExecuteNonQuery();
        UpgradeSchema(conn);
    }

    static void UpgradeSchema(SqliteConnection conn) {
        AddColumn(conn, "Jobs", "JobUrl", "TEXT");
        AddColumn(conn, "Jobs", "CompanyUrl", "TEXT");
        AddColumn(conn, "Jobs", "Location", "TEXT NOT NULL DEFAULT ''");
        AddColumn(conn, "Jobs", "JobSite", "TEXT NOT NULL DEFAULT ''");
        AddColumn(conn, "Jobs", "ApplyUrlCapturedAt", "TEXT");
        AddColumn(conn, "Jobs", "ApplicationPlatform", "TEXT NOT NULL DEFAULT 'Unknown'");
        AddColumn(conn, "Jobs", "QueueStatus", "TEXT NOT NULL DEFAULT 'Queued'");
        AddColumn(conn, "Jobs", "FailureReason", "TEXT NOT NULL DEFAULT ''");
        AddColumn(conn, "Applications", "ViewedAt", "TEXT");
        AddColumn(conn, "Applications", "ReadyAt", "TEXT");
        AddColumn(conn, "Applications", "AppliedAt", "TEXT");
        AddColumn(conn, "Applications", "InterviewAt", "TEXT");
        AddColumn(conn, "Applications", "FailedAt", "TEXT");
        AddColumn(conn, "Applications", "DoneAt", "TEXT");
        AddColumn(conn, "Applications", "FailedFrom", "TEXT NOT NULL DEFAULT ''");
        using (var index = conn.CreateCommand()) {
            index.CommandText = """
                CREATE INDEX IF NOT EXISTS idx_applications_status ON Applications(Status);
                CREATE INDEX IF NOT EXISTS idx_jobs_queue ON Jobs(QueueStatus);
                CREATE INDEX IF NOT EXISTS idx_jobs_created ON Jobs(CreatedAt);
                CREATE INDEX IF NOT EXISTS idx_jobs_site ON Jobs(JobSite);
                CREATE INDEX IF NOT EXISTS idx_jobs_platform ON Jobs(ApplicationPlatform);
                """;
            index.ExecuteNonQuery();
        }
        if (ReadUserVersion(conn) >= SchemaVersion) return;
        using (var backfill = conn.CreateCommand()) {
            backfill.CommandText = """
                UPDATE Jobs SET JobUrl = 'https://jobright.ai/jobs/info/' || ExternalJobId
                WHERE (JobUrl IS NULL OR TRIM(JobUrl) = '')
                  AND Source = 'jobright-browser'
                  AND ExternalJobId IS NOT NULL AND TRIM(ExternalJobId) <> '';
                UPDATE Jobs SET JobSite = 'Jobright'
                WHERE Source = 'jobright-browser' AND (JobSite IS NULL OR TRIM(JobSite) = '');
                UPDATE Jobs SET QueueStatus = 'Completed'
                WHERE Id IN (
                    SELECT JobId FROM ResumeOutputs
                    WHERE (DocxPath IS NOT NULL AND TRIM(DocxPath) <> '')
                       OR (PdfPath IS NOT NULL AND TRIM(PdfPath) <> '')
                );
                DELETE FROM ResumeOutputs
                WHERE rowid NOT IN (
                    SELECT rowid FROM (
                        SELECT rowid,
                               ROW_NUMBER() OVER (PARTITION BY JobId ORDER BY CreatedAt DESC, Id DESC) AS n
                        FROM ResumeOutputs
                    ) WHERE n = 1
                );
                """;
            backfill.ExecuteNonQuery();
        }
        using (var unique = conn.CreateCommand()) {
            unique.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS idx_resumeoutputs_job ON ResumeOutputs(JobId);";
            unique.ExecuteNonQuery();
        }
        SetUserVersion(conn, SchemaVersion);
        PerfLog.Line("DB MIGRATION version=" + SchemaVersion.ToString(CultureInfo.InvariantCulture));
    }

    static void AddColumn(SqliteConnection conn, string table, string column, string definition) {
        using var info = conn.CreateCommand();
        info.CommandText = "PRAGMA table_info(" + table + ");";
        using var reader = info.ExecuteReader();
        while (reader.Read())
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
        reader.Dispose();
        using var alter = conn.CreateCommand();
        alter.CommandText = "ALTER TABLE " + table + " ADD COLUMN " + column + " " + definition + ";";
        alter.ExecuteNonQuery();
    }

    static bool UpsertJob(SqliteConnection conn, SqliteTransaction tx, JobTask job, bool keepJobOnExternalConflict) {
        var external = ExternalJobIdFor(job);
        try {
            WriteJob(conn, tx, job, external);
            return true;
        } catch (SqliteException ex) when (ex.SqliteErrorCode == 19 && keepJobOnExternalConflict) {
            WriteJob(conn, tx, job, null);
            return true;
        } catch (SqliteException ex) when (ex.SqliteErrorCode == 19) {
            return false;
        }
    }

    static void WriteJob(SqliteConnection conn, SqliteTransaction tx, JobTask job, string? externalId) {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO Jobs (
                Id, Source, ExternalJobId, Company, Title, Location, JobDescription, JobUrl, CompanyUrl,
                ApplyUrl, ApplyUrlCapturedAt, ApplicationPlatform, JobSite, QueueStatus, FailureReason,
                CreatedAt, UpdatedAt)
            VALUES (
                $id, $source, $external, $company, $title, $location, $jd, $jobUrl, $companyUrl,
                $apply, $applyAt, $platform, $site,
                CASE WHEN TRIM(COALESCE($queue, '')) = '' THEN 'Queued' ELSE $queue END,
                $failure,
                $created, $updated)
            ON CONFLICT(Id) DO UPDATE SET
                Source = CASE WHEN TRIM(COALESCE(excluded.Source, '')) = '' THEN Jobs.Source ELSE excluded.Source END,
                ExternalJobId = COALESCE(excluded.ExternalJobId, Jobs.ExternalJobId),
                Company = CASE WHEN TRIM(COALESCE(excluded.Company, '')) = '' THEN Jobs.Company ELSE excluded.Company END,
                Title = CASE WHEN TRIM(COALESCE(excluded.Title, '')) = '' THEN Jobs.Title ELSE excluded.Title END,
                Location = CASE WHEN TRIM(COALESCE(excluded.Location, '')) = '' THEN Jobs.Location ELSE excluded.Location END,
                JobDescription = CASE WHEN TRIM(COALESCE(excluded.JobDescription, '')) = '' THEN Jobs.JobDescription ELSE excluded.JobDescription END,
                JobUrl = COALESCE(excluded.JobUrl, Jobs.JobUrl),
                CompanyUrl = COALESCE(excluded.CompanyUrl, Jobs.CompanyUrl),
                ApplyUrl = COALESCE(excluded.ApplyUrl, Jobs.ApplyUrl),
                ApplyUrlCapturedAt = COALESCE(excluded.ApplyUrlCapturedAt, Jobs.ApplyUrlCapturedAt),
                ApplicationPlatform = CASE
                    WHEN excluded.ApplicationPlatform IS NULL
                      OR TRIM(excluded.ApplicationPlatform) = ''
                      OR excluded.ApplicationPlatform = 'Unknown'
                    THEN COALESCE(NULLIF(TRIM(Jobs.ApplicationPlatform), ''), excluded.ApplicationPlatform)
                    ELSE excluded.ApplicationPlatform END,
                JobSite = CASE WHEN TRIM(COALESCE(excluded.JobSite, '')) = '' THEN Jobs.JobSite ELSE excluded.JobSite END,
                QueueStatus = CASE WHEN TRIM(COALESCE($queue, '')) = '' THEN Jobs.QueueStatus ELSE $queue END,
                FailureReason = excluded.FailureReason,
                UpdatedAt = excluded.UpdatedAt;
            """;
        cmd.Parameters.AddWithValue("$id", job.JobId.Trim());
        cmd.Parameters.AddWithValue("$source", job.Source ?? "");
        cmd.Parameters.AddWithValue("$external", (object?)externalId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$company", job.Company ?? "");
        cmd.Parameters.AddWithValue("$title", job.Title ?? "");
        cmd.Parameters.AddWithValue("$location", job.Location ?? "");
        cmd.Parameters.AddWithValue("$jd", job.Jd ?? "");
        cmd.Parameters.AddWithValue("$jobUrl", (object?)NullIfBlank(job.Link) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$companyUrl", (object?)NullIfBlank(job.CompanyUrl) ?? DBNull.Value);
        // A blank or job-site address does not replace an application URL already stored.
        cmd.Parameters.AddWithValue("$apply", (object?)ApplicationFields.StoredApplyUrl(job) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$applyAt", (object?)StampOrNull(job.ApplyUrlCapturedAt) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$platform", job.ApplicationPlatform.ToString());
        cmd.Parameters.AddWithValue("$site", ApplicationPlatformDetector.JobSiteName(job.Link, job.Source));
        cmd.Parameters.AddWithValue("$queue", job.Status ?? "");
        cmd.Parameters.AddWithValue("$failure", job.FailureReason ?? "");
        cmd.Parameters.AddWithValue("$created", Stamp(job.CreatedAt == default ? DateTime.Now : job.CreatedAt));
        cmd.Parameters.AddWithValue("$updated", Stamp(job.UpdatedAt == default ? DateTime.Now : job.UpdatedAt));
        cmd.ExecuteNonQuery();
    }

    static void UpsertApplication(SqliteConnection conn, SqliteTransaction tx, JobTask job) {
        var id = job.JobId.Trim();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO Applications (
                Id, JobId, Status, CreatedAt, UpdatedAt,
                ViewedAt, ReadyAt, AppliedAt, InterviewAt, FailedAt, DoneAt, FailedFrom)
            VALUES (
                $id, $job,
                CASE WHEN TRIM(COALESCE($status, '')) = '' THEN 'Viewed' ELSE $status END,
                $created, $updated,
                $viewed, $ready, $applied, $interview, $failed, $done, $from)
            ON CONFLICT(JobId) DO UPDATE SET
                Status = CASE WHEN TRIM(COALESCE($status, '')) = '' THEN Applications.Status ELSE $status END,
                UpdatedAt = excluded.UpdatedAt,
                ViewedAt = COALESCE(excluded.ViewedAt, Applications.ViewedAt),
                ReadyAt = COALESCE(excluded.ReadyAt, Applications.ReadyAt),
                AppliedAt = COALESCE(excluded.AppliedAt, Applications.AppliedAt),
                InterviewAt = COALESCE(excluded.InterviewAt, Applications.InterviewAt),
                FailedAt = COALESCE(excluded.FailedAt, Applications.FailedAt),
                DoneAt = COALESCE(excluded.DoneAt, Applications.DoneAt),
                FailedFrom = CASE WHEN TRIM(COALESCE(excluded.FailedFrom, '')) = '' THEN Applications.FailedFrom ELSE excluded.FailedFrom END;
            """;
        cmd.Parameters.AddWithValue("$id", "app:" + id);
        cmd.Parameters.AddWithValue("$job", id);
        cmd.Parameters.AddWithValue("$status", job.ApplicationStatus ?? "");
        cmd.Parameters.AddWithValue("$created", Stamp(job.CreatedAt == default ? DateTime.Now : job.CreatedAt));
        cmd.Parameters.AddWithValue("$updated", Stamp(job.UpdatedAt == default ? DateTime.Now : job.UpdatedAt));
        cmd.Parameters.AddWithValue("$viewed", (object?)StampOrNull(job.ViewedAt) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ready", (object?)StampOrNull(job.ReadyAt) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$applied", (object?)StampOrNull(job.AppliedAt) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$interview", (object?)StampOrNull(job.InterviewAt) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$failed", (object?)StampOrNull(job.FailedAt) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$done", (object?)StampOrNull(job.DoneAt) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$from", job.FailedFrom ?? "");
        cmd.ExecuteNonQuery();
    }

    static int ReadUserVersion(SqliteConnection conn) {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    static void SetUserVersion(SqliteConnection conn, int version) {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version = " + version.ToString(CultureInfo.InvariantCulture) + ";";
        cmd.ExecuteNonQuery();
    }

    static (int Jobs, int Applications, int Outputs) Counts(SqliteConnection conn) =>
        (Scalar(conn, "SELECT COUNT(*) FROM Jobs"),
         Scalar(conn, "SELECT COUNT(*) FROM Applications"),
         Scalar(conn, "SELECT COUNT(*) FROM ResumeOutputs"));

    static int Scalar(SqliteConnection conn, string sql) {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    static string Stamp(DateTime value) => value.ToString("o", CultureInfo.InvariantCulture);

    static string? StampOrNull(DateTime? value) =>
        value is DateTime at && at != default ? Stamp(at) : null;

    static string Text(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? "" : reader.GetString(ordinal) ?? "";

    static DateTime RequiredTime(SqliteDataReader reader, int ordinal) {
        var text = Text(reader, ordinal);
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : DateTime.Now;
    }

    static DateTime? OptionalTime(SqliteDataReader reader, int ordinal) {
        var text = Text(reader, ordinal);
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : null;
    }

    static string? NullIfBlank(string? value) {
        var trimmed = (value ?? "").Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}

public sealed class MigrationReport {
    public bool AlreadyComplete { get; init; }
    public bool Failed { get; init; }
    public int Jobs { get; init; }
    public int Applications { get; init; }
    public int ResumeOutputs { get; init; }
    public int ResumeInfoFilesRead { get; init; }
    public long TotalMs { get; init; }
    public long ScanMs { get; init; }
    public int DirectoryEnumerations { get; init; }
}

public sealed class StoredApplication {
    public string JobId { get; init; } = "";
    public string Source { get; init; } = "";
    public string? ExternalJobId { get; init; }
    public string Company { get; init; } = "";
    public string Title { get; init; } = "";
    public string JobDescription { get; init; } = "";
    public string? ApplyUrl { get; init; }
    public string? ApplicationStatus { get; init; }
    public string? DocxPath { get; init; }
    public string? PdfPath { get; init; }
    public string? OutputFolder { get; init; }
}

public sealed class StoreIntegrity {
    public int Jobs { get; init; }
    public int DuplicateInternalJobIds { get; init; }
    public int DuplicateSourceExternal { get; init; }
    public int MissingJobUrl { get; init; }
    public int MissingApplyUrl { get; init; }
    public int InvalidUrls { get; init; }
    public int MissingApplications { get; init; }
    public int OrphanApplications { get; init; }
    public int MissingResumeOutputs { get; init; }
    public int OrphanResumeOutputs { get; init; }
    public int StatusMismatches { get; init; }
    public int JobSiteStoredAsAts { get; init; }
    public string StatusDistribution { get; init; } = "";
    public string AtsDistribution { get; init; } = "";

    public string Describe() =>
        "APPLICATIONS integrity"
        + " jobs=" + Jobs
        + " duplicate-internal=" + DuplicateInternalJobIds
        + " duplicate-source-external=" + DuplicateSourceExternal
        + " missing-job-url=" + MissingJobUrl
        + " missing-apply-url=" + MissingApplyUrl
        + " invalid-urls=" + InvalidUrls
        + " missing-applications=" + MissingApplications
        + " orphan-applications=" + OrphanApplications
        + " missing-resume-outputs=" + MissingResumeOutputs
        + " orphan-resume-outputs=" + OrphanResumeOutputs
        + " status-mismatch=" + StatusMismatches
        + " job-site-stored-as-ats=" + JobSiteStoredAsAts
        + " status=" + StatusDistribution
        + " ats=" + AtsDistribution;
}

/// <summary>
/// The one mapping from a job to the fields the Applications page shows.
/// JobUrl is the posting (Open Job). ApplyUrl is the application page (Apply).
/// Job site is the board the posting came from. ATS is the application platform, never the job site.
/// Queue status is JobTask.Status. Application status is JobTask.ApplicationStatus.
/// </summary>
public static class ApplicationFields {
    public static string InternalJobId(JobTask job) => (job.JobId ?? "").Trim();
    public static string? ExternalJobId(JobTask job) => JobStore.ExternalJobIdFor(job);
    public static string JobSite(JobTask job) => job.JobSite ?? "";
    public static string JobUrl(JobTask job) => job.Link ?? "";
    public static string ApplyUrl(JobTask job) => job.ApplyUrl ?? "";

    /// <summary>ATS only. A job board stored by mistake is reported as unknown, not as the platform.</summary>
    public static ApplicationPlatform Ats(JobTask job) =>
        ApplicationPlatformDetector.IsAts(job.ApplicationPlatform) ? job.ApplicationPlatform : ApplicationPlatform.Unknown;

    public static string ApplicationStatus(JobTask job) => job.ApplicationStatus ?? "";
    public static string QueueStatus(JobTask job) => job.Status ?? "";
    public static string Company(JobTask job) => job.Company ?? "";
    public static string CompanyWebsite(JobTask job) => job.CompanyUrl ?? "";

    public static string ResumeDocxPath(JobTask job) {
        var path = job.ResumePath ?? "";
        return path.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) ? path : "";
    }

    public static string ResumePdfPath(JobTask job) {
        var path = job.ResumePath ?? "";
        return path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? path : "";
    }

    /// <summary>Null unless the address is an application page. A job-site URL is not stored as one.</summary>
    public static string? StoredApplyUrl(JobTask job) =>
        ApplyCapture.IsApplicationUrl(job.ApplyUrl) ? job.ApplyUrl.Trim() : null;

    public static string BindLine(JobTask job) {
        var ats = Ats(job);
        var atsName = ats == ApplicationPlatform.Unknown ? "" : JobTracker.PlatformDisplayName(ats);
        return "jobId=" + InternalJobId(job)
            + " status=" + ApplicationStatus(job)
            + " jobUrl-present=" + (JobTracker.IsOpenableUrl(JobUrl(job)) ? "yes" : "no")
            + " applyUrl-present=" + (ApplyCapture.IsApplicationUrl(ApplyUrl(job)) ? "yes" : "no")
            + " apply-enabled=" + (JobTracker.CanApply(job) ? "yes" : "no")
            + " ats=" + atsName;
    }
}
