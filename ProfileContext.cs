#pragma warning disable CS0105 // The style-test project already imports System.IO globally.
using System.IO;
#pragma warning restore CS0105
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace ResumeBuilder;

/// <summary>
/// One folder and one SQLite database per person. The global folder keeps the license, the icon
/// cache and the profile list. Job rows never store a profile id.
/// </summary>
public static class ProfilePaths {
    /// <summary>Tests point this at a temporary folder. The app leaves it unset.</summary>
    internal static string? RootOverride { get; set; }

    public static string GlobalRoot => RootOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ResumeBuilder");

    public static string UsersRoot => Path.Combine(GlobalRoot, "Users");

    public static string RegistryPath => Path.Combine(GlobalRoot, "profiles.json");

    public static string LegacyDatabasePath => Path.Combine(GlobalRoot, JobStore.FileName);

    public static string ProfileRoot(string profileId) {
        if (!ProfileIds.IsValid(profileId)) throw new ArgumentException("Invalid profile id.", nameof(profileId));
        return Path.Combine(UsersRoot, profileId);
    }

    public static string DatabasePath(string profileId) => Path.Combine(ProfileRoot(profileId), JobStore.FileName);
}

public static class ProfileIds {
    static readonly Regex Pattern = new("^USR-[0-9A-F]{8}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsValid(string? profileId) => profileId is not null && Pattern.IsMatch(profileId);

    public static string NewId() {
        Span<byte> bytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(bytes);
        return "USR-" + Convert.ToHexString(bytes);
    }

    /// <summary>A display name, or null when it is blank, too long, or not a folder-safe label.</summary>
    public static string? NormalizeName(string? name) {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0 || trimmed.Length > 40) return null;
        foreach (var c in trimmed)
            if (char.IsControl(c) || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*')
                return null;
        return trimmed;
    }
}

public sealed class ProfileRecord {
    public string ProfileId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Avatar { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastUsedAt { get; set; }

    public string? AvatarFile {
        get {
            if (string.IsNullOrWhiteSpace(Avatar) || Avatar.IndexOfAny(['\\', '/']) >= 0) return null;
            if (!ProfileIds.IsValid(ProfileId)) return null;
            return Path.Combine(ProfilePaths.ProfileRoot(ProfileId), Avatar);
        }
    }
}

public sealed class ProfileCard {
    public string ProfileId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string LastUsedText { get; init; } = "";
    public string? AvatarFile { get; init; }
}

/// <summary>profiles.json. Display names are labels. The profile id is the identity.</summary>
public static class ProfileRegistry {
    static readonly JsonSerializerOptions Opt = new() {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static IReadOnlyList<ProfileRecord> Load() =>
        (ReadFile().Profiles ?? new List<ProfileRecord>())
            .Where(p => ProfileIds.IsValid(p.ProfileId) && ProfileIds.NormalizeName(p.DisplayName) is not null)
            .ToList();

    public static ProfileRecord? Find(string profileId) =>
        Load().FirstOrDefault(p => string.Equals(p.ProfileId, profileId, StringComparison.Ordinal));

    public static ProfileRecord Create(string displayName, string? avatarSource) {
        var name = ProfileIds.NormalizeName(displayName) ?? throw new ArgumentException("Enter a profile name.");
        var profiles = Load().ToList();
        var id = UniqueId(profiles);
        var root = ProfilePaths.ProfileRoot(id);
        Directory.CreateDirectory(root);
        JobStore.EnsureReadyAt(ProfilePaths.DatabasePath(id));
        var now = DateTime.Now;
        var record = new ProfileRecord {
            ProfileId = id,
            DisplayName = name,
            Avatar = StoreAvatar(root, avatarSource),
            CreatedAt = now,
            LastUsedAt = now
        };
        profiles.Add(record);
        Save(profiles);
        return record;
    }

    public static bool Rename(string profileId, string displayName) {
        var name = ProfileIds.NormalizeName(displayName);
        if (name is null || !ProfileIds.IsValid(profileId)) return false;
        var profiles = Load().ToList();
        var record = profiles.FirstOrDefault(p => p.ProfileId == profileId);
        if (record is null) return false;
        record.DisplayName = name;
        Save(profiles);
        return true;
    }

    /// <summary>Removes the profile folder and the registry entry. Documents output is not touched.</summary>
    public static bool Delete(string profileId, out string? error) {
        error = null;
        if (!ProfileIds.IsValid(profileId)) { error = "That profile was not found."; return false; }
        if (ProfileLock.IsInUse(profileId)) { error = "This profile is already open."; return false; }
        var profiles = Load().ToList();
        if (profiles.All(p => p.ProfileId != profileId)) { error = "That profile was not found."; return false; }
        var root = ProfilePaths.ProfileRoot(profileId);
        try {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        } catch (IOException) {
            error = "This profile is already open.";
            return false;
        }
        Save(profiles.Where(p => p.ProfileId != profileId).ToList());
        return true;
    }

    public static void Touch(string profileId) {
        var profiles = Load().ToList();
        var record = profiles.FirstOrDefault(p => p.ProfileId == profileId);
        if (record is null) return;
        record.LastUsedAt = DateTime.Now;
        Save(profiles);
    }

    static string UniqueId(List<ProfileRecord> profiles) {
        for (var attempt = 0; attempt < 20; attempt++) {
            var id = ProfileIds.NewId();
            if (profiles.All(p => p.ProfileId != id) && !Directory.Exists(ProfilePaths.ProfileRoot(id)))
                return id;
        }
        throw new InvalidOperationException("A profile id could not be chosen.");
    }

    static string? StoreAvatar(string root, string? source) =>
        TryCopyAvatar(root, source, out var stored, out _) ? stored : null;

    /// <summary>
    /// Copies a picture into this profile folder as avatar.&lt;ext&gt;. The original file is not kept.
    /// Returns false and a short message when the file cannot be used. Details stay in the log.
    /// </summary>
    public static bool TrySetAvatar(string profileId, string? source, out string error) {
        error = "Unable to use this image.";
        if (!ProfileIds.IsValid(profileId)) {
            PerfLog.Line("PROFILE avatar rejected reason=profile");
            return false;
        }
        var profiles = Load().ToList();
        var record = profiles.FirstOrDefault(p => p.ProfileId == profileId);
        if (record is null) {
            PerfLog.Line("PROFILE avatar rejected id=" + profileId + " reason=profile");
            return false;
        }
        var root = ProfilePaths.ProfileRoot(profileId);
        Directory.CreateDirectory(root);
        if (!TryCopyAvatar(root, source, out var stored, out var reason)) {
            PerfLog.Line("PROFILE avatar rejected id=" + profileId + " reason=" + reason);
            return false;
        }
        record.Avatar = stored;
        Save(profiles);
        PerfLog.Line("PROFILE avatar updated id=" + profileId);
        return true;
    }

    /// <summary>Removes this profile's avatar file and the registry reference. Other files stay.</summary>
    public static bool ClearAvatar(string profileId) {
        if (!ProfileIds.IsValid(profileId)) return false;
        var profiles = Load().ToList();
        var record = profiles.FirstOrDefault(p => p.ProfileId == profileId);
        if (record is null) return false;
        DeleteStoredAvatars(ProfilePaths.ProfileRoot(profileId));
        record.Avatar = null;
        Save(profiles);
        PerfLog.Line("PROFILE avatar cleared id=" + profileId);
        return true;
    }

    /// <summary>A stored avatar path that exists and still looks like a picture. Otherwise null.</summary>
    public static string? LoadableAvatar(string? file) =>
        !string.IsNullOrWhiteSpace(file) && File.Exists(file) && HasImageHeader(file) ? file : null;

    static readonly string[] AvatarFileNames = {
        "avatar.png", "avatar.jpg", "avatar.jpeg", "avatar.webp", "avatar.bmp", "avatar.ico"
    };

    static bool TryCopyAvatar(string root, string? source, out string? stored, out string reason) {
        stored = null;
        reason = "type";
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source)) { reason = "missing"; return false; }
        var ext = Path.GetExtension(source).ToLowerInvariant();
        if (ext is not ".png" and not ".jpg" and not ".jpeg" and not ".webp" and not ".bmp" and not ".ico") return false;
        try {
            var info = new FileInfo(source);
            if (info.Length == 0) { reason = "empty"; return false; }
            if (info.Length > 2 * 1024 * 1024) { reason = "size"; return false; }
            if (!HasImageHeader(source)) { reason = "header"; return false; }
            Directory.CreateDirectory(root);
            var incoming = Path.Combine(root, "avatar-incoming" + ext);
            File.Copy(source, incoming, overwrite: true);
            if (!HasImageHeader(incoming)) {
                File.Delete(incoming);
                reason = "header";
                return false;
            }
            DeleteStoredAvatars(root);
            stored = "avatar" + ext;
            var dest = Path.Combine(root, stored);
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(incoming, dest);
            return true;
        } catch (Exception ex) {
            reason = ex.GetType().Name;
            try {
                var incoming = Path.Combine(root, "avatar-incoming" + ext);
                if (File.Exists(incoming)) File.Delete(incoming);
            } catch { /* the rejected copy is best-effort */ }
            return false;
        }
    }

