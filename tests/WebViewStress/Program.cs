using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using ResumeBuilder;
using Application = System.Windows.Application;
using Window = System.Windows.Window;

// A6.6.12 — 50-cycle WebView2 lifecycle stress test.
//
// Drives the PRODUCTION ChatHost with the same create/dispose steps MainWindow uses: shared
// environment, BrowserProcessExited plus an OS process-handle wait, and the same bounded timeout.
// There is no second lifecycle implementation here.
//
// No resume prompts are sent. This measures browser lifetime and memory only; the real 3-job queue
// run remains the end-to-end validation. A temporary user-data folder is used so the real ChatGPT
// login profile is never touched.
//
// Console output is flushed on every line so a hang is visible immediately (the previous harness
// buffered everything until shutdown, which looked like a silent stall). An overall wall-clock
// budget aborts a stuck run with a clear message instead of hanging forever.

static class WebViewStress {
    const int Cycles = 50;
    /// <summary>Overall budget for the whole harness (create+dispose × 50 + margins).</summary>
    static readonly TimeSpan OverallBudget = TimeSpan.FromMinutes(12);
    static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(10);
    static readonly TimeSpan EnsureBudget = TimeSpan.FromSeconds(45);
    static readonly List<string> Log = new();
    static int failures;
    static readonly object Gate = new();
    static readonly Stopwatch Wall = Stopwatch.StartNew();

    static void Emit(string line) {
        lock (Gate) {
            Log.Add(line);
            Console.WriteLine(line);
            Console.Out.Flush();
        }
    }

    static void Check(string label, bool ok, string? detail = null) {
        Emit((ok ? "PASS  " : "FAIL  ") + label + (detail is null ? "" : "  [" + detail + "]"));
        if (!ok) failures++;
    }

    static void ThrowIfOverBudget(string stage) {
        if (Wall.Elapsed > OverallBudget)
            throw new TimeoutException(
                $"WebViewStress overall budget exceeded at {stage} after {Wall.Elapsed:mm\\:ss} " +
                $"(limit {OverallBudget.TotalMinutes:0} min).");
    }

    static int WebViewCount() => Process.GetProcessesByName("msedgewebview2").Length;

    static long WebViewMb() {
        long total = 0;
        foreach (var p in Process.GetProcessesByName("msedgewebview2")) { try { total += p.WorkingSet64; } catch { } p.Dispose(); }
        return total / 1048576;
    }

    static long ManagedMb() => GC.GetTotalMemory(false) / 1048576;

