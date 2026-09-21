using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Brush = System.Windows.Media.Brush;
using System.Text.Json;
using System.Windows.Controls;

namespace ResumeBuilder;

/// <summary>
/// The built-in job browser: an ordinary WebView2 on its own profile, in its own window.
///
/// It shares nothing with the ChatGPT pane — different user-data folder, therefore a different
/// environment and a different browser process tree. It is not part of ChatHost's recycling, has no
/// completion watcher, no capture watchdog and no clipboard involvement. Signing in happens inside
/// the site, exactly as it would in any browser.
///
/// Phase 2: Import Current Job reads the page once, through JobrightPageExtractor, and hands the
/// result to <see cref="ImportJob"/>. This window never touches the queue or storage itself.
/// </summary>
public partial class JobBrowserWindow : System.Windows.Controls.UserControl {
    /// <summary>
    /// Cached across opens so re-opening the window reuses the same profile without asking WebView2
    /// to attach a second environment to a folder it already knows. Only the real profile is cached.
    /// </summary>
    static CoreWebView2Environment? _environment;

    Microsoft.Web.WebView2.Wpf.WebView2? _view;
    JobrightPageExtractor? _extractor;
    readonly string _startUrl;
    readonly string _profileFolder;
    bool _navigating;
    bool _importing;
    bool _stopFinding;

    /// <summary>Jobs found by the current scan that are NOT already in Resume Builder.</summary>
    readonly List<string> _foundJobUrls = new();

    /// <summary>Normalized keys of every link the current scan has seen, new or already imported.</summary>
    readonly HashSet<string> _seenJobKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many links the current scan passed over because they are already in Resume Builder.</summary>
    int _skippedExisting;

    /// <summary>
    /// Where an extracted job goes. MainWindow supplies it and runs the shared importer against its
    /// live task list, so this window stays free of storage and queue code.
    /// </summary>
    public Func<JobImportData, JobImportOutcome>? ImportJob { get; set; }
    
    public Func<string, bool>? JobExists { get; set; }

    /// <summary>
    /// Where an application address goes when the user clicks Apply: (job page address, destination).
    /// MainWindow supplies it, applies <see cref="ApplyCapture"/> to its live tasks and saves, so this
    /// window still never touches storage.
    /// </summary>
    public Func<string, string, ApplyCaptureResult>? RecordApplyUrl { get; set; }
    public JobBrowserWindow() : this(null, null) { }

    /// <summary>
    /// The tests open about:blank on a throwaway profile, so no third-party request is made and the
    /// real signed-in profile is never touched while the plumbing is verified.
    /// </summary>
    public JobBrowserWindow(string? startUrl, string? userDataFolder) {
        InitializeComponent();
        _startUrl = string.IsNullOrWhiteSpace(startUrl) ? JobBrowser.HomeUrl : startUrl;
        _profileFolder = string.IsNullOrWhiteSpace(userDataFolder) ? JobBrowser.UserDataFolder : userDataFolder;

        Unloaded += (_, _) => { /* keep browser alive across shell navigation; MainWindow.Shutdown disposes */ };
    }

    /// <summary>Called by the shell when the app closes so the Jobright profile is released cleanly.</summary>
    public void Shutdown() => DisposeBrowser();

    /// <summary>
    /// Creates the Jobright WebView2 on first use. Safe to call repeatedly; no-ops when already ready.
    /// Profile folder and automation contracts are unchanged.
    /// </summary>
    public Task EnsureReadyAsync() => InitializeBrowserAsync();

    /// <summary>True once the browser is usable — the tests wait on this instead of a sleep.</summary>
    public bool IsReady => _view?.CoreWebView2 is not null;

    /// <summary>True while an import is running. The button is disabled for exactly this long.</summary>
    public bool IsImporting => _importing;

    /// <summary>The profile this window is running on, so separation can be asserted.</summary>
    public string ProfileFolder => _profileFolder;