    static void DeleteStoredAvatars(string root) {
        if (!Directory.Exists(root)) return;
        foreach (var name in AvatarFileNames) {
            var path = Path.Combine(root, name);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    static bool HasImageHeader(string path) {
        try {
            using var stream = File.OpenRead(path);
            var header = new byte[12];
            var read = stream.Read(header, 0, header.Length);
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".png")
                return read >= 8 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47;
            if (ext is ".jpg" or ".jpeg")
                return read >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF;
            if (ext is ".webp")
                return read >= 12
                    && header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F'
                    && header[8] == (byte)'W' && header[9] == (byte)'E' && header[10] == (byte)'B' && header[11] == (byte)'P';
            if (ext is ".bmp")
                return read >= 2 && header[0] == (byte)'B' && header[1] == (byte)'M';
            if (ext is ".ico")
                return read >= 4 && header[0] == 0 && header[1] == 0 && header[2] == 1 && header[3] == 0;
            return false;
        } catch {
            return false;
        }
    }

    public static bool LegacyMigrated => ReadFile().LegacyMigrated;

    static ProfileRegistryFile ReadFile() {
        var path = ProfilePaths.RegistryPath;
        if (!File.Exists(path)) return new ProfileRegistryFile();
        return JsonSerializer.Deserialize<ProfileRegistryFile>(File.ReadAllText(path), Opt)
               ?? new ProfileRegistryFile();
    }

    static void Save(List<ProfileRecord> profiles) {
        var file = ReadFile();
        file.Profiles = profiles;
        Write(file);
    }

    public static void AddMigrated(ProfileRecord record) {
        var file = ReadFile();
        file.LegacyMigrated = true;
        file.Profiles ??= new List<ProfileRecord>();
        file.Profiles.Add(record);
        Write(file);
    }

    static void Write(ProfileRegistryFile file) {
        Directory.CreateDirectory(ProfilePaths.GlobalRoot);
        var path = ProfilePaths.RegistryPath;
        var json = JsonSerializer.Serialize(file, Opt);
        var temp = path + ".tmp";
        File.WriteAllText(temp, json);
        if (File.Exists(path)) File.Replace(temp, path, destinationBackupFileName: null);
        else File.Move(temp, path);
    }

    sealed class ProfileRegistryFile {
        public List<ProfileRecord> Profiles { get; set; } = new();
        public bool LegacyMigrated { get; set; }
    }
}

public static class ProfileCatalog {
    public static IReadOnlyList<ProfileCard> Cards() =>
        ProfileRegistry.Load()
            .OrderByDescending(p => p.LastUsedAt)
            .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(p => new ProfileCard {
                ProfileId = p.ProfileId,
                DisplayName = p.DisplayName,
                LastUsedText = LastUsedText(p.LastUsedAt),
                AvatarFile = ProfileRegistry.LoadableAvatar(p.AvatarFile)
            })
            .ToList();

