using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Brush = System.Windows.Media.Brush;

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
public partial class JobBrowserWindow : Window {
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

    /// <summary>
    /// Where an extracted job goes. MainWindow supplies it and runs the shared importer against its
    /// live task list, so this window stays free of storage and queue code.
    /// </summary>
    public Func<JobImportData, JobImportOutcome>? ImportJob { get; set; }

    /// <summary>
    /// The tests open about:blank on a throwaway profile, so no third-party request is made and the
    /// real signed-in profile is never touched while the plumbing is verified.
    /// </summary>
    public JobBrowserWindow(string? startUrl = null, string? userDataFolder = null) {
        InitializeComponent();
        _startUrl = string.IsNullOrWhiteSpace(startUrl) ? JobBrowser.HomeUrl : startUrl;
        _profileFolder = string.IsNullOrWhiteSpace(userDataFolder) ? JobBrowser.UserDataFolder : userDataFolder;

        Loaded += async (_, _) => await InitializeBrowserAsync();
        Closed += (_, _) => DisposeBrowser();
    }

    /// <summary>True once the browser is usable — the tests wait on this instead of a sleep.</summary>
    public bool IsReady => _view?.CoreWebView2 is not null;

    /// <summary>True while an import is running. The button is disabled for exactly this long.</summary>
    public bool IsImporting => _importing;

    /// <summary>The profile this window is running on, so separation can be asserted.</summary>
    public string ProfileFolder => _profileFolder;

    async Task InitializeBrowserAsync() {
        if (_view is not null) return;

        try {
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
            view.CoreWebView2.NavigationStarting += (_, _) => { _navigating = true; UpdateImportButton(); };
            view.NavigationCompleted += (_, _) => { _navigating = false; UpdateNavigationButtons(); UpdateImportButton(); };

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
        } catch (Exception ex) {
            // A browser failure here must stay here: the queue and the ChatGPT pane are untouched.
            PerfLog.Line("JOBBROWSER initialize FAILED: " + ex.Message);
            StatusText.Text = "The job browser could not start: " + ex.Message;
        }
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

    /// <summary>
    /// Import is offered only when it can work: the browser is up, nothing is loading, the address
    /// is a single job, something is ready to receive the job, and no import is already running.
    /// </summary>
    void UpdateImportButton() {
        var url = _view?.Source?.ToString();
        var onJob = JobrightPageExtractor.IsJobPage(url);

        ImportButton.IsEnabled = IsReady && !_navigating && !_importing && onJob && ImportJob is not null;
        ImportButton.ToolTip = onJob
            ? "Add this job to the queue."
            : "Open a single Jobright job to import it into the queue.";
    }

    async void Import_Click(object sender, RoutedEventArgs e) => await ImportCurrentJobAsync();

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
