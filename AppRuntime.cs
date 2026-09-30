using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace ResumeBuilder;

// ---------------------------------------------------------------------------
// Phase 1 install-readiness: the runtime rules that let Resume Builder run from
// C:\Program Files\ResumeBuilder with every writable file outside it.
//
//   %LOCALAPPDATA%\ResumeBuilder\    settings, tasks, logs, crash logs, results, WebView2 profiles
//   Documents\ResumeAutomation\      generated resumes (Resumes\) and CurrentResume.docx
//   install folder                   the executable and DLLs only — never written to
//
// Everything here is pure or injectable and holds no WPF, so it is tested directly. App.xaml.cs
// shows the messages and makes the decisions visible.
// ---------------------------------------------------------------------------

/// <summary>Default locations for a fresh install. No user name is ever written into code.</summary>
public static class AppPaths {
    /// <summary>Documents\ResumeAutomation — the user-visible output area.</summary>
    public static string DefaultAutomationFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ResumeAutomation");

    /// <summary>Documents\ResumeAutomation\Resumes — where generated resumes go on a fresh install.</summary>
    public static string DefaultResumeRoot => Path.Combine(DefaultAutomationFolder, "Resumes");

    /// <summary>
    /// First-run defaults for values a fresh install leaves blank. Only BLANK values are filled — a
    /// configured path, however unusual, is never replaced. Returns true when something was filled.
    /// </summary>
    public static bool ApplyFirstRunDefaults(AppSettings settings) {
        if (!string.IsNullOrWhiteSpace(settings.ResumeRootFolder)) return false;
        settings.ResumeRootFolder = DefaultResumeRoot;
        return true;
    }
}

/// <summary>
/// One Resume Builder per Windows session. Two copies would race on tasks.json and fight over the
/// same WebView2 profiles. The mutex name is fixed so the installer can use it too (Inno Setup
/// AppMutex) to refuse to upgrade while the app is running.
/// </summary>
public sealed class SingleInstance : IDisposable {
    /// <summary>Also the installer's AppMutex. Change both together.</summary>
    public const string MutexName = "ResumeBuilder.SingleInstance";

    readonly Mutex _mutex;
    SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>The instance's claim, or null when another instance already holds <paramref name="name"/>.</summary>
    public static SingleInstance? TryAcquire(string name = MutexName) {
        var mutex = new Mutex(initiallyOwned: false, name, out var createdNew);
        if (createdNew) return new SingleInstance(mutex);
        mutex.Dispose();
        return null;
    }

    public void Dispose() => _mutex.Dispose();

    /// <summary>
    /// Brings the already-running Resume Builder window to the front. Returns false when no window
    /// could be found (for example while the first instance is still starting up).
    /// </summary>
    public static bool ActivateExisting() {
        try {
            using var self = Process.GetCurrentProcess();
            foreach (var other in Process.GetProcessesByName(self.ProcessName)) {
                using (other) {
                    if (other.Id == self.Id) continue;
                    var handle = other.MainWindowHandle;
                    if (handle == IntPtr.Zero) continue;
                    if (IsIconic(handle)) ShowWindow(handle, SwRestore);
                    SetForegroundWindow(handle);
                    return true;
                }
            }
        } catch {
            // Fall through to the caller's message.
        }
        return false;
    }

    const int SwRestore = 9;
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
}

/// <summary>What the WebView2 runtime check found.</summary>
public sealed class WebView2Status {
    public bool Available { get; init; }
    public string? Version { get; init; }

    /// <summary>Why it is unavailable, in plain words (never a raw exception dump).</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Resume Builder's two embedded browsers need the Microsoft Edge WebView2 Runtime. It is checked
/// once at startup with the WebView2 SDK's own detection API, so a missing runtime is a clear
/// message instead of a raw exception. Nothing is downloaded or installed here.
/// </summary>
public static class WebView2Runtime {
    /// <summary>Microsoft's WebView2 Runtime download page, named in the message (never opened automatically).</summary>
    public const string DownloadPage = "https://developer.microsoft.com/microsoft-edge/webview2/";

    public static WebView2Status Check() =>
        Check(() => Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString());

    /// <summary>The check itself, with the SDK probe injected so every outcome is testable.</summary>
    public static WebView2Status Check(Func<string?> probe) {
        try {
            var version = probe();
            return string.IsNullOrWhiteSpace(version)
                ? new WebView2Status { Available = false, Reason = "The Microsoft Edge WebView2 Runtime is not installed." }
                : new WebView2Status { Available = true, Version = version };
        } catch (Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException) {
            return new WebView2Status { Available = false, Reason = "The Microsoft Edge WebView2 Runtime is not installed." };
        } catch (Exception ex) {
            return new WebView2Status { Available = false, Reason = "The WebView2 Runtime could not be checked (" + ex.GetType().Name + ")." };
        }
    }

    public static string Message(WebView2Status status) =>
        "Resume Builder needs the Microsoft Edge WebView2 Runtime for its ChatGPT and Job Browser panes.\n\n" +
        (status.Reason ?? "") + "\n\n" +
        "Install the Evergreen WebView2 Runtime from Microsoft:\n" + DownloadPage + "\n\n" +
        "Click Yes to check again after installing it, or No to exit.";
}

/// <summary>
/// What a fresh install still needs before a job can run. Pure: the caller says whether a candidate
/// profile exists. Configured values are only read, never changed; nothing is copied.
/// </summary>
public static class SetupCheck {
    public sealed record Item(string What, string Section);

    public static IReadOnlyList<Item> Missing(AppSettings settings, bool candidateProfileExists) {
        var missing = new List<Item>();

        if (PromptModes.IsNormal(settings.PromptMode)) {
            if (!FileExists(settings.NormalPrompt))
                missing.Add(new Item("the Normal Prompt file (Prompt Mode is Normal)", "Prompts"));
        } else if (!FileExists(settings.MasterPrompt)) {
            missing.Add(new Item("the Master Prompt file", "Prompts"));
        }

        if (!candidateProfileExists)
            missing.Add(new Item("your candidate profile", "Candidate Profile"));

        return missing;
    }

    /// <summary>The first-run notice, or null when everything needed is in place.</summary>
    public static string? Describe(IReadOnlyList<Item> missing, string resumeRoot) =>
        missing.Count == 0 ? null :
        "Resume Builder is almost ready. Before a job can run, set up:\n\n" +
        string.Join("\n", missing.Select(m => $"  •  {m.What} — Settings → {m.Section}")) +
        "\n\nGenerated resumes will be saved in:\n" + resumeRoot +
        "\n(change it under Settings → Folders).\n\nOpen Settings now?";

    static bool FileExists(string? path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path.Trim());
}
