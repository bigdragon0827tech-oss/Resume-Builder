using System.Diagnostics;
using System.IO;
using System.Text;

namespace ResumeBuilder;

/// <summary>
/// Lightweight timing and memory diagnostics, written to diagnostics.log next to the app data.
///
/// Deliberately cheap and side-effect free: a Stopwatch per stage, one working-set read per
/// snapshot, and an appended line. Nothing here changes behaviour, and every call swallows its own
/// failures — a diagnostics problem must never affect a job.
/// </summary>
public static class PerfLog {
    const long MaxBytes = 1_000_000;
    static readonly object Gate = new();

    public static bool Enabled { get; set; } = true;
    public static string Path => System.IO.Path.Combine(Storage.DataDir, "diagnostics.log");

    /// <summary>Times a stage; dispose (or let `using` do it) to record the elapsed milliseconds.</summary>
    public static IDisposable Measure(string stage) => new Scope(stage);

    /// <summary>Earlier sessions kept as diagnostics.1.log (newest) … diagnostics.5.log.</summary>
    public const int KeptSessions = 5;

    /// <summary>
    /// Called once per launch. The previous session's log is ROTATED, not deleted (it was cleared
    /// before Phase 1), so the log of a session that crashed survives the restart. Bounded to
    /// <see cref="KeptSessions"/> old files of at most ~1 MB each.
    /// </summary>
    public static void StartSession() {
        try {
            lock (Gate) {
                Directory.CreateDirectory(Storage.DataDir);
                LogRetention.Rotate(Path, KeptSessions);
            }
        } catch {
            // Diagnostics must never break startup.
        }
    }

    public static void Line(string text) => Lines(new[] { text });

    /// <summary>One append for many lines, so a one-time row dump does not open the log once per job.</summary>
    public static void Lines(IEnumerable<string> lines) {
        if (!Enabled || lines is null) return;
        try {
            lock (Gate) {
                var stamp = DateTime.Now.ToString("HH:mm:ss.fff");
                var text = string.Join("", lines.Select(line => stamp + "  " + line + Environment.NewLine));
                if (text.Length == 0) return;
                Directory.CreateDirectory(Storage.DataDir);
                var path = Path;
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) LogRetention.Rotate(path, KeptSessions);
                File.AppendAllText(path, text);
            }
        } catch {
            // Diagnostics must never break a run.
        }
    }

    /// <summary>Memory snapshot: managed heap, this process, and the WebView2 process tree.</summary>
    public static void Snapshot(string label) {
        if (!Enabled) return;
        try {
            var managed = GC.GetTotalMemory(false) / 1048576;
            long process;
            using (var self = Process.GetCurrentProcess()) process = self.WorkingSet64 / 1048576;

            long webview = 0;
            var count = 0;
            foreach (var p in Process.GetProcessesByName("msedgewebview2")) {
                try { webview += p.WorkingSet64; count++; } catch { }
                p.Dispose();
            }

            Line($"MEM  {label,-28} managed {managed,5} MB | process {process,5} MB | webview2 {count,2} procs {webview / 1048576,5} MB");
        } catch {
            // As above.
        }
    }

    sealed class Scope : IDisposable {
        readonly string _stage;
        readonly Stopwatch _clock = Stopwatch.StartNew();
        public Scope(string stage) => _stage = stage;
        public void Dispose() {
            _clock.Stop();
            Line($"TIME {_stage,-28} {_clock.ElapsedMilliseconds,6} ms");
        }
    }
}

/// <summary>
/// Shared polling policy for the ChatGPT automation: start fast, back off, and stop at a budget.
/// Kept separate and pure so the cadence can be asserted in tests without a browser.
/// </summary>
public static class PollPolicy {
    public const int FirstDelayMs = 100;
    public const int MaxDelayMs = 600;