    static string LastUsedText(DateTime at) {
        var local = at.Kind == DateTimeKind.Utc ? at.ToLocalTime() : at;
        if (local.Date == DateTime.Now.Date) return "Last used today";
        return "Last used " + local.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
    }
}

/// <summary>The open profile. Every profile-specific path goes through here.</summary>
public static class ProfileContext {
    public static string? ProfileId { get; private set; }
    public static string? DisplayName { get; private set; }

    public static bool IsOpen => ProfileId is not null;

    /// <summary>The profile folder, or the global folder when no profile is open.</summary>
    public static string ProfileRoot =>
        ProfileId is null ? ProfilePaths.GlobalRoot : ProfilePaths.ProfileRoot(ProfileId);

    public static string DatabasePath => Path.Combine(ProfileRoot, JobStore.FileName);
    public static string HtmlTailoringPath => Path.Combine(ProfileRoot, "HtmlTailoring");
    public static string EmailTasksPath => Path.Combine(ProfileRoot, "email-tasks.json");
    public static string SettingsPath => Path.Combine(ProfileRoot, "settings.json");
    public static string CandidateProfilePath => Path.Combine(ProfileRoot, "candidate-profile.json");

    public static bool TryOpen(string profileId) {
        if (!ProfileIds.IsValid(profileId)) return false;
        var record = ProfileRegistry.Find(profileId);
        if (record is null) return false;
        if (!Directory.Exists(ProfilePaths.ProfileRoot(profileId))) return false;
        ProfileId = record.ProfileId;
        DisplayName = record.DisplayName;
        ProfileRegistry.Touch(profileId);
        return true;
    }

