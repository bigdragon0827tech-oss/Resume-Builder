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
    bool _autoImportRunning;
    bool _stopAutoImport;
    int _sessionFound;
    int _sessionGoal;
    bool _parked;

    /// <summary>True while Auto Import is processing jobs. The shell uses this to keep the browser alive.</summary>
    public bool IsAutoImportRunning => _autoImportRunning;

    /// <summary>Raised after an Auto Import session ends, including a goal stop or a user stop.</summary>
    public event Action? AutoImportEnded;

    /// <summary>True for the whole of one Find Jobs click, including the awaits before import is set.</summary>
    bool _findJobsRunning;
    bool _blockFindJobsClick;

    /// <summary>Jobs found by the current scan that are NOT already in Resume Builder.</summary>
    readonly List<string> _foundJobUrls = new();

    /// <summary>Normalized keys of every link the current scan has seen, new or already imported.</summary>
    readonly HashSet<string> _seenJobKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Jobright ids that already had one Remove From List call in this Find Jobs run.</summary>
    readonly HashSet<string> _removalAttempted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The first removal of a Find Jobs run logs which job links are actually on the page.</summary>
    bool _logRemovalLinkDebug;

    /// <summary>How many links the current scan passed over because they are already in Resume Builder.</summary>
    int _skippedExisting;

    /// <summary>
    /// Normalized keys this Auto Import session has already attempted — imported, duplicate, failed
    /// or refused by a filter. A top-up round may surface the same card again; it is not reopened.
    /// The set lives for one session only: it is cleared when a new scan starts, so a job refused
    /// today can be imported tomorrow once the user changes a filter. Nothing is blacklisted on disk.
    /// </summary>
    readonly HashSet<string> _processedJobKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many jobs each filter refused during the current Auto Import session.</summary>
    readonly Dictionary<JobFilterReason, int> _filteredThisSession = new();

    /// <summary>One key per job link, the same form duplicate detection uses.</summary>
    static string JobKey(string url) {
        var id = JobrightPageExtractor.JobIdFromUrl(url);
        return id is not null ? "jobright:" + id : JobUrls.Normalize(url) ?? url.Trim();
    }

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

    /// <summary>A loaded page whose address did not name an ATS. Does not change the ApplyUrl.</summary>
    public Action<string, PlatformDetectionResult>? NotePlatform { get; set; }
    public JobBrowserWindow() : this(null, null) { }

    /// <summary>
    /// The tests open about:blank on a throwaway profile, so no third-party request is made and the
    /// real signed-in profile is never touched while the plumbing is verified.
    /// </summary>
    public JobBrowserWindow(string? startUrl, string? userDataFolder) {
        InitializeComponent();
        _startUrl = string.IsNullOrWhiteSpace(startUrl) ? JobBrowser.HomeUrl : startUrl;
        _profileFolder = string.IsNullOrWhiteSpace(userDataFolder) ? JobBrowser.UserDataFolder : userDataFolder;

        BuildImportFilters();

        IsVisibleChanged += (_, _) => SyncAutoImportButtons(log: false);
        Unloaded += (_, _) => { /* keep browser alive across shell navigation; MainWindow.Shutdown disposes */ };
    }

    // ---------- import filters ----------
    //
    // The toolbar edits the SAME five AppSettings values the Settings window does. There is no
    // browser-only copy: every toggle is a read-modify-write of the one settings.json, so an
    // unrelated setting can never be lost and the two screens can never disagree.

    /// <summary>The filter checkboxes, in JobImportFilter.Switches order, built once.</summary>
    readonly List<System.Windows.Controls.CheckBox> _filterBoxes = new();

    /// <summary>Set while the boxes are being loaded from settings, so binding is not read as a click.</summary>
    bool _loadingFilters;

    /// <summary>
    /// Creates one checkbox per shared switch descriptor and loads the persisted values at once.
    /// Building them here — not on first popup open — is what keeps "Filters (N)" correct from the
    /// moment the control exists, instead of showing the defaults until the popup is first opened.
    /// </summary>
    void BuildImportFilters() {
        foreach (var descriptor in JobImportFilter.Switches) {
            var box = new System.Windows.Controls.CheckBox {
                Content = descriptor.Label,
                Margin = new Thickness(0, 4, 0, 4),
                Tag = descriptor.Reason
            };
            box.Checked += FilterSwitch_Changed;
            box.Unchecked += FilterSwitch_Changed;

            _filterBoxes.Add(box);
            FilterSwitchPanel.Children.Insert(FilterSwitchPanel.Children.Count - 1, box);
        }

        FilterFooterText.Text = JobImportFilter.AppliesToFutureImports;
        RefreshImportFilters();
    }

    /// <summary>
    /// Re-reads the five switches from settings.json into the boxes and the button caption. Called
    /// at construction, whenever the popup is opened, and by MainWindow after Settings saves — so
    /// a value changed on the other screen shows here without reopening the browser.
    /// </summary>
    public void RefreshImportFilters() {
        var settings = Storage.LoadSettings();

        _loadingFilters = true;
        try {
            for (var i = 0; i < _filterBoxes.Count && i < JobImportFilter.Switches.Count; i++)
                _filterBoxes[i].IsChecked = JobImportFilter.Switches[i].Get(settings);
        } finally {
            _loadingFilters = false;
        }

        FiltersButton.Content = JobImportFilter.ButtonText(settings);
    }

    void Filters_Click(object sender, RoutedEventArgs e) {
        // Opening always shows what is actually saved, even if Settings changed it meanwhile.
        RefreshImportFilters();
        FiltersPopup.IsOpen = !FiltersPopup.IsOpen;
    }

    /// <summary>
    /// One switch changed: load the current settings, set that single field, save. Everything else
    /// in settings.json comes straight back from disk, so nothing unrelated is rewritten.
    /// </summary>
    void FilterSwitch_Changed(object sender, RoutedEventArgs e) {
        if (_loadingFilters) return;
        if (sender is not System.Windows.Controls.CheckBox { Tag: JobFilterReason reason } box) return;

        var descriptor = JobImportFilter.Switches.FirstOrDefault(s => s.Reason == reason);
        if (descriptor is null) return;

        var settings = Storage.LoadSettings();
        descriptor.Set(settings, box.IsChecked == true);
        Storage.SaveSettings(settings);

        FiltersButton.Content = JobImportFilter.ButtonText(settings);
        StatusText.Text = $"Import filters: {JobImportFilter.EnabledCount(settings)} on. " +
                          JobImportFilter.AppliesToFutureImports;

        PerfLog.Line($"JOBBROWSER filter {reason}={(box.IsChecked == true ? "on" : "off")}");
    }

    /// <summary>The filter switches as they are saved right now — the tests read this.</summary>
    public AppSettings CurrentImportFilters() => Storage.LoadSettings();

    /// <summary>The checkbox captions currently on show, for the UI harness.</summary>
    public IReadOnlyList<string> ImportFilterLabels() =>
        _filterBoxes.Select(b => b.Content as string ?? "").ToList();

    /// <summary>Whether each switch is ticked, in JobImportFilter.Switches order.</summary>
    public IReadOnlyList<bool> ImportFilterStates() =>
        _filterBoxes.Select(b => b.IsChecked == true).ToList();

    /// <summary>The toolbar caption, so a test can assert "Filters (3)" without reading XAML.</summary>
    public string ImportFilterButtonText() => FiltersButton.Content as string ?? "";

    /// <summary>Ticks or clears one switch exactly as a click would, persisting it. For the harness.</summary>
    public void SetImportFilter(JobFilterReason reason, bool enabled) {
        var box = _filterBoxes.FirstOrDefault(b => b.Tag is JobFilterReason r && r == reason);
        if (box is not null) box.IsChecked = enabled;
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
                _ = NotePagePlatformAsync(view);
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
            StatusText.Text = "Unable to load browser.";
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
    /// Updates which job-browser actions are currently available.
    /// Import First Job reads the first card on the recommend list.
    /// </summary>
    void UpdateImportButton() {
        SyncAutoImportButtons(log: false);
        if (ImportBusyBar is not null)
            ImportBusyBar.Visibility = _importing || _navigating ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Shows the running Auto Import on the toolbar. Returning to Job Browser calls this;
    /// it does not start, stop, or recreate the browser.
    /// </summary>
    public void SyncDisplayedAutomation() => SyncAutoImportButtons(log: true);

    void SyncAutoImportButtons(bool log)
    {
        var goal = _sessionGoal > 0 ? _sessionGoal : GetJobTarget();
        if (log)
            PerfLog.Line(
                $"JOBBROWSER auto-ui-sync running={(_autoImportRunning ? "true" : "false")} found={_sessionFound} goal={goal}");

        AutoImportFirstButton.Content = _autoImportRunning ? "Stop Auto Import" : "Auto Import";
        AutoImportFirstButton.IsEnabled = true;
        FoundJobsText.Text = $"Found: {_sessionFound}";
    }

    /// <summary>
    /// Keeps this control visible at a real size, off the shell window, so the Jobright browser
    /// is not suspended when another panel is showing.
    /// </summary>
    public void ParkForBackground()
    {
        if (_parked)
            return;
        _parked = true;
        HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        Width = 1280;
        Height = 900;
        Margin = new Thickness(-20000, 0, 0, 0);
        IsHitTestVisible = false;
    }

    /// <summary>Puts the Job Browser back in the shell page after background Auto Import.</summary>
    public void RestoreFromBackground()
    {
        if (!_parked)
            return;
        _parked = false;
        ClearValue(HorizontalAlignmentProperty);
        ClearValue(VerticalAlignmentProperty);
        ClearValue(WidthProperty);
        ClearValue(HeightProperty);
        ClearValue(MarginProperty);
        IsHitTestVisible = true;
    }

    /// <summary>
    /// Called when the page starts leaving (same page) or asks for a new window. Only a single Jobright
    /// job page heading OFF jobright.ai is considered; jobright-to-jobright navigation (browsing, Auto
    /// Import) returns at once. It reads nothing from the page and never blocks the navigation.
    /// </summary>
    /// <summary>
    /// After a page has already loaded, look at a few resource addresses when the URL itself
    /// does not name an ATS. Navigation is never delayed or cancelled.
    /// </summary>
    async Task NotePagePlatformAsync(Microsoft.Web.WebView2.Wpf.WebView2 view) {
        try {
            var url = view.Source?.ToString();
            if (ApplicationPlatformDetector.Inspect(url).Confidence != PlatformConfidence.Unknown) return;
            if (!ApplyCapture.IsApplicationUrl(url)) return;
            var core = view.CoreWebView2;
            if (core is null) return;
            var raw = await core.ExecuteScriptAsync(ApplicationPlatformDetector.PageFingerprintScript);
            var result = ApplicationPlatformDetector.InspectPage(url, ApplicationPlatformDetector.ParsePageSignals(raw));
            if (result.Confidence == PlatformConfidence.Unknown || NotePlatform is null) return;
            await Dispatcher.InvokeAsync(() => NotePlatform(url!, result));
        } catch (Exception ex) {
            PerfLog.Line("PLATFORM page fingerprint skipped " + ex.GetType().Name);
        }
    }

    ApplyCaptureResult ReportApplyDestination(string? jobPageUrl, string? destination, string via) {
        try {
            if (!JobrightPageExtractor.IsJobPage(jobPageUrl)) return ApplyCaptureResult.NotJobPage;
            if (!Uri.TryCreate(destination, UriKind.Absolute, out var dest) ||
                dest.Host.Equals("jobright.ai", StringComparison.OrdinalIgnoreCase) ||
                dest.Host.EndsWith(".jobright.ai", StringComparison.OrdinalIgnoreCase)) return ApplyCaptureResult.NotApplicationUrl;

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
            return result;
        } catch (Exception ex) {
            // Recording is a convenience: a failure here must never disturb browsing.
            PerfLog.Line("JOBBROWSER apply capture failed " + ex.GetType().Name);
            return ApplyCaptureResult.NotApplicationUrl;
        }
    }

    /// <summary>
    /// Applications asked for an Apply URL this job does not have yet. Opens the Jobright posting
    /// in this profile's browser and records the external address the page's Apply button uses.
    /// A click on that button is still observed by the navigation and new-window handlers.
    /// </summary>
    public async Task CaptureExternalApplyAsync(string jobUrl) {
        if (!JobrightPageExtractor.IsJobPage(jobUrl)) return;
        await EnsureReadyAsync();
        if (_view?.CoreWebView2 is null || _extractor is null) {
            StatusText.Text = "Unable to load browser.";
            return;
        }

        StatusText.Text = "Opening the job to capture its application link…";
        SetBrowserBusy(true, "Opening the job…");
        try {
            if (!await NavigateAndWaitAsync(jobUrl, TimeSpan.FromSeconds(20))) {
                StatusText.Text = "Unable to open application page.";
                return;
            }

            JobImportData? data;
            try {
                data = await _extractor.ExtractCurrentJobAsync();
            } catch (JobExtractionException ex) {
                PerfLog.Line("JOBBROWSER apply read failed " + ex.GetType().Name);
                StatusText.Text = "Click Apply on this Jobright page. The application link will be recorded.";
                return;
            }

            var page = JobrightPageExtractor.IsJobPage(_view.Source?.ToString()) ? _view.Source!.ToString() : jobUrl;
            if (!ApplyCapture.IsApplicationUrl(data?.ApplyUrl)) {
                StatusText.Text = "Click Apply on this Jobright page. The application link will be recorded.";
                return;
            }

            var result = ReportApplyDestination(page, data!.ApplyUrl, "new-window");
            if (result is ApplyCaptureResult.Recorded or ApplyCaptureResult.Unchanged)
                JobTracker.OpenJobUrl(data.ApplyUrl);
        } finally {
            SetBrowserBusy(false);
        }
    }

    async void Import_Click(object sender, RoutedEventArgs e) => await ImportCurrentJobAsync();

    async void AutoImport_Click(object sender, RoutedEventArgs e) =>
        await AutoImportFoundJobsAsync();

    async void ImportFirstJob_Click(object sender, RoutedEventArgs e)
    {
        if (_autoImportRunning ||
            _importing ||
            _navigating ||
            _view?.CoreWebView2 is null ||
            _extractor is null ||
            ImportJob is null)
            return;

        _importing = true;
        _stopFinding = false;
        PerfLog.Line("JOBBROWSER import-first start");
        StatusText.Text = "Reading the first job…";
        try
        {
            await ImportFirstJobAsync();
        }
        catch (Exception ex)
        {
            PerfLog.Line("JOBBROWSER import-first failed " + ex.GetType().Name);
            StatusText.Text = "Could not finish this job. It was left on Jobright.";
        }
        finally
        {
            _importing = false;
            UpdateImportButton();
            PerfLog.Line("JOBBROWSER import-first finished");
        }
    }

    async void AutoImportFirst_Click(object sender, RoutedEventArgs e)
    {
        if (_autoImportRunning)
        {
            if (_stopAutoImport)
                return;
            _stopAutoImport = true;
            StatusText.Text = "Stopping after the current job…";
            PerfLog.Line("JOBBROWSER auto-import-user-stop");
            return;
        }

        if (_importing ||
            _navigating ||
            _view?.CoreWebView2 is null ||
            _extractor is null ||
            ImportJob is null)
            return;

        _autoImportRunning = true;
        _stopAutoImport = false;
        _importing = true;
        _sessionFound = 0;
        _sessionGoal = GetJobTarget();
        UpdateImportButton();
        PerfLog.Line($"JOBBROWSER auto-import-start goal={_sessionGoal}");
        try
        {
            await RunAutoImportAsync();
        }
        catch (Exception ex)
        {
            PerfLog.Line("JOBBROWSER import-first failed " + ex.GetType().Name);
            PerfLog.Line("JOBBROWSER auto-import-stop reason=exception");
            StatusText.Text = "Auto import stopped. The job was left on Jobright.";
        }
        finally
        {
            _importing = false;
            _autoImportRunning = false;
            _stopAutoImport = false;
            UpdateImportButton();
            AutoImportEnded?.Invoke();
        }
    }

    async Task RunAutoImportAsync()
    {
        var number = 0;
        while (!_stopAutoImport)
        {
            number++;
            PerfLog.Line($"JOBBROWSER auto-import-next {number}");
            StatusText.Text = $"Auto import {number}…";
            string? stopReason;
            try
            {
                stopReason = await ImportFirstJobAsync();
            }
            catch (Exception ex)
            {
                PerfLog.Line("JOBBROWSER import-first failed " + ex.GetType().Name);
                stopReason = "exception";
            }

            if (stopReason is not null)
            {
                PerfLog.Line($"JOBBROWSER auto-import-stop reason={stopReason}");
                if (stopReason == "exception")
                    StatusText.Text = "Auto import stopped. The job was left on Jobright.";
                return;
            }
            if (_sessionFound >= _sessionGoal)
            {
                PerfLog.Line($"JOBBROWSER auto-import-stop reason=goal found={_sessionFound}");
                StatusText.Text = $"Found {_sessionFound} of {_sessionGoal}.";
                return;
            }
            if (_stopAutoImport)
                break;

            await Task.Delay(400);
        }

        StatusText.Text = "Auto import stopped.";
    }

    /// <summary>
    /// One first card. Already-imported cards are marked Already Applied in place.
    /// A new card goes through the existing importer, then is marked after the return to the list.
    /// A null return means this card is finished and another first card may be processed.
    /// </summary>
    async Task<string?> ImportFirstJobAsync()
    {
        var recommendUrl = RecommendListUrl(_view?.Source?.ToString());
        if (!await EnsureRecommendAsync(recommendUrl))
        {
            PerfLog.Line("JOBBROWSER processing-failed none reason=navigation");
            StatusText.Text = "Could not open the recommend list.";
            return "navigation";
        }
        await WaitForJobListReadyAsync();

        var current = await ReadFirstJobAsync();
        if (current is null)
        {
            PerfLog.Line("JOBBROWSER first-job none");
            StatusText.Text = "No job card on this list.";
            return "no-job";
        }

        var id = current.Value.Id;
        var url = current.Value.Url;
        PerfLog.Line($"JOBBROWSER first-job id={id} url={JobBrowser.SafeForLog(url)}");
        if (JobExists?.Invoke(url) == true)
        {
            PerfLog.Line($"JOBBROWSER existing {id} true");
            StatusText.Text = "Already imported. Marking Already Applied…";
            return await MarkAlreadyAppliedAsync(id, recommendUrl);
        }

        PerfLog.Line($"JOBBROWSER existing {id} false");
        StatusText.Text = "Importing the first job…";
        var result = await ImportListedJobAsync(url, id);
        if (result == ListedImport.Imported && _autoImportRunning)
        {
            _sessionFound++;
            FoundJobsText.Text = $"Found: {_sessionFound}";
            PerfLog.Line($"JOBBROWSER auto-import-found {_sessionFound}/{_sessionGoal}");
        }
        if (result == ListedImport.Technical)
        {
            PerfLog.Line($"JOBBROWSER leaving-job-on-jobright {id}");
            StatusText.Text = "This job was left on Jobright.";
            return "technical";
        }

        var listReady = await EnsureRecommendAsync(recommendUrl);
        if (!listReady &&
            result is ListedImport.Imported or ListedImport.Filtered or ListedImport.AlreadyThere)
            listReady = await PageIsReadyAsync(recommendUrl);
        if (!listReady)
        {
            PerfLog.Line($"JOBBROWSER processing-failed {id} reason=navigation");
            PerfLog.Line($"JOBBROWSER leaving-job-on-jobright {id}");
            StatusText.Text = "Could not return to the recommend list. The job was left on Jobright.";
            return "navigation";
        }
        await WaitForJobListReadyAsync();
        return await MarkAlreadyAppliedAsync(id, recommendUrl);
    }

    /// <summary>
    /// Marks the job Already Applied through Jobright's own signed-in page.
    /// Success is only the confirmed API result. The card is then removed on the page.
    /// </summary>
    async Task<string?> MarkAlreadyAppliedAsync(string id, string recommendUrl)
    {
        if (!IsJobrightJobId(id) || _view?.CoreWebView2 is null)
        {
            PerfLog.Line($"JOBBROWSER already-applied-api-failed {id} reason=unavailable");
            StatusText.Text = "Could not mark Already Applied. The job was left on Jobright.";
            return "already-applied";
        }

        PerfLog.Line($"JOBBROWSER already-applied-api-attempt {id}");
        var started = await PageStatusAsync(AlreadyAppliedApiStartScript(id));
        if (started != "started")
        {
            PerfLog.Line($"JOBBROWSER already-applied-api-raw {id} {started}");
            PerfLog.Line($"JOBBROWSER already-applied-api-failed {id} reason=start");
            StatusText.Text = "Could not mark Already Applied. The job was left on Jobright.";
            return "already-applied";
        }

        var payload = "";
        for (var i = 0; i < 50; i++)
        {
            payload = await PageStatusAsync(AlreadyAppliedApiPollScript(id));
            if (payload.StartsWith('{'))
                break;
            await Task.Delay(100);
        }
        if (!payload.StartsWith('{'))
        {
            PerfLog.Line($"JOBBROWSER already-applied-api-raw {id} timeout");
            PerfLog.Line($"JOBBROWSER already-applied-api-failed {id} reason=timeout");
            StatusText.Text = "Could not mark Already Applied. The job was left on Jobright.";
            return "already-applied";
        }

        PerfLog.Line($"JOBBROWSER already-applied-api-raw {id} {payload}");
        if (!TryReadApplyApi(payload, out var http, out var responseOk, out var success, out var result, out var failure))
        {
            PerfLog.Line($"JOBBROWSER already-applied-api-failed {id} reason=unparsed");
            StatusText.Text = "Could not mark Already Applied. The job was left on Jobright.";
            return "already-applied";
        }

        PerfLog.Line($"JOBBROWSER already-applied-api-status {id} http={http}");
        if (http is >= 200 and <= 299 && responseOk && success && result)
        {
            PerfLog.Line($"JOBBROWSER already-applied-api-success {id}");
            StatusText.Text = "Marked Already Applied.";
            if (!await RemoveLocalJobCardAsync(id))
            {
                PerfLog.Line($"JOBBROWSER local-card-remove-fallback-refresh {id}");
                if (!await RefreshRecommendAsync(recommendUrl))
                {
                    StatusText.Text = "Marked Already Applied. The list did not refresh.";
                    return "refresh";
                }
            }
            return null;
        }

        var reason = failure is "request" or "unparsed" ? failure
            : http is < 200 or > 299 || !responseOk ? "http"
            : !success ? "success"
            : !result ? "result"
            : "ambiguous";
        PerfLog.Line($"JOBBROWSER already-applied-api-failed {id} reason={reason}");
        StatusText.Text = "Could not mark Already Applied. The job was left on Jobright.";
        return "already-applied";
    }

    static bool TryReadApplyApi(
        string payload,
        out int http,
        out bool responseOk,
        out bool success,
        out bool result,
        out string failure)
    {
        http = 0;
        responseOk = false;
        success = false;
        result = false;
        failure = "";
        if (string.IsNullOrWhiteSpace(payload) || !payload.TrimStart().StartsWith('{'))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (!root.TryGetProperty("done", out var done) || done.ValueKind != JsonValueKind.True)
                return false;
            if (!root.TryGetProperty("httpStatus", out var status) || !status.TryGetInt32(out http))
                return false;
            responseOk = root.TryGetProperty("responseOk", out var ok) && ok.ValueKind == JsonValueKind.True;
            success = root.TryGetProperty("success", out var okFlag) && okFlag.ValueKind == JsonValueKind.True;
            result = root.TryGetProperty("result", out var resultFlag) && resultFlag.ValueKind == JsonValueKind.True;
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                failure = error.GetString() ?? "";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    async Task<bool> RemoveLocalJobCardAsync(string id)
    {
        PerfLog.Line($"JOBBROWSER local-card-remove-attempt {id}");
        if (await PageStatusAsync(RemoveLocalJobCardScript(id)) != "removed")
            return false;
        var current = await ReadFirstJobAsync();
        if (current is not null && current.Value.Id == id)
            return false;
        PerfLog.Line($"JOBBROWSER local-card-remove-success {id}");
        return true;
    }

    static string RemoveLocalJobCardScript(string id) => """
        (() => {
            const expected = "__JOB_ID__";
            const extractId = (href) => {
                const raw = String(href || "");
                try {
                    const match = new URL(raw, location.origin).pathname.toLowerCase().match(/\/jobs\/info\/([0-9a-f]{24})/);
                    if (match) return match[1];
                } catch (e) { }
                const fallback = raw.toLowerCase().match(/\/jobs\/info\/([0-9a-f]{24})/);
                return fallback ? fallback[1] : "";
            };
            let anchor = null;
            for (const link of document.querySelectorAll('a[href*="/jobs/info/"]')) {
                if (extractId(link.href || link.getAttribute("href")) !== expected) continue;
                anchor = link;
                break;
            }
            if (!anchor) return "missing";
            const row = anchor.closest(".job-card-flag-classname") || anchor.closest('[class*="job-card-flag"]');
            if (!row) return "no-row";
            row.remove();
            return "removed";
        })()
        """.Replace("__JOB_ID__", id, StringComparison.Ordinal);

    async Task<bool> RefreshRecommendAsync(string recommendUrl)
    {
        var view = _view;
        if (view?.CoreWebView2 is null)
            return false;
        var current = view.Source?.ToString() ?? "";
        if (!current.Contains("/jobs/recommend", StringComparison.OrdinalIgnoreCase))
            return await NavigateAndWaitAsync(recommendUrl, TimeSpan.FromSeconds(15));

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            view.NavigationCompleted -= Completed;
            tcs.TrySetResult(e.IsSuccess);
        }
        view.NavigationCompleted += Completed;
        try
        {
            view.CoreWebView2.Reload();
            var finished = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            if (finished != tcs.Task)
            {
                view.NavigationCompleted -= Completed;
                return false;
            }
            return await tcs.Task;
        }
        catch
        {
            view.NavigationCompleted -= Completed;
            return false;
        }
    }

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

                case JobImportKind.Skipped:
                    // A filter refusal is never silent, and never shows a pattern, the description
                    // or the application address — only which rule refused it.
                    PerfLog.Line("JOBBROWSER import skipped " +
                                 JobBrowser.SafeForLog(_view?.Source?.ToString()) +
                                 " reason=" + JobImportFilter.LogReason(outcome.FilterReason));
                    ShowResult(JobImportFilter.Describe(outcome.FilterReason),
                               $"{outcome.Title}\n{outcome.Company}\n\nChange this under Filters, then import again.",
                               Neutral);
                    StatusText.Text = JobImportFilter.Describe(outcome.FilterReason);
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
    /// When <paramref name="removeImported"/> is set, each of those cards is asked to leave Jobright's list.
    /// Returns how many new jobs this pass added.
    /// </summary>
    async Task<int> CollectJobsFromCurrentPageAsync(int target, bool removeImported = false)
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
        var alreadyImported = new List<string>();

        foreach (var url in urls)
        {
            if (string.IsNullOrWhiteSpace(url))
                continue;

            if (!removeImported && _foundJobUrls.Count >= target)
                break;

            // One key per job, the same form import duplicate detection uses.
            var key = JobKey(url);
            if (_seenJobKeys.Contains(key))
                continue;

            if (JobExists?.Invoke(url) == true)
            {
                // Seen for this scan even if Jobright's own removal fails, so the card is
                // not collected and is not submitted to Remove From List again. The first
                // attempt still runs below, before the scan scrolls.
                _seenJobKeys.Add(key);
                _skippedExisting++;
                alreadyImported.Add(url);
                var id = JobrightPageExtractor.JobIdFromUrl(url);
                if (id is not null) {
                    PerfLog.Line("IDENTITY lookup source=jobright id=" + id);
                    PerfLog.Line($"JOBBROWSER duplicate {id}");
                }
                continue;
            }

            if (_foundJobUrls.Count >= target)
                continue;

            _seenJobKeys.Add(key);
            _foundJobUrls.Add(url);
            added++;
        }

        FoundJobsText.Text = $"Found: {_foundJobUrls.Count}";

        if (removeImported)
        {
            foreach (var url in alreadyImported)
            {
                if (_stopFinding)
                    break;
                var attemptId = JobrightPageExtractor.JobIdFromUrl(url);
                if (attemptId is not null && _removalAttempted.Contains(attemptId))
                    continue;
                PerfLog.Line($"JOBBROWSER removal-attempt {attemptId ?? "?"}");
                await RemoveImportedFromJobrightAsync(url);
            }
        }

        PerfLog.Line(
            $"JOBBROWSER collected new={_foundJobUrls.Count} skipped existing={_skippedExisting}");

        return added;
    }

    /// <summary>
    /// Asks Jobright to remove one already-imported card via that card's own menu.
    /// A miss, or a menu that is not clearly this job's, leaves the card on Jobright.
    /// The job is never deleted from Resume Builder.
    /// </summary>
    async Task<bool> RemoveImportedFromJobrightAsync(string url, bool postImport = false)
    {
        var id = JobrightPageExtractor.JobIdFromUrl(url);
        if (_view?.CoreWebView2 is null || id is null || !IsJobrightJobId(id))
        {
            PerfLog.Line("JOBBROWSER remove-from-list skipped");
            return false;
        }

        try
        {
            _removalAttempted.Add(id);
            var opened = ReadOpen(await PageStatusAsync(OpenCardMenuScript(id)));
            if (_logRemovalLinkDebug)
            {
                _logRemovalLinkDebug = false;
                if (opened.Chain.Length > 0)
                    PerfLog.Line($"JOBBROWSER remove-link-debug {id} {opened.Chain[0]}");
                foreach (var href in opened.Buttons)
                    PerfLog.Line($"JOBBROWSER remove-link-match {id} href=\"{href}\"");
            }
            if (opened.Status == "target-missing")
            {
                PerfLog.Line($"JOBBROWSER target-link-not-in-dom {id}");
                return false;
            }
            if (opened.Index != 1)
            {
                if (postImport)
                    await LogPostImportLinksAsync(id);
                PerfLog.Line($"JOBBROWSER card-not-found {id}");
                return false;
            }
            PerfLog.Line($"JOBBROWSER exact-link-found {id}");
            if (opened.Status == "ambiguous-card")
            {
                PerfLog.Line($"JOBBROWSER ambiguous-card {id}");
                return false;
            }
            PerfLog.Line($"JOBBROWSER result-row-found {id}");
            PerfLog.Line($"JOBBROWSER row-id {id} actual={opened.Row}");
            PerfLog.Line("JOBBROWSER more-svg-source method=RemoveImportedFromJobrightAsync");
            PerfLog.Line("JOBBROWSER removal-path branch=post-import-id-lookup");
            PerfLog.Line($"JOBBROWSER more-svg-count {id} {opened.Count}");
            if (opened.Count == 0 && opened.Button.Length > 0)
                PerfLog.Line($"JOBBROWSER row-more-debug {id} {opened.Button}");
            if (opened.Status == "ambiguous-card-menu")
            {
                PerfLog.Line($"JOBBROWSER ambiguous-card-menu {id}");
                return false;
            }
            if (opened.Status != "opened")
            {
                PerfLog.Line($"JOBBROWSER menu-not-found {id}");
                return false;
            }

            PerfLog.Line($"JOBBROWSER card-menu-clicked {id}");
            return await AcceptRemoveMenuAsync(id);
        }
        catch (Exception ex)
        {
            PerfLog.Line("JOBBROWSER remove-from-list failed " + ex.GetType().Name);
            return false;
        }
    }

    /// <summary>
    /// The more-options control was already clicked. Wait for Jobright's menu and choose
    /// Remove From List. Closing a Turbo modal is not success.
    /// </summary>
    async Task<bool> AcceptRemoveMenuAsync(string id)
    {
        PerfLog.Line($"JOBBROWSER popup-labels {id} [{await PopupItemsTextAsync()}]");
        if (await FailIfTurboAsync(id))
            return false;

        if (!await WaitForPageAsync(RemoveItemVisibleScript, "ready"))
        {
            if (await FailIfTurboAsync(id))
                return false;
            await PageStatusAsync(DismissMenuScript);
            PerfLog.Line($"JOBBROWSER remove-from-list-not-found {id}");
            return false;
        }
        PerfLog.Line($"JOBBROWSER remove-from-list-found {id}");

        var clicked = await PageStatusAsync(ClickRemoveItemScript);
        if (clicked != "clicked")
        {
            if (await FailIfTurboAsync(id))
                return false;
            await PageStatusAsync(DismissMenuScript);
            PerfLog.Line($"JOBBROWSER remove-from-list-not-found {id}");
            return false;
        }
        PerfLog.Line($"JOBBROWSER removal-clicked {id}");
        if (await FailIfTurboAsync(id))
            return false;

        var outcome = await WaitForRemovalAsync(id);
        if (outcome == "timeout" && await FailIfTurboAsync(id))
            return false;
        if (outcome == "timeout" && await WaitForPageAsync(RemoveItemVisibleScript, "ready", tries: 4))
        {
            if (await PageStatusAsync(ClickRemoveItemScript) == "clicked")
                outcome = await WaitForRemovalAsync(id);
        }

        if (outcome == "timeout")
        {
            await PageStatusAsync(DismissMenuScript);
            PerfLog.Line($"JOBBROWSER timeout {id}");
            return false;
        }

        PerfLog.Line($"JOBBROWSER {outcome} {id}");
        return true;
    }

    static bool IsJobrightJobId(string id) =>
        id.Length == 24 && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    readonly record struct OpenReport(
        string Status, string Row, string Card, int Count, string Button, int Index, string[] Chain, string[] Buttons);

    static OpenReport ReadOpen(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload) || !payload.TrimStart().StartsWith('{'))
            return new OpenReport(payload ?? "", "", "", 0, "", -1, Array.Empty<string>(), Array.Empty<string>());
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
            var row = root.TryGetProperty("row", out var r) ? r.GetString() ?? "" : "";
            var card = root.TryGetProperty("card", out var c) ? c.GetString() ?? "" : "";
            var count = root.TryGetProperty("count", out var n) && n.TryGetInt32(out var value) ? value : 0;
            var button = root.TryGetProperty("button", out var b) ? b.GetString() ?? "" : "";
            var index = root.TryGetProperty("index", out var i) && i.TryGetInt32(out var at) ? at : -1;
            var chain = ReadStrings(root, "chain");
            var buttons = ReadStrings(root, "buttons");
            return new OpenReport(status, row, card, count, button, index, chain, buttons);
        }
        catch (JsonException)
        {
            return new OpenReport(payload, "", "", 0, "", -1, Array.Empty<string>(), Array.Empty<string>());
        }
    }

    static string[] ReadStrings(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var items) || items.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        return items.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? "")
            .Where(item => item.Length > 0)
            .ToArray();
    }

    async Task<string> PopupItemsTextAsync()
    {
        var raw = "";
        for (var i = 0; i < 4; i++)
        {
            raw = await PageStatusAsync(PopupItemsScript);
            if (raw.StartsWith('[') && raw != "[]")
                break;
            await Task.Delay(80);
        }
        if (!raw.StartsWith('['))
            return "(none)";
        try
        {
            var items = JsonSerializer.Deserialize<string[]>(raw) ?? Array.Empty<string>();
            return items.Length == 0 ? "(none)" : string.Join(" | ", items);
        }
        catch (JsonException)
        {
            return "(none)";
        }
    }

    /// <summary>
    /// The Turbo upgrade modal means the wrong control was clicked. Close it and fail this card.
    /// Closing it is not a successful removal, and the same click is not repeated.
    /// </summary>
    async Task<bool> FailIfTurboAsync(string id)
    {
        if (await PageStatusAsync(TurboModalScript) != "open")
            return false;
        PerfLog.Line($"JOBBROWSER unexpected-turbo-modal {id}");
        await PageStatusAsync(CloseTurboModalScript);
        await WaitForPageAsync(TurboModalScript, "closed", tries: 8);
        return true;
    }

    async Task<string> PageStatusAsync(string script)
    {
        var raw = await _view!.CoreWebView2.ExecuteScriptAsync(script);
        if (string.IsNullOrWhiteSpace(raw) || raw == "null")
            return "";
        try
        {
            return JsonSerializer.Deserialize<string>(raw) ?? "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    async Task<bool> WaitForPageAsync(string script, string expected, int tries = 8)
    {
        for (var i = 0; i < tries; i++)
        {
            if (await PageStatusAsync(script) == expected)
                return true;
            await Task.Delay(80);
        }
        return false;
    }

    async Task<string> WaitForRemovalAsync(string id)
    {
        var script = CardGoneScript(id);
        for (var i = 0; i < 8; i++)
        {
            var status = await PageStatusAsync(script);
            if (status is "gone" or "toast")
                return status == "gone" ? "success card-gone" : "success toast";
            await Task.Delay(80);
        }
        return "timeout";
    }

    static string AlreadyAppliedApiStartScript(string id) => """
        (() => {
            const jobId = "__JOB_ID__";
            const box = window.__rbApply = window.__rbApply || {};
            box[jobId] = { done: false };
            fetch("/swan/job/apply", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ jobId: jobId, source: 0 })
            }).then((response) => response.text().then((text) => {
                let body = null;
                let error = "";
                try {
                    body = JSON.parse(text);
                } catch (e) {
                    error = "unparsed";
                }
                if (!error && (!body || typeof body !== "object"))
                    error = "unparsed";
                const errorMsg = body && typeof body.errorMsg === "string" ? body.errorMsg.slice(0, 80) : null;
                box[jobId] = {
                    done: true,
                    httpStatus: response.status,
                    responseOk: response.ok === true,
                    success: !!(body && body.success === true),
                    result: !!(body && body.result === true),
                    errorCode: body && typeof body.errorCode === "number" ? body.errorCode : null,
                    errorMsg: errorMsg,
                    error: error
                };
            })).catch(() => {
                box[jobId] = {
                    done: true,
                    httpStatus: 0,
                    responseOk: false,
                    success: false,
                    result: false,
                    errorCode: null,
                    errorMsg: null,
                    error: "request"
                };
            });
            return "started";
        })()
        """.Replace("__JOB_ID__", id, StringComparison.Ordinal);

    static string AlreadyAppliedApiPollScript(string id) => """
        (() => {
            const jobId = "__JOB_ID__";
            const box = window.__rbApply;
            const slot = box && box[jobId];
            if (!slot || slot.done !== true) return "";
            delete box[jobId];
            return JSON.stringify(slot);
        })()
        """.Replace("__JOB_ID__", id, StringComparison.Ordinal);

    static string OpenFirstCardMenuScript(string id) => """
        (() => {
            const expected = "__JOB_ID__";
            const extractId = (href) => {
                const raw = String(href || "");
                try {
                    const match = new URL(raw, location.origin).pathname.toLowerCase().match(/\/jobs\/info\/([0-9a-f]{24})/);
                    if (match) return match[1];
                } catch (e) { }
                const fallback = raw.toLowerCase().match(/\/jobs\/info\/([0-9a-f]{24})/);
                return fallback ? fallback[1] : "";
            };
            const clip = (value) => String(value || "").replace(/\s+/g, " ").trim().slice(0, 240);
            const report = (status, actual, count, diag) => JSON.stringify(Object.assign({
                status,
                card: actual || "",
                count
            }, diag || {}));
            const diagFrom = (anchor, row) => {
                const ancestors = [];
                let parent = anchor ? anchor.parentElement : null;
                for (let depth = 1; parent && depth <= 6; depth++, parent = parent.parentElement) {
                    ancestors.push({
                        depth,
                        tag: parent.tagName || "",
                        id: parent.id || "",
                        className: clip(parent.getAttribute("class"))
                    });
                }
                const body = {
                    anchorHref: anchor ? (anchor.getAttribute("href") || anchor.href || "") : "",
                    anchorTag: anchor ? (anchor.tagName || "") : "",
                    anchorClass: anchor ? clip(anchor.getAttribute("class")) : "",
                    ancestors
                };
                if (!row) return body;
                const svgs = [...row.querySelectorAll("svg")];
                body.rowTag = row.tagName || "";
                body.rowId = row.id || "";
                body.rowClass = clip(row.getAttribute("class"));
                body.svgCount = svgs.length;
                body.ariaLabels = [...new Set(svgs.map(svg => svg.getAttribute("aria-label") || "").filter(Boolean))];
                body.useHrefs = [...new Set([...row.querySelectorAll("use")].map(use =>
                    use.getAttribute("href") || use.getAttribute("xlink:href") || "").filter(Boolean))];
                body.ariaCount = row.querySelectorAll('svg[aria-label="more-options"]').length;
                body.useCount = row.querySelectorAll('use[href*="#more"]').length;
                body.hasRow = true;
                return body;
            };
            let anchor = null;
            let job = "";
            for (const link of document.querySelectorAll('a[href*="/jobs/info/"]')) {
                const found = extractId(link.href || link.getAttribute("href"));
                if (!found) continue;
                anchor = link;
                job = found;
                break;
            }
            if (!anchor) return report("no-card", "", 0, null);
            if (job !== expected) return report("changed", job, 0, diagFrom(anchor, null));
            const row = anchor.closest(".job-card-flag-classname") || anchor.closest('[class*="job-card-flag"]');
            if (!row) return report("no-row", job, 0, diagFrom(anchor, null));
            let menus = [...row.querySelectorAll('svg[aria-label="more-options"]')];
            if (menus.length === 0) {
                const uses = [...row.querySelectorAll('use[href*="#more"]')];
                if (uses.length === 1) {
                    const svg = uses[0].closest("svg");
                    if (svg && row.contains(svg)) menus = [svg];
                }
            }
            if (menus.length !== 1) return report("ambiguous", job, menus.length, diagFrom(anchor, row));
            const svg = menus[0];
            const press = (type) => svg.dispatchEvent(new MouseEvent(type, { bubbles: true, cancelable: true, view: window }));
            press("mousedown");
            press("mouseup");
            svg.click();
            return report("opened", job, 1, diagFrom(anchor, row));
        })()
        """.Replace("__JOB_ID__", id, StringComparison.Ordinal);

    static string OpenCardMenuScript(string id) => """
        (() => {
            const id = "__JOB_ID__";
            const extractId = (href) => {
                const raw = String(href || "");
                try {
                    const match = new URL(raw, location.origin).pathname.toLowerCase().match(/\/jobs\/info\/([0-9a-f]{24})/);
                    if (match) return match[1];
                } catch (e) { }
                const fallback = raw.toLowerCase().match(/\/jobs\/info\/([0-9a-f]{24})/);
                return fallback ? fallback[1] : "";
            };
            const jobIdsIn = (node) => {
                const ids = new Set();
                for (const link of node.querySelectorAll('a[href*="/jobs/info/"]')) {
                    const foundId = extractId(link.href || link.getAttribute("href"));
                    if (foundId) ids.add(foundId);
                }
                return ids;
            };
            const report = (status, linkFound, rowId, count, debug, linkDebug, matches) => JSON.stringify({
                status,
                row: rowId || "",
                count,
                index: linkFound,
                button: debug || "",
                chain: linkDebug ? [linkDebug] : [],
                buttons: matches || []
            });
            const anchors = [...document.querySelectorAll('a[href*="/jobs/info/"]')];
            const parsed = anchors.map(anchor => ({
                anchor,
                href: anchor.getAttribute("href") || anchor.href || "",
                job: extractId(anchor.href || anchor.getAttribute("href"))
            }));
            const ids = [...new Set(parsed.map(item => item.job).filter(Boolean))];
            const matches = parsed.filter(item => item.job === id);
            const linkDebug = "links=" + anchors.length + " ids=[" + ids.join(",") + "] exactMatches=" + matches.length;
            const matchHrefs = matches.map(item => item.href);
            if (matches.length === 0) return report("target-missing", 0, "", 0, "", linkDebug, []);
            let row = null;
            for (const match of matches) {
                const anchor = match.anchor;
                const candidate = anchor.closest(".job-card-flag-classname") || anchor.closest('[class*="job-card-flag"]');
                if (!candidate || !candidate.contains(anchor)) continue;
                const rowIds = jobIdsIn(candidate);
                if (rowIds.size === 1 && rowIds.has(id)) { row = candidate; break; }
            }
            if (!row) return report("ambiguous-card", 1, "", 0, "", linkDebug, matchHrefs);
            const ariaSvgs = [...row.querySelectorAll('svg[aria-label="more-options"]')];
            const moreUses = [...row.querySelectorAll('use[href*="#more"]')];
            let menus = ariaSvgs;
            let debug = "";
            if (ariaSvgs.length === 0) {
                const labels = [...new Set([...row.querySelectorAll("svg")]
                    .map(svg => svg.getAttribute("aria-label") || "")
                    .filter(Boolean))].slice(0, 8);
                debug = [
                    "rowClass=" + String(row.getAttribute("class") || "").split(/\s+/).slice(0, 2).join(" ").slice(0, 80),
                    "rowId=" + (row.id || ""),
                    "svgCount=" + row.querySelectorAll("svg").length,
                    "ariaLabels=[" + labels.join(",") + "]",
                    "moreUseCount=" + moreUses.length
                ].join(" ");
                if (moreUses.length === 1) {
                    const svg = moreUses[0].closest("svg");
                    menus = svg && row.contains(svg) ? [svg] : [];
                }
            }
            if (menus.length !== 1)
                return report("ambiguous-card-menu", 1, row.id || "", ariaSvgs.length, debug, linkDebug, matchHrefs);
            const svg = menus[0];
            const press = (type) => svg.dispatchEvent(new MouseEvent(type, { bubbles: true, cancelable: true, view: window }));
            press("mousedown");
            press("mouseup");
            svg.click();
            return report("opened", 1, row.id || "", ariaSvgs.length, debug, linkDebug, matchHrefs);
        })()
        """.Replace("__JOB_ID__", id, StringComparison.Ordinal);

    const string PopupItemsScript = """
        (() => {
            const visible = (el) => {
                const box = el.getBoundingClientRect();
                if (box.width < 2 || box.height < 2) return false;
                const style = getComputedStyle(el);
                return style.display !== "none" && style.visibility !== "hidden" && Number(style.opacity) !== 0;
            };
            const inMenu = (el) => {
                const root = el.closest("[role='menu'], .ant-dropdown, .ant-dropdown-menu");
                return !!root && visible(root);
            };
            const items = [];
            const seen = new Set();
            for (const el of document.querySelectorAll("[role='menuitem'], .ant-dropdown-menu-item")) {
                const text = (el.innerText || "").replace(/\s+/g, " ").trim();
                if (!text || text.length > 40 || !visible(el) || !inMenu(el)) continue;
                const key = text.toLowerCase();
                if (seen.has(key)) continue;
                seen.add(key);
                items.push(text);
                if (items.length >= 6) break;
            }
            return JSON.stringify(items);
        })()
        """;

    const string RemoveItemVisibleScript = """
        (() => {
            const visible = (el) => {
                const box = el.getBoundingClientRect();
                if (box.width < 2 || box.height < 2) return false;
                const style = getComputedStyle(el);
                return style.display !== "none" && style.visibility !== "hidden" && Number(style.opacity) !== 0;
            };
            const inMenu = (el) => {
                const root = el.closest("[role='menu'], .ant-dropdown, .ant-dropdown-menu");
                return !!root && visible(root);
            };
            for (const el of document.querySelectorAll("[role='menuitem'], .ant-dropdown-menu-item")) {
                const text = (el.innerText || "").replace(/\s+/g, " ").trim();
                if (text.toLowerCase() === "remove from list" && visible(el) && inMenu(el)) return "ready";
            }
            return "no-item";
        })()
        """;

    const string ClickRemoveItemScript = """
        (() => {
            const visible = (el) => {
                const box = el.getBoundingClientRect();
                if (box.width < 2 || box.height < 2) return false;
                const style = getComputedStyle(el);
                return style.display !== "none" && style.visibility !== "hidden" && Number(style.opacity) !== 0;
            };
            const inMenu = (el) => {
                const root = el.closest("[role='menu'], .ant-dropdown, .ant-dropdown-menu");
                return !!root && visible(root);
            };
            const hits = [...document.querySelectorAll("[role='menuitem'], .ant-dropdown-menu-item")]
                .filter(el => visible(el) && inMenu(el) && (el.innerText || "").replace(/\s+/g, " ").trim().toLowerCase() === "remove from list");
            if (hits.length === 0) return "no-item";
            hits[0].click();
            return "clicked";
        })()
        """;

    const string TurboModalScript = """
        (() => {
            const visible = (el) => {
                const box = el.getBoundingClientRect();
                if (box.width < 2 || box.height < 2) return false;
                const style = getComputedStyle(el);
                return style.display !== "none" && style.visibility !== "hidden" && Number(style.opacity) !== 0;
            };
            for (const el of document.querySelectorAll("div, section, [role='dialog']")) {
                const text = (el.innerText || "").replace(/\s+/g, " ").trim().toLowerCase();
                if (text.length === 0 || text.length > 700) continue;
                if (!text.includes("track multiple job searches at once")) continue;
                if (!text.includes("upgrade to turbo")) continue;
                if (visible(el)) return "open";
            }
            return "closed";
        })()
        """;

    const string CloseTurboModalScript = """
        (() => {
            const visible = (el) => {
                const box = el.getBoundingClientRect();
                if (box.width < 2 || box.height < 2) return false;
                const style = getComputedStyle(el);
                return style.display !== "none" && style.visibility !== "hidden" && Number(style.opacity) !== 0;
            };
            let modal = null;
            for (const el of document.querySelectorAll("div, section, [role='dialog']")) {
                const text = (el.innerText || "").replace(/\s+/g, " ").trim().toLowerCase();
                if (text.length === 0 || text.length > 700) continue;
                if (!text.includes("track multiple job searches at once") || !text.includes("upgrade to turbo")) continue;
                if (!visible(el)) continue;
                if (!modal || text.length < (modal.innerText || "").length) modal = el;
            }
            if (!modal) return "absent";
            const closer = [...modal.querySelectorAll("button, [role='button']")].find(el => {
                if (!visible(el)) return false;
                const label = ((el.getAttribute("aria-label") || "") + " " + (el.getAttribute("title") || "") + " " + (el.innerText || ""))
                    .replace(/\s+/g, " ").trim().toLowerCase();
                if (label.includes("upgrade") || label.includes("turbo")) return false;
                return label === "x" || label === "close" || label.includes("close") || label.includes("dismiss");
            });
            if (closer) {
                closer.click();
                return "clicked";
            }
            document.dispatchEvent(new KeyboardEvent("keydown", {
                key: "Escape", code: "Escape", keyCode: 27, which: 27, bubbles: true
            }));
            return "escape";
        })()
        """;

    const string DismissMenuScript = """
        (() => {
            document.dispatchEvent(new KeyboardEvent("keydown", {
                key: "Escape", code: "Escape", keyCode: 27, which: 27, bubbles: true
            }));
            return "dismissed";
        })()
        """;

    static string CardGoneScript(string id) => """
        (() => {
            const path = "/jobs/info/__JOB_ID__";
            let present = false;
            for (const link of document.querySelectorAll('a[href*="/jobs/info/"]')) {
                try {
                    const name = new URL(link.href).pathname.toLowerCase().replace(/\/$/, "");
                    if (name === path) { present = true; break; }
                } catch (e) { }
            }
            if (!present) return "gone";

            const phrase = "this job has been removed from your list";
            const visible = (el) => {
                const box = el.getBoundingClientRect();
                if (box.width < 2 || box.height < 2) return false;
                const style = getComputedStyle(el);
                return style.display !== "none" && style.visibility !== "hidden" && Number(style.opacity) !== 0;
            };
            for (const el of document.querySelectorAll("div, span, p")) {
                const text = (el.innerText || "").replace(/\s+/g, " ").trim().toLowerCase();
                if (text.length === 0 || text.length > 240 || !text.includes(phrase)) continue;
                if (visible(el)) return "toast";
            }
            return "present";
        })()
        """.Replace("__JOB_ID__", id, StringComparison.Ordinal);

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

    /// <summary>Temporary. Find Jobs stays on the cards already rendered and does not scroll.</summary>
    static readonly bool EnableFindJobsAutoScroll = false;

    readonly record struct ListedJob(string Id, string Url);

    enum ListedImport { Imported, Filtered, AlreadyThere, Technical }

    enum FirstCardRemoval { Removed, Changed, Failed }

    static string RecommendListUrl(string? current)
    {
        if (!string.IsNullOrWhiteSpace(current) &&
            current.Contains("/jobs/recommend", StringComparison.OrdinalIgnoreCase))
            return current;
        return "https://jobright.ai/jobs/recommend";
    }

    /// <summary>The first /jobs/info link in the current page, read again every cycle.</summary>
    async Task<ListedJob?> ReadFirstJobAsync()
    {
        const string script = """
            (() => {
                const extractId = (href) => {
                    const raw = String(href || "");
                    try {
                        const match = new URL(raw, location.origin).pathname.toLowerCase().match(/\/jobs\/info\/([0-9a-f]{24})/);
                        if (match) return match[1];
                    } catch (e) { }
                    const fallback = raw.toLowerCase().match(/\/jobs\/info\/([0-9a-f]{24})/);
                    return fallback ? fallback[1] : "";
                };
                for (const anchor of document.querySelectorAll('a[href*="/jobs/info/"]')) {
                    const href = anchor.href || anchor.getAttribute("href") || "";
                    const job = extractId(href);
                    if (!job) continue;
                    return JSON.stringify({ id: job, url: href });
                }
                return "";
            })()
            """;
        var raw = await PageStatusAsync(script);
        if (string.IsNullOrWhiteSpace(raw) || !raw.TrimStart().StartsWith('{'))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var id = doc.RootElement.TryGetProperty("id", out var job) ? job.GetString() : null;
            var url = doc.RootElement.TryGetProperty("url", out var link) ? link.GetString() : null;
            if (id is null || url is null || !IsJobrightJobId(id))
                return null;
            return new ListedJob(id, url);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    async Task<bool> EnsureRecommendAsync(string recommendUrl)
    {
        if (await PageIsReadyAsync(recommendUrl))
            return !_stopFinding;
        if (!await NavigateAndWaitAsync(recommendUrl, TimeSpan.FromSeconds(15)))
            return false;
        await WaitForJobListReadyAsync();
        return !_stopFinding;
    }

    static string JobListedScript(string id) => """
        (() => {
            const id = "__JOB_ID__";
            const extractId = (href) => {
                const raw = String(href || "");
                try {
                    const match = new URL(raw, location.origin).pathname.toLowerCase().match(/\/jobs\/info\/([0-9a-f]{24})/);
                    if (match) return match[1];
                } catch (e) { }
                const fallback = raw.toLowerCase().match(/\/jobs\/info\/([0-9a-f]{24})/);
                return fallback ? fallback[1] : "";
            };
            for (const link of document.querySelectorAll('a[href*="/jobs/info/"]')) {
                if (extractId(link.href || link.getAttribute("href")) === id) return "present";
            }
            return "gone";
        })()
        """.Replace("__JOB_ID__", id, StringComparison.Ordinal);

    async Task<bool> JobIsGoneAsync(string id)
    {
        for (var i = 0; i < 8 && !_stopFinding; i++)
        {
            if (await PageStatusAsync(JobListedScript(id)) == "gone")
                return true;
            await Task.Delay(80);
        }
        return await PageStatusAsync(JobListedScript(id)) == "gone";
    }

    void LogFirstCardDiagnostics(string id, string payload)
    {
        if (string.IsNullOrWhiteSpace(payload) || !payload.TrimStart().StartsWith('{'))
        {
            PerfLog.Line($"JOBBROWSER first-card-diag skipped reason=not-json");
            return;
        }
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var href = TextProp(root, "anchorHref");
            var tag = TextProp(root, "anchorTag");
            if (href.Length == 0 && tag.Length == 0)
            {
                PerfLog.Line($"JOBBROWSER first-card-diag skipped reason=no-anchor-fields status={TextProp(root, "status")}");
                return;
            }
            PerfLog.Line(
                $"JOBBROWSER first-anchor-debug {id}\n  href={href}\n  tag={tag}\n  class={TextProp(root, "anchorClass")}");
            if (root.TryGetProperty("ancestors", out var ancestors) && ancestors.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in ancestors.EnumerateArray())
                {
                    var depth = item.TryGetProperty("depth", out var d) && d.TryGetInt32(out var n) ? n : 0;
                    PerfLog.Line(
                        $"JOBBROWSER first-ancestor {id} depth={depth} tag={TextProp(item, "tag")} id={TextProp(item, "id")} class={TextProp(item, "className")}");
                }
            }
            if (!root.TryGetProperty("hasRow", out var hasRow) || hasRow.ValueKind != JsonValueKind.True)
                return;
            var labels = JoinProp(root, "ariaLabels");
            var uses = JoinProp(root, "useHrefs");
            var svgCount = root.TryGetProperty("svgCount", out var svg) && svg.TryGetInt32(out var svgN) ? svgN : 0;
            var ariaCount = root.TryGetProperty("ariaCount", out var aria) && aria.TryGetInt32(out var ariaN) ? ariaN : 0;
            var useCount = root.TryGetProperty("useCount", out var use) && use.TryGetInt32(out var useN) ? useN : 0;
            PerfLog.Line(
                $"JOBBROWSER first-row-debug {id}\n  rowTag={TextProp(root, "rowTag")}\n  rowId={TextProp(root, "rowId")}\n  rowClass={TextProp(root, "rowClass")}\n  svgCount={svgCount}\n  ariaLabels=[{labels}]\n  useHrefs=[{uses}]");
            PerfLog.Line($"JOBBROWSER first-more-aria-count {id} {ariaCount}");
            PerfLog.Line($"JOBBROWSER first-more-use-count {id} {useCount}");
        }
        catch (JsonException)
        {
            PerfLog.Line("JOBBROWSER first-card-diag skipped reason=json");
        }
    }

    static string TextProp(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    static string JoinProp(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var items) || items.ValueKind != JsonValueKind.Array)
            return "";
        return string.Join(",", items.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? "")
            .Where(item => item.Length > 0));
    }

    async Task LogPostImportLinksAsync(string id)
    {
        var payload = await PageStatusAsync(PostImportLinkDebugScript(id));
        if (string.IsNullOrWhiteSpace(payload) || !payload.TrimStart().StartsWith('{'))
        {
            PerfLog.Line($"JOBBROWSER postimport-link-debug {id}\n  links=0\n  ids=[]");
            PerfLog.Line($"JOBBROWSER postimport-target-not-in-dom {id}");
            return;
        }
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var links = root.TryGetProperty("links", out var n) && n.TryGetInt32(out var count) ? count : 0;
            var ids = root.TryGetProperty("ids", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString() ?? "")
                    .Where(item => item.Length > 0)
                    .ToArray()
                : Array.Empty<string>();
            PerfLog.Line($"JOBBROWSER postimport-link-debug {id}\n  links={links}\n  ids=[{string.Join(",", ids)}]");
            if (root.TryGetProperty("matches", out var matches) && matches.ValueKind == JsonValueKind.Array)
            {
                foreach (var href in matches.EnumerateArray())
                {
                    if (href.ValueKind == JsonValueKind.String)
                        PerfLog.Line($"JOBBROWSER postimport-link-match {id} href=\"{href.GetString()}\"");
                }
            }
            if (!ids.Contains(id, StringComparer.Ordinal))
                PerfLog.Line($"JOBBROWSER postimport-target-not-in-dom {id}");
        }
        catch (JsonException)
        {
            PerfLog.Line($"JOBBROWSER postimport-link-debug {id}\n  links=0\n  ids=[]");
            PerfLog.Line($"JOBBROWSER postimport-target-not-in-dom {id}");
        }
    }

    static string PostImportLinkDebugScript(string id) => """
        (() => {
            const id = "__JOB_ID__";
            const extractId = (href) => {
                const raw = String(href || "");
                try {
                    const match = new URL(raw, location.origin).pathname.toLowerCase().match(/\/jobs\/info\/([0-9a-f]{24})/);
                    if (match) return match[1];
                } catch (e) { }
                const fallback = raw.toLowerCase().match(/\/jobs\/info\/([0-9a-f]{24})/);
                return fallback ? fallback[1] : "";
            };
            const anchors = [...document.querySelectorAll('a[href*="/jobs/info/"]')];
            const parsed = anchors.map(anchor => ({
                href: anchor.getAttribute("href") || anchor.href || "",
                job: extractId(anchor.href || anchor.getAttribute("href"))
            }));
            const ids = [...new Set(parsed.map(item => item.job).filter(Boolean))];
            const matches = parsed.filter(item => item.job === id).map(item => item.href);
            return JSON.stringify({ links: anchors.length, ids, matches });
        })()
        """.Replace("__JOB_ID__", id, StringComparison.Ordinal);

    /// <summary>
    /// Opens the menu on the current first card. The card is chosen by position, then its id is
    /// checked. A different first card is not clicked.
    /// </summary>
    async Task<FirstCardRemoval> RemoveCurrentFirstCardAsync(string id, string recommendUrl)
    {
        var payload = await PageStatusAsync(OpenFirstCardMenuScript(id));
        LogFirstCardDiagnostics(id, payload);
        var opened = ReadOpen(payload);
        if (opened.Status == "changed")
        {
            PerfLog.Line($"JOBBROWSER first-card-changed expected={id} actual={opened.Card}");
            return FirstCardRemoval.Changed;
        }
        if (opened.Status is "opened" or "ambiguous")
            PerfLog.Line($"JOBBROWSER first-row-found {id}");
        PerfLog.Line("JOBBROWSER more-svg-source method=RemoveCurrentFirstCardAsync");
        PerfLog.Line($"JOBBROWSER removal-path branch={opened.Status}");
        PerfLog.Line($"JOBBROWSER more-svg-count {id} {opened.Count}");
        if (opened.Status != "opened")
            return FirstCardRemoval.Failed;

        PerfLog.Line($"JOBBROWSER removal-attempt {id}");
        if (!await AcceptRemoveMenuAsync(id))
            return FirstCardRemoval.Failed;
        return await JobLeftTheListAsync(id, recommendUrl)
            ? FirstCardRemoval.Removed
            : FirstCardRemoval.Failed;
    }

    /// <summary>
    /// Removes one job from the recommended list and requires that its link is gone afterwards.
    /// A menu miss, or a card that is still there after a reload, is not success.
    /// Used after navigation, when the card must be found again by its job id.
    /// </summary>
    async Task<bool> RemoveAndConfirmAsync(string url, string id, string recommendUrl)
    {
        if (_stopFinding)
            return false;
        PerfLog.Line($"JOBBROWSER removal-attempt {id}");
        if (!await RemoveImportedFromJobrightAsync(url, postImport: true))
            return false;
        return await JobLeftTheListAsync(id, recommendUrl);
    }

    async Task<bool> JobLeftTheListAsync(string id, string recommendUrl)
    {
        if (await JobIsGoneAsync(id))
        {
            PerfLog.Line($"JOBBROWSER removal-success {id}");
            return true;
        }

        if (_stopFinding || !await NavigateAndWaitAsync(recommendUrl, TimeSpan.FromSeconds(15)))
            return false;
        await WaitForJobListReadyAsync();
        if (_stopFinding)
            return false;
        if (await JobIsGoneAsync(id))
        {
            PerfLog.Line($"JOBBROWSER removal-success {id}");
            return true;
        }

        PerfLog.Line($"JOBBROWSER removal-failed {id} still-present");
        return false;
    }

    /// <summary>
    /// Opens one listing and runs the existing extract-and-import path. Nothing here decides filters,
    /// icons, or duplicate rules.
    /// </summary>
    async Task<ListedImport> ImportListedJobAsync(string url, string id)
    {
        PerfLog.Line($"JOBBROWSER import-start {id}");
        if (!await NavigateAndWaitAsync(url, TimeSpan.FromSeconds(15)))
        {
            PerfLog.Line($"JOBBROWSER processing-failed {id} reason=navigation");
            return ListedImport.Technical;
        }

        try
        {
            var data = await ExtractWithRetryAsync();
            if (ImportJob is null)
            {
                PerfLog.Line($"JOBBROWSER processing-failed {id} reason=import-unavailable");
                return ListedImport.Technical;
            }

            var outcome = ImportJob(data);
            switch (outcome.Kind)
            {
                case JobImportKind.Imported:
                    PerfLog.Line($"JOBBROWSER import-success {id}");
                    return ListedImport.Imported;
                case JobImportKind.Duplicate:
                    PerfLog.Line($"JOBBROWSER existing {id} true");
                    return ListedImport.AlreadyThere;
                case JobImportKind.Skipped:
                    var reason = JobImportFilter.LogReason(outcome.FilterReason);
                    PerfLog.Line($"JOBBROWSER processed-filtered {id} reason={reason}");
                    _filteredThisSession[outcome.FilterReason] =
                        _filteredThisSession.GetValueOrDefault(outcome.FilterReason) + 1;
                    return ListedImport.Filtered;
                default:
                    if (string.IsNullOrWhiteSpace(outcome.Reason))
                    {
                        PerfLog.Line($"JOBBROWSER processing-failed {id} reason=indeterminate");
                        return ListedImport.Technical;
                    }
                    PerfLog.Line($"JOBBROWSER processed-filtered {id} reason={outcome.Reason}");
                    return ListedImport.Filtered;
            }
        }
        catch (JobExtractionException)
        {
            PerfLog.Line($"JOBBROWSER processing-failed {id} reason=extract");
            return ListedImport.Technical;
        }
        catch (Exception ex)
        {
            PerfLog.Line($"JOBBROWSER processing-failed {id} reason={ex.GetType().Name}");
            return ListedImport.Technical;
        }
    }

    /// <summary>
    /// Processes the recommended list one first-card at a time until <paramref name="target"/>
    /// jobs have been imported. Already-imported and filtered cards are removed and do not count.
    /// </summary>
    async Task FindJobsUntilTargetAsync(int target, string source)
    {
        PerfLog.Line($"JOBBROWSER find start source={source}");
        if (!EnableFindJobsAutoScroll)
            PerfLog.Line("JOBBROWSER auto-scroll disabled");

        if (_view?.CoreWebView2 is null || _extractor is null || ImportJob is null)
        {
            PerfLog.Line("JOBBROWSER processing-failed none reason=not-ready");
            return;
        }

        var recommendUrl = RecommendListUrl(_view.Source?.ToString());
        var imported = 0;
        var cycle = 0;
        var failed = false;
        string? previousId = null;

        if (!await EnsureRecommendAsync(recommendUrl))
            {
                PerfLog.Line("JOBBROWSER processing-failed none reason=navigation");
                PerfLog.Line("JOBBROWSER leaving-job-on-jobright none");
                return;
            }

            while (imported < target && !_stopFinding)
            {
                cycle++;
                PerfLog.Line($"JOBBROWSER cycle {cycle}");
                StatusText.Text = $"Imported {imported}/{target}";

                var current = await ReadFirstJobAsync();
                if (current is null)
                {
                    PerfLog.Line("JOBBROWSER first-job none");
                    break;
                }

                var id = current.Value.Id;
                var url = current.Value.Url;
                PerfLog.Line($"JOBBROWSER first-job id={id} url={JobBrowser.SafeForLog(url)}");
                if (id == previousId)
                {
                    PerfLog.Line($"JOBBROWSER processing-failed {id} reason=still-first");
                    PerfLog.Line($"JOBBROWSER leaving-job-on-jobright {id}");
                    failed = true;
                    break;
                }
                previousId = id;
                if (_stopFinding)
                    break;

                if (JobExists?.Invoke(url) == true)
                {
                    PerfLog.Line("IDENTITY lookup source=jobright id=" + id);
                    PerfLog.Line($"JOBBROWSER existing {id} true");
                    if (_stopFinding)
                        break;
                    var removed = await RemoveCurrentFirstCardAsync(id, recommendUrl);
                    if (removed == FirstCardRemoval.Changed)
                    {
                        previousId = null;
                        continue;
                    }
                    if (removed != FirstCardRemoval.Removed)
                    {
                        PerfLog.Line($"JOBBROWSER leaving-job-on-jobright {id}");
                        failed = true;
                        break;
                    }
                    continue;
                }

                PerfLog.Line($"JOBBROWSER existing {id} false");
                if (_stopFinding)
                    break;

                var result = await ImportListedJobAsync(url, id);
                if (result == ListedImport.Imported)
                    imported++;
                if (_stopFinding)
                    break;
                if (result == ListedImport.Technical)
                {
                    PerfLog.Line($"JOBBROWSER leaving-job-on-jobright {id}");
                    failed = true;
                    break;
                }

                PerfLog.Line($"JOBBROWSER return-recommend {id}");
                if (!await EnsureRecommendAsync(recommendUrl))
                {
                    PerfLog.Line($"JOBBROWSER processing-failed {id} reason=navigation");
                    PerfLog.Line($"JOBBROWSER leaving-job-on-jobright {id}");
                    failed = true;
                    break;
                }
                if (_stopFinding)
                    break;
                if (!await RemoveAndConfirmAsync(url, id, recommendUrl))
                {
                    PerfLog.Line($"JOBBROWSER leaving-job-on-jobright {id}");
                    failed = true;
                    break;
                }
            }

        StatusText.Text =
            imported >= target ? $"Imported {imported} of {target}."
            : _stopFinding ? $"Stopped — imported {imported} of {target}."
            : failed ? $"Stopped — could not continue. Imported {imported} of {target}."
            : $"No more jobs on this list — imported {imported} of {target}.";

        PerfLog.Line(
            $"JOBBROWSER find finished imported={imported} target={target} stopped={_stopFinding} failed={failed}");
    }

    async void FindJobs_Click(object sender, RoutedEventArgs e)
    {
        const string source = "FindButtonClick";
        if (_blockFindJobsClick ||
            _findJobsRunning ||
            _view?.CoreWebView2 is null ||
            _navigating ||
            _importing)
        {
            var reason = _findJobsRunning ? "already-running"
                : _blockFindJobsClick ? "click-blocked"
                : _view?.CoreWebView2 is null ? "no-view"
                : _navigating ? "navigating"
                : "importing";
            PerfLog.Line($"JOBBROWSER find ignored source={source} reason={reason}");
            return;
        }

        PerfLog.Line($"JOBBROWSER find invoke source={source}");
        PerfLog.Line($"JOBBROWSER find-enter source={source} running={_findJobsRunning}");
        _findJobsRunning = true;
        _importing = true;

        try
        {
            _stopFinding = false;
            UpdateImportButton();
            await WaitForJobListReadyAsync();

            await FindJobsUntilTargetAsync(GetJobTarget(), source);
        }
        finally
        {
            _importing = false;
            _findJobsRunning = false;
            _blockFindJobsClick = true;
            UpdateImportButton();
            _blockFindJobsClick = false;
        }
    }
    

    
    /// <summary>
    /// Navigate to one job URL and wait until WebView2 reports that navigation completed.
    /// </summary>
    /// <summary>
    /// Navigates and then polls the live page address. A missing completion event is not a failure
    /// when the WebView is already on the requested page.
    /// </summary>
    async Task<bool> NavigateAndWaitAsync(string url, TimeSpan timeout) {
        var view = _view;
        if (view?.CoreWebView2 is null)
            return false;
        if (await PageIsReadyAsync(url))
            return true;

        var tcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs e) {
            view.NavigationCompleted -= Completed;
            tcs.TrySetResult(e.IsSuccess);
        }

        view.NavigationCompleted += Completed;
        try {
            try
            {
                if (view.CoreWebView2.IsSuspended)
                    view.CoreWebView2.Resume();
            }
            catch (InvalidOperationException)
            {
            }

            view.CoreWebView2.Navigate(url);
            var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
            while (Environment.TickCount64 < deadline)
            {
                if (await PageIsReadyAsync(url))
                    return true;
                if (tcs.Task.IsCompleted && await tcs.Task && await PageIsReadyAsync(url))
                    return true;
                await Task.Delay(200);
            }

            return await PageIsReadyAsync(url);
        } catch {
            return false;
        } finally {
            view.NavigationCompleted -= Completed;
        }
    }

    async Task<bool> PageIsReadyAsync(string target)
    {
        if (PageShows(target))
            return true;
        if (_view?.CoreWebView2 is null)
            return false;
        try
        {
            var href = await PageStatusAsync("location.href");
            return AddressesMatch(href, target);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return false;
        }
    }

    bool PageShows(string target)
    {
        var live = _view?.CoreWebView2?.Source;
        if (AddressesMatch(live, target))
            return true;
        return AddressesMatch(_view?.Source?.ToString(), target);
    }

    static bool AddressesMatch(string? source, string target)
    {
        if (string.IsNullOrWhiteSpace(source) ||
            !Uri.TryCreate(source, UriKind.Absolute, out var src) ||
            !Uri.TryCreate(target, UriKind.Absolute, out var dst))
            return false;
        if (!src.Host.Equals(dst.Host, StringComparison.OrdinalIgnoreCase))
            return false;
        return string.Equals(
            src.AbsolutePath.TrimEnd('/'),
            dst.AbsolutePath.TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);
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
    /// <summary>
    /// Imports until <c>target</c> jobs have ACTUALLY been imported, not until <c>target</c> jobs
    /// have been examined. Duplicates, filter refusals and failures do not count, so the list is
    /// topped up — more results are discovered with the existing scanner mechanics — whenever it
    /// runs out before the target is met. It ends on the target, on Stop, or at the end of the list.
    /// </summary>
    public async Task AutoImportFoundJobsAsync() {
        if (_importing ||
            _foundJobUrls.Count == 0 ||
            _view?.CoreWebView2 is null ||
            _extractor is null ||
            ImportJob is null) {
            return;
        }

        var target = GetJobTarget();
        var returnUrl = _view.Source?.ToString();

        var imported = 0;
        var existing = 0;
        var failed = 0;
        var filtered = 0;
        var examined = 0;
        var reachedEnd = false;

        _importing = true;
        _stopFinding = false;
        UpdateImportButton();

        ResultBanner.Visibility = Visibility.Collapsed;

        try {
            var next = 0;

            while (imported < target && !_stopFinding) {
                // Nothing left to try: top up from the results page with the normal scanner.
                if (next >= _foundJobUrls.Count) {
                    if (reachedEnd) break;

                    reachedEnd = await TopUpFoundJobsAsync(returnUrl, target - imported);
                    if (next >= _foundJobUrls.Count) break;   // nothing new arrived
                    continue;
                }

                var url = _foundJobUrls[next++];
                var key = JobKey(url);

                // One attempt per job per session: a card that reappears after a scroll is not reopened.
                if (!_processedJobKeys.Add(key)) continue;

                if (ImportJobAlreadyExists(url)) {
                    existing++;
                    PerfLog.Line("JOBBROWSER auto skip existing " + JobBrowser.SafeForLog(url));
                    continue;
                }

                examined++;
                FoundJobsText.Text = $"Imported: {imported}/{target}";
                StatusText.Text = $"Opening job {examined} — imported {imported} of {target}…";

                PerfLog.Line($"JOBBROWSER auto job {examined} " + JobBrowser.SafeForLog(url));

                var navigated = await NavigateAndWaitAsync(url, TimeSpan.FromSeconds(15));

                if (!navigated) {
                    failed++;
                    PerfLog.Line($"JOBBROWSER auto failed {examined} navigation");
                    continue;
                }

                try {
                    StatusText.Text = $"Reading job {examined} — imported {imported} of {target}…";

                    var data = await ExtractWithRetryAsync();
                    var outcome = ImportJob(data);

                    switch (outcome.Kind) {
                        case JobImportKind.Imported:
                            imported++;
                            FoundJobsText.Text = $"Imported: {imported}/{target}";
                            PerfLog.Line("JOBBROWSER auto imported " + outcome.JobId);
                            break;

                        case JobImportKind.Duplicate:
                            existing++;
                            PerfLog.Line("JOBBROWSER auto existing " + outcome.JobId);
                            break;

                        case JobImportKind.Skipped:
                            // An intentional refusal: no task, and it never counts toward the target.
                            filtered++;
                            _filteredThisSession[outcome.FilterReason] =
                                _filteredThisSession.GetValueOrDefault(outcome.FilterReason) + 1;

                            PerfLog.Line("JOBBROWSER import skipped " + JobBrowser.SafeForLog(url) +
                                         " reason=" + JobImportFilter.LogReason(outcome.FilterReason));
                            break;

                        default:
                            failed++;
                            PerfLog.Line("JOBBROWSER auto failed " + outcome.Reason);
                            break;
                    }
                } catch (JobExtractionException ex) {
                    failed++;
                    PerfLog.Line("JOBBROWSER auto extract failed " + ex.Message);
                } catch (Exception ex) {
                    failed++;
                    PerfLog.Line("JOBBROWSER auto failed " + ex.GetType().Name);
                }
            }

            // Return to the Jobright results page where Find Jobs started.
            if (!string.IsNullOrWhiteSpace(returnUrl) && _view?.CoreWebView2 is not null) {
                StatusText.Text = "Returning to job results…";
                await NavigateAndWaitAsync(returnUrl, TimeSpan.FromSeconds(15));
            }

            var filteredLine = filtered > 0 ? $"\nFiltered out: {filtered}{FilteredBreakdown()}" : "";

            ShowResult(
                "Import finished",
                $"Imported: {imported} of {target}\n" +
                $"Existing: {existing}\n" +
                $"Failed: {failed}" + filteredLine,
                failed == 0 ? Success : Neutral);

            StatusText.Text =
                (imported >= target ? $"Finished — imported {imported}."
                 : _stopFinding ? $"Stopped — imported {imported} of {target}."
                 : $"Reached the end of the list — imported {imported} of {target}.") +
                $" Existing {existing}, filtered {filtered}, failed {failed}.";

            PerfLog.Line($"JOBBROWSER auto finished imported={imported} target={target} " +
                         $"existing={existing} filtered={filtered} failed={failed} " +
                         $"end={reachedEnd} stopped={_stopFinding}");
        } finally {
            _importing = false;
            FoundJobsText.Text = $"Found: {_sessionFound}";
            UpdateImportButton();
        }
    }

    /// <summary>A safe one-line summary of which filters refused jobs — counts and reasons only.</summary>
    string FilteredBreakdown() {
        if (_filteredThisSession.Count == 0) return "";
        var parts = JobImportFilter.Switches
            .Where(s => _filteredThisSession.ContainsKey(s.Reason))
            .Select(s => $"{JobImportFilter.ShortText(s.Reason)} {_filteredThisSession[s.Reason]}");
        return " (" + string.Join(", ", parts) + ")";
    }

    /// <summary>
    /// Goes back to the results page and discovers more jobs with the SAME collect-and-scroll
    /// mechanics Find Jobs uses, so URL de-duplication, Stop and end-of-list behave identically.
    /// Returns true when the list is exhausted.
    /// </summary>
    async Task<bool> TopUpFoundJobsAsync(string? resultsUrl, int stillNeeded) {
        if (string.IsNullOrWhiteSpace(resultsUrl) || _view?.CoreWebView2 is null) return true;

        StatusText.Text = $"Looking for {stillNeeded} more job(s)…";

        if (!await NavigateAndWaitAsync(resultsUrl, TimeSpan.FromSeconds(15))) return true;
        await WaitForJobListReadyAsync();

        // Ask for enough new links to cover what the target still needs.
        var limit = _foundJobUrls.Count + Math.Max(1, stillNeeded);
        var idleRounds = 0;

        while (_foundJobUrls.Count < limit && !_stopFinding) {
            var added = await CollectJobsFromCurrentPageAsync(limit);
            if (_foundJobUrls.Count >= limit || _stopFinding) break;

            var moved = await ScrollDownAsync();
            idleRounds = added == 0 && !moved ? idleRounds + 1 : 0;
            if (idleRounds >= EndOfListRounds) {
                PerfLog.Line($"JOBBROWSER auto top-up reached the end new={_foundJobUrls.Count}");
                return true;
            }
        }

        PerfLog.Line($"JOBBROWSER auto top-up found={_foundJobUrls.Count} needed={stillNeeded}");
        return false;
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