    async Task InitializeBrowserAsync() {
        if (_view is not null) return;

        try {
            SetBrowserBusy(true, "Starting Jobright browser…");
            PerfLog.Line("JOBBROWSER initialize");
            Directory.CreateDirectory(_profileFolder);

            // A separate user-data folder is what makes this a separate browser: its own cookies,
            // its own sign-in and its own processes. The ChatGPT environment is never reused here.
            var environment = _profileFolder == JobBrowser.UserDataFolder
                ? _environment ??= await CoreWebView2Environment.CreateAsync(null, JobBrowser.UserDataFolder)
                : await CoreWebView2Environment.CreateAsync(null, _profileFolder);

            var view = new Microsoft.Web.WebView2.Wpf.WebView2();
            BrowserHost.Child = view;
            await view.EnsureCoreWebView2Async(environment);
            _view = view;

            // Ordinary browser behaviour: nothing is disabled and nothing is injected. The page is
            // read only by the extractor, only when Import is clicked.
            view.CoreWebView2.HistoryChanged += (_, _) => UpdateNavigationButtons();
            view.CoreWebView2.SourceChanged += (_, _) => OnAddressChanged();
            view.CoreWebView2.NavigationStarting += (_, e) => {
                // Same-page Apply: the user's click takes this page off a job page to the application site.
                // Observed only; the navigation itself is never changed.
                ReportApplyDestination(view.Source?.ToString(), e.Uri, "navigation");
                _navigating = true;
                SetBrowserBusy(true, "Loading page…");
                UpdateImportButton();
            };
            // New-window Apply. Handled is never set, so the window opens exactly as it did before.
            view.CoreWebView2.NewWindowRequested += (_, e) =>
                ReportApplyDestination(view.Source?.ToString(), e.Uri, "new-window");
            view.NavigationCompleted += (_, _) => {
                _navigating = false;
                SetBrowserBusy(false);
                UpdateNavigationButtons();
                UpdateImportButton();
            };

            _extractor = new JobrightPageExtractor(
                script => view.CoreWebView2.ExecuteScriptAsync(script),
                () => view.Source?.ToString());

            view.Source = new Uri(_startUrl);

            HomeButton.IsEnabled = true;
            RefreshButton.IsEnabled = true;
            OnAddressChanged();
            UpdateNavigationButtons();

            PerfLog.Line("JOBBROWSER ready");
            StatusText.Text = "Sign in to the site as usual, open a job, then click Import Current Job.";
            SetBrowserBusy(false);
        } catch (Exception ex) {
            // A browser failure here must stay here: the queue and the ChatGPT pane are untouched.
            PerfLog.Line("JOBBROWSER initialize FAILED: " + ex.Message);
            StatusText.Text = "The job browser could not start: " + ex.Message;
            SetBrowserBusy(false);
        }
    }