    public static void Close() {
        ProfileId = null;
        DisplayName = null;
    }

    public static string ProfileLogLine() =>
        "PROFILE id=" + (ProfileId ?? "(none)") + " name=" + OneLine(DisplayName);

    public static string DatabaseLogLine() => "DB path=" + DatabasePath;

    public static string LogToken => ProfileId ?? "(none)";

    static string OneLine(string? value) {
        var text = (value ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length == 0 ? "(none)" : text;
    }
}

public static class ProfileLock {
    public static string MutexName(string profileId) => "ResumeBuilder.Profile." + profileId;

    public static SingleInstance? TryAcquire(string profileId) =>
        ProfileIds.IsValid(profileId) ? SingleInstance.TryAcquire(MutexName(profileId)) : null;

    public static bool IsInUse(string profileId) {
        var held = TryAcquire(profileId);
        if (held is null) return true;
        held.Dispose();
        return false;
    }

    public static void Remember(string profileId) {
        if (!ProfileIds.IsValid(profileId)) return;
        File.WriteAllText(PidPath(profileId), Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
    }

    public static void Forget(string profileId) {
        if (!ProfileIds.IsValid(profileId)) return;
        var path = PidPath(profileId);
        try {
            if (!File.Exists(path)) return;
            if (File.ReadAllText(path).Trim() != Environment.ProcessId.ToString(CultureInfo.InvariantCulture)) return;
            File.Delete(path);
        } catch (IOException) { }
    }

    public static string PidPath(string profileId) => Path.Combine(ProfilePaths.ProfileRoot(profileId), "workspace.pid");
}

public static class ProfileWindow {
    public static string Title(string displayName) => "Resume Builder - " + displayName.Trim();