    /// <summary>Delay before the next attempt: 100, 200, 300 … capped at 600 ms.</summary>
    public static int Delay(int attempt) => Math.Min(FirstDelayMs * Math.Max(attempt, 1), MaxDelayMs);

    /// <summary>
    /// The delays a budget allows, so a caller can poll until the budget is spent. Budgets are
    /// counted in requested delay rather than wall clock, which keeps tests deterministic and makes
    /// the ceiling predictable regardless of how slow an individual probe is.
    /// </summary>
    public static IEnumerable<int> Delays(int budgetMs) {
        var spent = 0;
        var attempt = 0;
        while (spent < budgetMs) {
            var delay = Delay(++attempt);
            spent += delay;
            yield return delay;
        }
    }

    /// <summary>How many probes a budget allows (the first probe happens before any delay).</summary>
    public static int Attempts(int budgetMs) => Delays(budgetMs).Count() + 1;
}

/// <summary>Bounded retention for log files, so diagnostics never grow without limit.</summary>
public static class LogRetention {
    /// <summary>
    /// "x.log" -> "x.1.log", "x.1.log" -> "x.2.log" … keeping at most <paramref name="keep"/> old
    /// files; the oldest is dropped. An empty or missing log is left alone.
    /// </summary>
    public static void Rotate(string path, int keep) {
        if (!File.Exists(path) || new FileInfo(path).Length == 0) return;

        string Numbered(int n) => System.IO.Path.ChangeExtension(path, null) + "." + n + System.IO.Path.GetExtension(path);

        if (File.Exists(Numbered(keep))) File.Delete(Numbered(keep));
        for (var n = keep - 1; n >= 1; n--)
            if (File.Exists(Numbered(n))) File.Move(Numbered(n), Numbered(n + 1));
        File.Move(path, Numbered(1));
    }

    /// <summary>Deletes all but the newest <paramref name="keep"/> files matching <paramref name="pattern"/>.</summary>
    public static void Prune(string directory, string pattern, int keep) {
        if (!Directory.Exists(directory)) return;
        foreach (var old in new DirectoryInfo(directory).GetFiles(pattern)
                     .OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name).Skip(keep)) {
            try { old.Delete(); } catch { /* a locked file is kept */ }
        }
    }
}

/// <summary>
/// Writes crash-yyyyMMdd-HHmmss.log for an exception nothing else handled. Contents: time, app
/// version, where it was caught, OS, and the exception with its stack — never settings, cookies,
/// WebView2 data or resume text. Never throws; keeps the newest <see cref="Kept"/> crash logs.
/// </summary>
public static class CrashLog {
    public const int Kept = 10;
    static readonly object Gate = new();
    static Exception? _lastLogged;

    /// <summary>Returns the file written, or null when this exception was already logged or writing failed.</summary>
    public static string? Write(Exception exception, string source, string? directory = null) {
        try {
            lock (Gate) {
                // The dispatcher and the AppDomain can both report the same fatal exception.
                if (ReferenceEquals(_lastLogged, exception)) return null;
                _lastLogged = exception;

                var dir = directory ?? Storage.DataDir;
                Directory.CreateDirectory(dir);

                var path = System.IO.Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                for (var n = 2; File.Exists(path); n++)
                    path = System.IO.Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}-{n}.log");

                var version = typeof(CrashLog).Assembly
                    .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                    .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "?";

                var text = new StringBuilder()
                    .AppendLine("Resume Builder crash report")
                    .AppendLine("Time:    " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                    .AppendLine("Version: " + version)
                    .AppendLine("Source:  " + source)
                    .AppendLine("OS:      " + Environment.OSVersion)
                    .AppendLine()
                    .AppendLine(exception.ToString())
                    .ToString();
                File.WriteAllText(path, text);

                LogRetention.Prune(dir, "crash-*.log", Kept);
                PerfLog.Line($"CRASH {source} {exception.GetType().Name} logged to {System.IO.Path.GetFileName(path)}");
                return path;
            }
        } catch {
            return null;
        }
    }
}
