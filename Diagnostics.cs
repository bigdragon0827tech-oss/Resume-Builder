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

    public static void Line(string text) {
        if (!Enabled) return;
        try {
            lock (Gate) {
                Directory.CreateDirectory(Storage.DataDir);
                var path = Path;
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) File.Delete(path);
                File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + text + Environment.NewLine);
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