    /// <summary>Brings forward the process that already holds this profile. A missing window returns false.</summary>
    public static bool Activate(string profileId) {
        if (!ProfileIds.IsValid(profileId) || !File.Exists(ProfileLock.PidPath(profileId))) return false;
        if (!int.TryParse(File.ReadAllText(ProfileLock.PidPath(profileId)).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
            return false;
        if (pid == Environment.ProcessId) return false;
        try {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return false;
            var handle = process.MainWindowHandle;
            if (handle == IntPtr.Zero) return false;
            if (IsIconic(handle)) ShowWindow(handle, 9);
            SetForegroundWindow(handle);
            return true;
        } catch (ArgumentException) {
            return false;
        } catch {
            return false;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool IsIconic(IntPtr hWnd);
}

public static class ProfileLaunch {
    public static string? ProfileId(string[] args) {
        for (var i = 0; i < args.Length - 1; i++) {
            if (!string.Equals(args[i], "--profile", StringComparison.OrdinalIgnoreCase)) continue;
            var id = (args[i + 1] ?? "").Trim().ToUpperInvariant();
            return ProfileIds.IsValid(id) ? id : null;
        }
        return null;
    }

    public static bool Start(string profileId) {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) || !ProfileIds.IsValid(profileId)) return false;
        Process.Start(new ProcessStartInfo(exe, "--profile " + profileId) { UseShellExecute = false });
        return true;
    }
}

public enum ProfileMigrationStatus { None, Migrated, Already, Failed }

public sealed class ProfileMigrationResult {
    public ProfileMigrationStatus Status { get; init; }
    public string? ProfileId { get; init; }
    public string? Error { get; init; }
    public bool Failed => Status == ProfileMigrationStatus.Failed;
}

/// <summary>
/// Copies the previous single-user folder into one profile. The original files stay where they
/// are, so a failed copy does not change them.
/// </summary>
public static class ProfileMigration {
    static readonly string[] Files = {
        "resumebuilder.db", "resumebuilder.db-wal", "resumebuilder.db-shm",
        "candidate-profile.json", "candidate-profile.source.json",
        "settings.json", "email-tasks.json", "baseline-profile.json",
        "prepared-request.json", "prepared-request.txt", "tasks.json"
    };

    static readonly string[] Folders = { "PromptAdaptation", "HtmlTailoring", "results" };
    static readonly string[] BestEffortFolders = { "WebView2", "JobBrowserWebView2" };

    public static ProfileMigrationResult MigrateIfNeeded() {
        try {
            if (ProfileRegistry.LegacyMigrated || ProfileRegistry.Load().Count > 0)
                return new ProfileMigrationResult { Status = ProfileMigrationStatus.Already };
            if (!File.Exists(ProfilePaths.LegacyDatabasePath))
                return new ProfileMigrationResult { Status = ProfileMigrationStatus.None };

            SqliteConnection.ClearAllPools();
            var id = ProfileIds.NewId();
            var guard = 0;
            while (Directory.Exists(ProfilePaths.ProfileRoot(id)) && guard++ < 20)
                id = ProfileIds.NewId();
            var finalRoot = ProfilePaths.ProfileRoot(id);
            var staging = finalRoot + ".partial";
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);
            try {
                CopyListed(ProfilePaths.GlobalRoot, staging);
                var sourceDb = ProfilePaths.LegacyDatabasePath;
                var copiedDb = Path.Combine(staging, JobStore.FileName);
                if (!File.Exists(copiedDb) || new FileInfo(copiedDb).Length != new FileInfo(sourceDb).Length)
                    throw new IOException("The profile database copy does not match the original.");
                if (Directory.Exists(finalRoot)) Directory.Delete(finalRoot, recursive: true);
                Directory.Move(staging, finalRoot);
            } catch {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
                throw;
            }

            var now = DateTime.Now;
            ProfileRegistry.AddMigrated(new ProfileRecord {
                ProfileId = id,
                DisplayName = "Default",
                CreatedAt = now,
                LastUsedAt = now
            });
            return new ProfileMigrationResult { Status = ProfileMigrationStatus.Migrated, ProfileId = id };
        } catch (Exception ex) {
            PerfLog.Line("PROFILE migration failed " + ex.GetType().Name);
            return new ProfileMigrationResult {
                Status = ProfileMigrationStatus.Failed,
                Error = "Existing Resume Builder data could not be copied into a profile. The original files were not changed."
            };
        }
    }

    static void CopyListed(string sourceRoot, string destRoot) {
        foreach (var name in Files) {
            var source = Path.Combine(sourceRoot, name);
            if (!File.Exists(source)) continue;
            File.Copy(source, Path.Combine(destRoot, name), overwrite: false);
        }
        foreach (var name in Folders)
            CopyFolder(Path.Combine(sourceRoot, name), Path.Combine(destRoot, name), bestEffort: false);
        foreach (var name in BestEffortFolders)
            CopyFolder(Path.Combine(sourceRoot, name), Path.Combine(destRoot, name), bestEffort: true);
    }

    static void CopyFolder(string source, string dest, bool bestEffort) {
        if (!Directory.Exists(source)) return;
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source)) {
            try {
                File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: false);
            } catch (IOException) when (bestEffort) { }
            catch (UnauthorizedAccessException) when (bestEffort) { }
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyFolder(dir, Path.Combine(dest, Path.GetFileName(dir)), bestEffort);
    }
}