    [STAThread]
    static void Main() {
        // Unbuffered console so progress appears even when stdout is redirected.
        try { Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true }); } catch { }

        Emit($"WebViewStress starting — {Cycles} cycles, overall budget {OverallBudget.TotalMinutes:0} min");
        Emit($"cwd={Environment.CurrentDirectory}");
        Emit($"temp={Path.GetTempPath()}");

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) => {
            try {
                Emit("WPF Application.Startup fired — entering RunAsync");
                await RunAsync();
            }
            catch (Exception ex) {
                Emit("HARNESS ERROR: " + ex);
                failures++;
            }
            finally {
                Emit("");
                Emit(failures == 0 ? "ALL STRESS CHECKS PASSED" : failures + " STRESS CHECK(S) FAILED");
                Emit($"wall clock: {Wall.Elapsed:mm\\:ss\\.fff}");
                app.Shutdown();
            }
        };
        app.Run();
    }

    static async Task RunAsync() {
        ThrowIfOverBudget("before window");
        Emit("creating off-screen host window…");
        var window = new Window { Width = 400, Height = 300, Left = -32000, Top = -32000, ShowInTaskbar = false, Title = "WebViewStress" };
        var panel = new Border();
        window.Content = panel;
        window.Show();
        Emit("host window shown");

        var userData = Path.Combine(Path.GetTempPath(), "rb-stress-" + Guid.NewGuid().ToString("N"));
        Emit("user-data folder: " + userData);
        CoreWebView2Environment? env = null;
        Microsoft.Web.WebView2.Wpf.WebView2? view = null;

        var baselineProcs = WebViewCount();
        var baselineMb = WebViewMb();
        Emit($"baseline before any cycle: {baselineProcs} webview2 processes, {baselineMb} MB");
        Emit("");

        var pids = new List<int>();
        var started = new List<DateTime>();   // pid alone is ambiguous: Windows reuses ids after exit
        var afterExitMb = new List<long>();
        var afterExitProcs = new List<int>();
        var managedMb = new List<long>();
        var exitMs = new List<long>();
        var liveNow = 0; var maxLive = 0;
        var handlersAttached = 0; var handlersDetached = 0;
        var createdDuringRelease = false;

        ChatHost? host = null;
        host = new ChatHost(
            create: async () => {
                Emit("  create: begin");
                if (host!.IsReleasing) createdDuringRelease = true;
                if (env is null) {
                    Emit("  create: CoreWebView2Environment.CreateAsync…");
                    env = await CoreWebView2Environment.CreateAsync(null, userData)
                        .WaitAsync(EnsureBudget);
                    Emit("  create: environment ready");
                }
                var v = new Microsoft.Web.WebView2.Wpf.WebView2();
                panel.Child = v;
                Emit("  create: EnsureCoreWebView2Async…");
                await v.EnsureCoreWebView2Async(env).WaitAsync(EnsureBudget);
                v.Source = new Uri("about:blank");
                view = v;
                liveNow++; maxLive = Math.Max(maxLive, liveNow);
                var pid = (int)v.CoreWebView2.BrowserProcessId;
                pids.Add(pid);
                started.Add(StartTimeOf(pid));
                Emit($"  create: done pid={pid}");
                return pid;
            },
            dispose: async () => {
                var v = view; view = null;
                if (v is null) { Emit("  dispose: nothing to dispose"); return true; }
                var pid = pids[^1];
                Emit($"  dispose: begin pid={pid}");

                var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnExited(object? _, CoreWebView2BrowserProcessExitedEventArgs e) {
                    if (e.BrowserProcessId == pid) exited.TrySetResult(true);
                }
                if (env is not null) { env.BrowserProcessExited += OnExited; handlersAttached++; }

                var clock = Stopwatch.StartNew();
                panel.Child = null;
                v.Dispose();
                var confirmed = await WaitForExitAsync(exited.Task, pid);
                clock.Stop();

                if (env is not null) { env.BrowserProcessExited -= OnExited; handlersDetached++; }
                if (confirmed) liveNow--;
                exitMs.Add(clock.ElapsedMilliseconds);
                Emit($"  dispose: {(confirmed ? "confirmed" : "TIMED OUT")} in {clock.ElapsedMilliseconds} ms");
                return confirmed;
            });

        var staleAliveAtCreate = 0;
        var reusedPids = 0;

        for (var cycle = 1; cycle <= Cycles; cycle++) {
            ThrowIfOverBudget($"before cycle {cycle}");
            Emit($"cycle {cycle}/{Cycles}: EnsureAsync…");
            await host.EnsureAsync().WaitAsync(EnsureBudget);
            var pid = host.BrowserProcessId;

            if (host.IsAlive && liveNow != 1) Check($"cycle {cycle}: exactly one browser alive", false, "live=" + liveNow);

            // The invariant that matters: no browser from an EARLIER cycle may still be running when a
            // new one is created. Windows is free to hand the same pid back once a process has exited,
            // so a repeated pid value is normal; a previous browser still alive would not be.
            var stillAlive = 0;
            for (var i = 0; i < pids.Count - 1; i++) if (IsSameProcessAlive(pids[i], started[i])) stillAlive++;
            if (stillAlive > 0) { staleAliveAtCreate += stillAlive; Emit($"cycle {cycle}: {stillAlive} earlier browser(s) STILL ALIVE"); }
            if (pids.Take(pids.Count - 1).Contains(pid)) reusedPids++;

            Emit($"cycle {cycle}/{Cycles}: RecycleAsync…");
            await host.RecycleAsync().WaitAsync(ExitTimeout + TimeSpan.FromSeconds(5));

            var procs = WebViewCount();
            var mb = WebViewMb();
            afterExitProcs.Add(procs);
            afterExitMb.Add(mb);
            managedMb.Add(ManagedMb());

            if (cycle is 1 or 10 or 20 or 30 or 40 or 50 || host.LastShutdownTimedOut)
                Emit($"cycle {cycle,2}: pid {pid,6}  exit {(host.LastShutdownTimedOut ? "TIMED OUT" : exitMs[^1] + " ms"),10}  " +
                     $"after exit: {procs,2} procs {mb,5} MB  managed {managedMb[^1],3} MB");
        }

        Emit("");
        Check("50 browsers created", host.Creations == Cycles, host.Creations.ToString());
        Check("50 browsers disposed", host.Disposals == Cycles, host.Disposals.ToString());
        Check("no earlier browser was ever alive when a new one was created", staleAliveAtCreate == 0,
            staleAliveAtCreate + " stale");
        Check("every cycle produced its own browser lifecycle", pids.Count == Cycles,
            $"{pids.Distinct().Count()} distinct pid values, {reusedPids} reused by Windows after exit");
        Check("never more than one browser alive at a time", maxLive == 1, "max " + maxLive);
        Check("no browser was created while a release was in flight", !createdDuringRelease);
        Check("zero recycle timeouts", host.TimeoutCount == 0, host.TimeoutCount + " timeouts");
        Check("nothing alive after the last cycle", !host.IsAlive && !host.IsReleasing);
        var orphans = 0;
        for (var i = 0; i < pids.Count; i++) if (IsSameProcessAlive(pids[i], started[i])) orphans++;
        Check("no orphaned browser process from any cycle", orphans == 0, orphans + " orphans");
        Check("event handlers attached and detached in equal numbers",
            handlersAttached == handlersDetached && handlersAttached == Cycles,
            handlersAttached + " attached / " + handlersDetached + " detached");

        Check("process count returns to baseline after every cycle",
            afterExitProcs.All(p => p <= baselineProcs),
            "max after exit = " + afterExitProcs.Max() + ", baseline " + baselineProcs);

        var firstTen = managedMb.Take(10).Average();
        var lastTen = managedMb.TakeLast(10).Average();
        Check("managed memory does not grow continuously", lastTen - firstTen < 10,
            $"first10 avg {firstTen:F1} MB -> last10 avg {lastTen:F1} MB");

        var min = afterExitMb.Min(); var max = afterExitMb.Max(); var avg = afterExitMb.Average();
        Check("WebView2 memory after exit stays in a stable band", max - min <= 150,
            $"min {min} MB, max {max} MB, spread {max - min} MB");

        Emit("");
        Emit("MEMORY SUMMARY (WebView2 working set after the browser process actually exited)");
        foreach (var c in new[] { 1, 10, 20, 30, 40, 50 })
            Emit($"  Cycle {c,2} after exit:   {afterExitMb[c - 1],5} MB   ({afterExitProcs[c - 1]} processes)");
        Emit("");
        Emit($"  minimum baseline:        {min,5} MB");
        Emit($"  maximum baseline:        {max,5} MB");
        Emit($"  average baseline:        {avg,5:F1} MB");
        Emit($"  cycle 1 vs cycle 50:     {afterExitMb[^1] - afterExitMb[0],+5} MB");
        Emit($"  managed cycle 1 / 50:    {managedMb[0]} MB / {managedMb[^1]} MB");
        Emit($"  recycle timeouts:        {host.TimeoutCount}");
        Emit($"  orphan browsers:         {orphans}");
        Emit($"  pid values reused by Windows after exit: {reusedPids}");
        Emit($"  exit confirmation time:  min {exitMs.Min()} ms, max {exitMs.Max()} ms, avg {exitMs.Average():F0} ms");

        try { Directory.Delete(userData, true); } catch { }
        window.Close();
    }

    static DateTime StartTimeOf(int pid) {
        try { using var p = Process.GetProcessById(pid); return p.StartTime; }
        catch { return DateTime.MinValue; }
    }

    /// <summary>Same pid AND same start time — otherwise Windows has simply reused the id.</summary>
    static bool IsSameProcessAlive(int pid, DateTime started) {
        try {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited && p.StartTime == started;
        } catch { return false; }
    }

    /// <summary>Same wait the application uses: the exit event and the process handle, bounded.</summary>
    static async Task<bool> WaitForExitAsync(Task<bool> exitEvent, int pid) {
        using var cancellation = new System.Threading.CancellationTokenSource(ExitTimeout);
        Task handleWait;
        try {
            using var process = Process.GetProcessById(pid);
            handleWait = process.WaitForExitAsync(cancellation.Token);
        } catch (ArgumentException) { return true; }
        catch { handleWait = Task.Delay(System.Threading.Timeout.Infinite, cancellation.Token); }

        var finished = await Task.WhenAny(exitEvent, handleWait);
        if (finished == exitEvent) return true;
        try { await handleWait; return true; } catch { return false; }
    }
}