    void SetBrowserBusy(bool busy, string? message = null) {
        if (BrowserBusyOverlay is null) return;
        BrowserBusyOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy && message is not null && BrowserBusyText is not null)
            BrowserBusyText.Text = message;
    }

    void OnAddressChanged() {
        var url = _view?.Source?.ToString() ?? "";
        AddressBox.Text = url;
        PerfLog.Line("JOBBROWSER navigate " + JobBrowser.SafeForLog(url));
        UpdateImportButton();
    }

    void UpdateNavigationButtons() {
        var core = _view?.CoreWebView2;
        BackButton.IsEnabled = core?.CanGoBack == true;
        ForwardButton.IsEnabled = core?.CanGoForward == true;
    }

    void StopFinding_Click(object sender, RoutedEventArgs e)
    {
        _stopFinding = true;

        StatusText.Text =
            "Stopping job scan...";

        PerfLog.Line(
            "JOBBROWSER find jobs stop requested");
    }   


    int GetJobTarget()
    {
        if (JobTargetBox.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Content.ToString(), out var value))
        {
            return value;
        }

        return 50;
    }

    /// <summary>
    /// Import is offered only when it can work: the browser is up, nothing is loading, the address
    /// is a single job, something is ready to receive the job, and no import is already running.
    /// </summary>
    /// <summary>
    /// Updates which job-browser actions are currently available.
    /// Import Current Job is for a single job page.
    /// Find Jobs is for other Jobright pages such as Recommended/search results.
    /// </summary>
    void UpdateImportButton() {
        var url = _view?.Source?.ToString() ?? "";
        var onJob = JobrightPageExtractor.IsJobPage(url);

        var onJobright = Uri.TryCreate(url, UriKind.Absolute, out var uri)
                        && (uri.Host.Equals("jobright.ai", StringComparison.OrdinalIgnoreCase)
                            || uri.Host.EndsWith(".jobright.ai", StringComparison.OrdinalIgnoreCase));

        ImportButton.IsEnabled =
            IsReady &&
            !_navigating &&
            !_importing &&
            onJob &&
            ImportJob is not null;

        ImportButton.ToolTip = onJob
            ? "Add this job to the queue."
            : "Open a single Jobright job to import it into the queue.";

        FindJobsButton.IsEnabled =
            IsReady &&
            !_navigating &&
            !_importing &&
            onJobright &&
            !onJob;

        FindJobsButton.ToolTip = onJob
            ? "Go back to a Jobright results page to find jobs."
            : "Find jobs on the current Jobright results page.";

        AutoImportButton.IsEnabled =
            IsReady &&
            !_navigating &&
            !_importing &&
            _foundJobUrls.Count > 0;

        AutoImportButton.ToolTip = _foundJobUrls.Count > 0
            ? $"Import {_foundJobUrls.Count} new jobs one by one."
            : "Click Find Jobs first.";
    }

    /// <summary>
    /// Called when the page starts leaving (same page) or asks for a new window. Only a single Jobright
    /// job page heading OFF jobright.ai is considered; jobright-to-jobright navigation (browsing, Auto
    /// Import) returns at once. It reads nothing from the page and never blocks the navigation.
    /// </summary>
    void ReportApplyDestination(string? jobPageUrl, string? destination, string via) {
        try {
            if (!JobrightPageExtractor.IsJobPage(jobPageUrl)) return;
            if (!Uri.TryCreate(destination, UriKind.Absolute, out var dest) ||
                dest.Host.Equals("jobright.ai", StringComparison.OrdinalIgnoreCase) ||
                dest.Host.EndsWith(".jobright.ai", StringComparison.OrdinalIgnoreCase)) return;

            var result = RecordApplyUrl?.Invoke(jobPageUrl!, destination!) ?? ApplyCaptureResult.UnknownJob;
            PerfLog.Line($"JOBBROWSER apply seen {JobBrowser.SafeForLog(destination)} via {via} -> {result}");

            switch (result) {
                case ApplyCaptureResult.Recorded:
                    StatusText.Text = "Application link recorded for this job.";
                    break;
                case ApplyCaptureResult.UnknownJob:
                    StatusText.Text = "This job is not in Resume Builder yet; import it to record its application link.";
                    break;
            }
        } catch (Exception ex) {
            // Recording is a convenience: a failure here must never disturb browsing.
            PerfLog.Line("JOBBROWSER apply capture failed " + ex.GetType().Name);
        }
    }

    async void Import_Click(object sender, RoutedEventArgs e) => await ImportCurrentJobAsync();

    async void AutoImport_Click(object sender, RoutedEventArgs e) =>
        await AutoImportFoundJobsAsync();

    /// <summary>
    /// One import, start to finish. The flag and the disabled button together make a double click
    /// a no-op, so the same job can never be submitted twice in flight.
    /// </summary>
    public async Task ImportCurrentJobAsync() {
        if (_importing || _extractor is null || ImportJob is null) return;

        _importing = true;
        UpdateImportButton();
        StatusText.Text = "Reading current job…";
        PerfLog.Line("JOBBROWSER import requested " + JobBrowser.SafeForLog(_view?.Source?.ToString()));

        try {
            JobImportData? data;
            try {
                data = await _extractor.ExtractCurrentJobAsync();
            } catch (JobExtractionException ex) {
                PerfLog.Line("JOBBROWSER import failed " + ex.Message +
                             (ex.InnerException is null ? "" : " (" + ex.InnerException.GetType().Name + ")"));
                ShowFailure(ex.Message);
                return;
            }

            if (data is null) {
                PerfLog.Line("JOBBROWSER import failed not a job page");
                ShowFailure("This is not a Jobright job page. Open a single job first.");
                return;
            }

            PerfLog.Line("JOBBROWSER extract success");
            var outcome = ImportJob(data);

            switch (outcome.Kind) {
                case JobImportKind.Imported:
                    PerfLog.Line("JOBBROWSER import success " + outcome.JobId);
                    ShowResult("Imported", $"{outcome.Title}\n{outcome.Company}\n\nStatus: {outcome.ApplicationStatus}", Success);
                    StatusText.Text = "Imported — the job is in the queue.";
                    break;

                case JobImportKind.Duplicate:
                    PerfLog.Line("JOBBROWSER duplicate " + outcome.JobId);
                    ShowResult("Already imported", $"{outcome.Title}\n{outcome.Company}", Neutral);
                    StatusText.Text = "This job is already in the queue; nothing was added.";
                    break;

                default:
                    PerfLog.Line("JOBBROWSER import failed " + outcome.Reason);
                    ShowFailure(outcome.Reason);
                    break;
            }
        } catch (Exception ex) {
            // Anything unexpected is logged by type only and shown in plain words.
            PerfLog.Line("JOBBROWSER import failed unexpected " + ex.GetType().Name);
            ShowFailure("Something went wrong while reading the page. Try again once it has finished loading.");
        } finally {
            _importing = false;
            UpdateImportButton();
        }
    }

    /// <summary>
    /// Adds the job links on screen that are not already in Resume Builder, up to <paramref name="target"/>.
    /// Links already in the queue are counted as skipped and never count toward the target.
    /// Returns how many new jobs this pass added.
    /// </summary>
    async Task<int> CollectJobsFromCurrentPageAsync(int target)
    {
        if (_view?.CoreWebView2 is null)
            return 0;

        const string script = """
            (() => {
                const urls = Array.from(
                    document.querySelectorAll('a[href*="/jobs/info/"]')
                )
                .map(a => a.href)
                .filter(Boolean);

                return [...new Set(urls)];
            })()
            """;

        var result = await _view.CoreWebView2.ExecuteScriptAsync(script);

        var urls =
            JsonSerializer.Deserialize<string[]>(result)
            ?? Array.Empty<string>();

        var added = 0;

        foreach (var url in urls)
        {
            if (_foundJobUrls.Count >= target)
                break;

            if (string.IsNullOrWhiteSpace(url))
                continue;

            // One key per job, so the same card seen again after a scroll is not counted twice.
            var key = JobUrls.Normalize(url) ?? url.Trim();
            if (!_seenJobKeys.Add(key))
                continue;

            if (JobExists?.Invoke(url) == true)
            {
                _skippedExisting++;
                continue;
            }

            _foundJobUrls.Add(url);
            added++;
        }

        FoundJobsText.Text =
            $"Found: {_foundJobUrls.Count}";

        PerfLog.Line(
            $"JOBBROWSER collected new={_foundJobUrls.Count} skipped existing={_skippedExisting}");

        return added;
    }

    async Task WaitForJobListReadyAsync()
    {
        if (_view?.CoreWebView2 is null)
            return;

        for (var i = 0; i < 20; i++)
        {
            const string script = """
            (() => {
                return document.querySelectorAll(
                    'a[href*="/jobs/info/"]'
                ).length;
            })()
            """;

            var result =
                await _view.CoreWebView2.ExecuteScriptAsync(script);

            if (int.TryParse(result, out var count) &&
                count > 0)
            {
                PerfLog.Line(
                    $"JOBBROWSER job list ready count={count}");

                return;
            }

            await Task.Delay(500);
        }

        PerfLog.Line(
            "JOBBROWSER job list readiness timeout");
    }

    /// <summary>
    /// Scrolls Jobright's internal results container (never the window) and waits for more cards.
    /// Returns false when the container could not be scrolled any further or was not found.
    /// </summary>
    async Task<bool> ScrollDownAsync()
    {
        if (_view?.CoreWebView2 is null)
            return false;

        const string script = """
        (() => {
            const container =
                document.querySelector(
                    'div.index_jobs-page-main-content__qd__a'
                );

            if (!container)
                return "container not found";

            const before = {
                top: container.scrollTop,
                height: container.scrollHeight
            };

            container.scrollTop +=
                Math.floor(container.clientHeight * 0.8);

            const after = {
                top: container.scrollTop,
                height: container.scrollHeight
            };

            return JSON.stringify({
                before,
                after
            });
        })()
        """;

        var result =
            await _view.CoreWebView2.ExecuteScriptAsync(script);

        PerfLog.Line(
            "JOBBROWSER scroll result " + result);

        var moved = false;
        try
        {
            // ExecuteScriptAsync JSON-encodes the returned string, so unwrap it before parsing.
            var inner = JsonSerializer.Deserialize<string>(result);
            if (inner is not null && inner.StartsWith('{'))
            {
                using var doc = JsonDocument.Parse(inner);
                var before = doc.RootElement.GetProperty("before").GetProperty("top").GetDouble();
                var after = doc.RootElement.GetProperty("after").GetProperty("top").GetDouble();
                moved = after > before;
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            moved = false;
        }

        await Task.Delay(3000);
        return moved;
    }

    /// <summary>
    /// Rounds in a row with no new job and no scroll movement before the scan decides it has
    /// reached the end of Jobright's list (the delay after each scroll gives it time to load more).
    /// </summary>
    const int EndOfListRounds = 3;

    /// <summary>
    /// Scrolls until <paramref name="target"/> jobs that are NOT already in Resume Builder have been
    /// found. Jobs already imported are skipped and do not count toward the target.
    /// </summary>
    async Task FindJobsUntilTargetAsync(int target)
    {
        _foundJobUrls.Clear();
        _seenJobKeys.Clear();
        _skippedExisting = 0;

        var idleRounds = 0;
        var reachedEnd = false;

        while (_foundJobUrls.Count < target && !_stopFinding)
        {
            StatusText.Text =
                $"Finding new jobs... {_foundJobUrls.Count}/{target}" +
                (_skippedExisting > 0 ? $" (skipped {_skippedExisting} already in Resume Builder)" : "");

            var added = await CollectJobsFromCurrentPageAsync(target);

            if (_stopFinding || _foundJobUrls.Count >= target)
                break;

            var moved = await ScrollDownAsync();

            idleRounds = added == 0 && !moved ? idleRounds + 1 : 0;
            if (idleRounds >= EndOfListRounds)
            {
                reachedEnd = true;
                break;
            }
        }

        var skipped = _skippedExisting > 0
            ? $" Skipped {_skippedExisting} already in Resume Builder."
            : "";

        StatusText.Text =
            _foundJobUrls.Count >= target ? $"Found {_foundJobUrls.Count} new jobs.{skipped}"
            : reachedEnd ? $"Reached the end of the list — found {_foundJobUrls.Count} of {target} new jobs.{skipped}"
            : $"Stopped — found {_foundJobUrls.Count} new jobs.{skipped}";

        PerfLog.Line(
            $"JOBBROWSER find finished new={_foundJobUrls.Count} target={target} " +
            $"skipped existing={_skippedExisting} end={reachedEnd} stopped={_stopFinding}");
    }

    async void FindJobs_Click(object sender, RoutedEventArgs e)
    {
        if (_view?.CoreWebView2 is null ||
            _navigating ||
            _importing)
            return;

        FindJobsButton.IsEnabled = false;

        try
        {
            _stopFinding = false;
            StopFindingButton.IsEnabled = true;
            await WaitForJobListReadyAsync();

            await FindJobsUntilTargetAsync(GetJobTarget());
            
        }
        finally
        {
            StopFindingButton.IsEnabled = false;
            FindJobsButton.IsEnabled = true;
            UpdateImportButton();
        }
    }
    

    
    /// <summary>
    /// Navigate to one job URL and wait until WebView2 reports that navigation completed.
    /// </summary>
    async Task<bool> NavigateAndWaitAsync(string url, TimeSpan timeout) {
        var view = _view;
        if (view?.CoreWebView2 is null)
            return false;

        var tcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs e) {
            view.NavigationCompleted -= Completed;
            tcs.TrySetResult(e.IsSuccess);
        }

        view.NavigationCompleted += Completed;

        try {
            view.CoreWebView2.Navigate(url);

            var finished = await Task.WhenAny(
                tcs.Task,
                Task.Delay(timeout));

            if (finished != tcs.Task) {
                view.NavigationCompleted -= Completed;
                return false;
            }

            return await tcs.Task;
        } catch {
            view.NavigationCompleted -= Completed;
            return false;
        }
    }


    /// <summary>
    /// Navigation can finish before Jobright has populated its job-detail helper data.
    /// Retry the existing extractor for a short period instead of reading stale/empty data.
    /// </summary>
    async Task<JobImportData> ExtractWithRetryAsync() {
        if (_extractor is null)
            throw new JobExtractionException(
                "The job extractor is not ready.");

        JobExtractionException? last = null;

        for (var attempt = 0; attempt < 20; attempt++) {
            try {
                var data = await _extractor.ExtractCurrentJobAsync();

                if (data is not null)
                    return data;
            } catch (JobExtractionException ex) {
                last = ex;
            }

            await Task.Delay(500);
        }

        throw last ??
              new JobExtractionException(
                  "The job details did not become ready in time.");
    }


    bool ImportJobAlreadyExists(string url)
    {
        return JobExists?.Invoke(url) == true;
    }

    /// <summary>
    /// Imports all URLs collected by Find Jobs, one job at a time.
    /// Each job still uses the existing Jobright extractor and ImportJob path.
    /// </summary>
    public async Task AutoImportFoundJobsAsync() {
        if (_importing ||
            _foundJobUrls.Count == 0 ||
            _view?.CoreWebView2 is null ||
            _extractor is null ||
            ImportJob is null) {
            return;
        }

        var urls = _foundJobUrls.ToList();
        var returnUrl = _view.Source?.ToString();

        var imported = 0;
        var existing = 0;
        var failed = 0;

        _importing = true;
        UpdateImportButton();

        ResultBanner.Visibility = Visibility.Collapsed;

        try {
            for (var i = 0; i < urls.Count; i++) {
                var url = urls[i];

                if (ImportJobAlreadyExists(url)) {
                    existing++;

                    PerfLog.Line(
                        "JOBBROWSER auto skip existing " +
                        JobBrowser.SafeForLog(url));

                    continue;
                }

                FoundJobsText.Text = $"{i + 1}/{urls.Count}";
                StatusText.Text =
                    $"Opening job {i + 1} of {urls.Count}…";

                PerfLog.Line(
                    $"JOBBROWSER auto job {i + 1}/{urls.Count} " +
                    JobBrowser.SafeForLog(url));

                var navigated = await NavigateAndWaitAsync(
                    url,
                    TimeSpan.FromSeconds(15));

                if (!navigated) {
                    failed++;

                    PerfLog.Line(
                        $"JOBBROWSER auto failed {i + 1}/{urls.Count} navigation");

                    continue;
                }

                try {
                    StatusText.Text =
                        $"Reading job {i + 1} of {urls.Count}…";

                    var data = await ExtractWithRetryAsync();

                    var outcome = ImportJob(data);

                    switch (outcome.Kind) {
                        case JobImportKind.Imported:
                            imported++;

                            PerfLog.Line(
                                "JOBBROWSER auto imported " +
                                outcome.JobId);
                            break;


                        case JobImportKind.Duplicate:
                            existing++;

                            PerfLog.Line(
                                "JOBBROWSER auto existing " +
                                outcome.JobId);
                            break;


                        default:
                            failed++;

                            PerfLog.Line(
                                "JOBBROWSER auto failed " +
                                outcome.Reason);
                            break;
                    }
                } catch (JobExtractionException ex) {
                    failed++;

                    PerfLog.Line(
                        "JOBBROWSER auto extract failed " +
                        ex.Message);
                } catch (Exception ex) {
                    failed++;

                    PerfLog.Line(
                        "JOBBROWSER auto failed " +
                        ex.GetType().Name);
                }
            }


            // Return to the Jobright results page where Find Jobs started.
            if (!string.IsNullOrWhiteSpace(returnUrl) &&
                _view?.CoreWebView2 is not null) {

                StatusText.Text =
                    "Returning to job results…";

                await NavigateAndWaitAsync(
                    returnUrl,
                    TimeSpan.FromSeconds(15));
            }


            ShowResult(
                "Import finished",
                $"Found: {urls.Count}\n" +
                $"Imported: {imported}\n" +
                $"Existing: {existing}\n" +
                $"Failed: {failed}",
                failed == 0 ? Success : Neutral);


            StatusText.Text =
                $"Finished — imported {imported}, " +
                $"existing {existing}, failed {failed}.";
        } finally {
            _importing = false;

            FoundJobsText.Text =
                $"Found: {_foundJobUrls.Count}";

            UpdateImportButton();
        }
    }

    // ---------- result banner ----------
    static readonly (string Back, string Border, string Ink) Success = ("#E4F4E8", "#BFE3C8", "#1E7E34");
    static readonly (string Back, string Border, string Ink) Neutral = ("#E1EEFA", "#BCD6EF", "#1D6FB8");
    static readonly (string Back, string Border, string Ink) Problem = ("#FDECEC", "#F3C4C4", "#B42318");

    void ShowFailure(string reason) {
        ShowResult("Could not import this page.", "Reason: " + reason, Problem);
        StatusText.Text = "Nothing was added to the queue.";
    }

    void ShowResult(string headline, string detail, (string Back, string Border, string Ink) colours) {
        var converter = new BrushConverter();
        ResultBanner.Background = (Brush)converter.ConvertFromString(colours.Back)!;
        ResultBanner.BorderBrush = (Brush)converter.ConvertFromString(colours.Border)!;
        ResultHeadline.Foreground = (Brush)converter.ConvertFromString(colours.Ink)!;
        ResultHeadline.Text = headline;
        ResultDetail.Text = detail;
        ResultBanner.Visibility = Visibility.Visible;
    }

    void DismissResult_Click(object sender, RoutedEventArgs e) => ResultBanner.Visibility = Visibility.Collapsed;

    // ---------- navigation ----------

    void Back_Click(object sender, RoutedEventArgs e) {
        if (_view?.CoreWebView2?.CanGoBack == true) _view.CoreWebView2.GoBack();
    }

    void Forward_Click(object sender, RoutedEventArgs e) {
        if (_view?.CoreWebView2?.CanGoForward == true) _view.CoreWebView2.GoForward();
    }

    void Refresh_Click(object sender, RoutedEventArgs e) => _view?.CoreWebView2?.Reload();

    void Home_Click(object sender, RoutedEventArgs e) {
        if (_view?.CoreWebView2 is null) return;
        _view.CoreWebView2.Navigate(JobBrowser.HomeUrl);
    }

    /// <summary>
    /// Tears down this browser only. The cached environment is kept so the next open reuses the same
    /// signed-in profile, which is how a normal browser behaves between sessions.
    /// </summary>
    void DisposeBrowser() {
        var view = _view;
        _view = null;
        _extractor = null;
        if (view is null) return;

        BrowserHost.Child = null;
        try { view.Dispose(); } catch { /* teardown must never throw out of a window close */ }
        PerfLog.Line("JOBBROWSER dispose");
    }
}
