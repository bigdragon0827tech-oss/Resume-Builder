using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using System.Runtime.InteropServices;
using RadioButton = System.Windows.Controls.RadioButton;

namespace ResumeBuilder;





public partial class MainWindow : Window {
    readonly ObservableCollection<JobTask> _tasks = new(Storage.LoadTasks());

    /// <summary>
    /// The live queue. The Settings tracking tab edits these same task objects rather than a second
    /// copy loaded from disk, so a status change there can never be overwritten by this window's
    /// next save.
    /// </summary>
    public IReadOnlyList<JobTask> Tasks => _tasks;

    /// <summary>What TaskList shows: an Active or History filter over <see cref="_tasks"/> (TaskViews).</summary>
    readonly System.Windows.Data.ListCollectionView _taskView;
    bool _showHistory;

    readonly ClipboardWatcher _watcher = new();
    readonly QueueRunner _queue = new();
    readonly GlobalHotkey _hotkey = new();
    readonly CaptureWatchdog _captureWatchdog = new();
    /// <summary>The job whose answer ChatGPT confirmably finished and whose Copy is still awaited.</summary>
    string? _readyJobId;
    JobTask? _activeJob;
    CoreWebView2Environment? _webEnvironment;
    Microsoft.Web.WebView2.Wpf.WebView2? _chatView;
    ChatHost? _chat;
    System.Threading.CancellationTokenSource? _sendCancellation;
    string? _activePreparedText;

    /// <summary>Embedded settings / applications view (shell). Replaces the old floating Settings window.</summary>
    SettingsWindow SettingsHost => SettingsView;

    bool _darkTheme = true;

    /// <summary>The active job's prepared request. Built ONCE; every retry re-sends this same text.</summary>
    PreparedRequest? _activePrepared;

    /// <summary>Which ChatGPT attempt (1..GptAttempts.MaxAttempts) the active job is on; 0 when idle.</summary>
    int _attempt;

    /// <summary>One copy keystroke per attempt, whether it was asked for at ReadyUnconfirmed or Ready.</summary>
    readonly CaptureRequestGate _copyRequest=new();

    /// <summary>Prevents overlapping AdvanceQueueAsync loops (nested fail→advance + outer advance).</summary>
    int _advanceDepth;

    /// <summary>Live ChatGPT WebView2 generation — bumped on create and on dispose.</summary>
    readonly BrowserGeneration _browserGen = new();

    /// <summary>Cancelled when the live browser is disposed so delayed focus/copy work exits.</summary>
    CancellationTokenSource? _browserOpsCts;

    IntPtr _previousWorkWindow = IntPtr.Zero;

    void RememberPreviousWorkWindow()
    {
        try
        {
            var current = GlobalHotkey.CurrentForegroundWindow();

            var resumeBuilder =
                new System.Windows.Interop.WindowInteropHelper(this).Handle;

            if (current == IntPtr.Zero)
            {
                _previousWorkWindow = IntPtr.Zero;
                PerfLog.Line("WORK WINDOW save skipped - no foreground window");
                return;
            }

            if (GlobalHotkey.IsForegroundOwnedBy(resumeBuilder)
                || GlobalHotkey.IsForegroundOwnedByProcess(Environment.ProcessId))
            {
                _previousWorkWindow = IntPtr.Zero;
                PerfLog.Line("WORK WINDOW save skipped - ResumeBuilder already foreground");
                return;
            }

            _previousWorkWindow = current;

            PerfLog.Line(
                $"WORK WINDOW saved hwnd=0x{current.ToInt64():X}");
        }
        catch(Exception ex)
        {
            _previousWorkWindow = IntPtr.Zero;
            PerfLog.Line("WORK WINDOW save failed: " + ex.Message);
        }
    }

    void RestorePreviousWorkWindow(string reason)
    {
        var saved = _previousWorkWindow;

        // Consume it once only.
        _previousWorkWindow = IntPtr.Zero;

        if (saved == IntPtr.Zero)
            return;

        try
        {
            var resumeBuilder =
                new System.Windows.Interop.WindowInteropHelper(this).Handle;

            // Important:
            // if the user has already switched somewhere else manually,
            // do not steal focus from that new window.
            if (!GlobalHotkey.IsForegroundOwnedBy(resumeBuilder)
                && !GlobalHotkey.IsForegroundOwnedByProcess(Environment.ProcessId))
            {
                PerfLog.Line(
                    $"WORK WINDOW restore skipped - foreground already changed ({reason})");
                return;
            }

            if (GlobalHotkey.TryRestoreForegroundWindow(saved))
            {
                PerfLog.Line(
                    $"WORK WINDOW restored hwnd=0x{saved.ToInt64():X} ({reason})");
            }
            else
            {
                PerfLog.Line(
                    $"WORK WINDOW restore skipped - window unavailable or activation failed ({reason})");
            }
        }
        catch(Exception ex)
        {
            PerfLog.Line(
                $"WORK WINDOW restore failed ({reason}): {ex.Message}");
        }
    }

    public MainWindow() {
        InitializeComponent();
        _chat = new ChatHost(CreateChatViewAsync, DisposeChatViewAsync);

        // A job left Processing by a crash or a close would never be re-run; put it back in the queue.
        var recovered = QueueRunner.RecoverStaleProcessing(_tasks);
        // The platform is always derived from ApplyUrl: fill in older jobs and apply detector updates.
        var redetected = ApplicationPlatformDetector.Refresh(_tasks);
        if (recovered > 0 || redetected > 0) Storage.SaveTasks(_tasks);

        _taskView = TaskViews.CreateView(_tasks, () => _showHistory);
        TaskList.ItemsSource = _taskView;
        foreach (var t in _tasks) WatchTask(t);
        _tasks.CollectionChanged += (_, e) => {
            if (e.NewItems is not null) foreach (JobTask t in e.NewItems) WatchTask(t);
            UpdateViewSwitch();
        };
        Loaded += MainWindow_Loaded;
        Closed += (_, _) => {
            DismissAnswerReady("app closed");
            _hotkey.Dispose();
            _watcher.Dispose();
            JobBrowserView.Shutdown();
            _ = DisposeChatViewAsync();
        };
        _watcher.TextCaptured += OnClipboardTextCaptured;
        UpdateSummary();
        UpdateViewSwitch();
        WireShell();
        if (recovered > 0) QueueStatus.Text = $"Recovered {recovered} job(s) left in progress by the previous session.";
    }

    void WireShell() {
        JobBrowserView.ImportJob = ImportFromBrowser;
        JobBrowserView.JobExists = url => _tasks.Any(t =>
            JobUrls.Normalize(t.Link) == JobUrls.Normalize(url));
        JobBrowserView.RecordApplyUrl = RecordApplyUrlFromBrowser;

        DashboardView.RefreshInputRequested += () => RefreshInput();
        DashboardView.OpenJobBrowserRequested += () => NavigateTo("JobBrowser");
        DashboardView.StartQueueRequested += () => StartQueue_Click(this, new RoutedEventArgs());
        DashboardView.OpenReadyToApplyRequested += () => {
            NavigateTo("Applications");
            SettingsHost.ShowReadyToApplyQueue();
        };
    }

    async void MainWindow_Loaded(object sender, RoutedEventArgs e) {
        PerfLog.Clear();
        PerfLog.Snapshot("startup");
        _watcher.Attach(this);
        RegisterFocusHotkey();
        await EnsureChatAsync();          // show ChatGPT so the user can sign in
        RefreshInput();
    }

    // ---------- A6.6.12 ChatGPT WebView2 lifetime ----------

    /// <summary>The live browser, or null while it is recycled away.</summary>
    CoreWebView2? Chat => _chatView?.CoreWebView2;

    async Task EnsureChatAsync() {
        if (_chat is null) return;
        try {
            // Host and view can desync if a dispose cleared the control while ChatHost still
            // believed a browser was alive — EnsureAsync would then no-op and leave Chat null.
            if (ChatHost.IsDesynced(_chat.IsAlive, _chatView?.CoreWebView2 is not null)) {
                PerfLog.Line("BROWSER desync: ChatHost.IsAlive but CoreWebView2 missing — recycling before ensure");
                await _chat.RecycleAsync();
            }

            PerfLog.Line($"BROWSER ensure requested alive={_chat.IsAlive} releasing={_chat.IsReleasing}");
            await _chat.EnsureAsync();

            if (_chatView?.CoreWebView2 is null)
                throw new InvalidOperationException(
                    "ChatGPT WebView2 did not initialize (CoreWebView2 is null after Ensure).");

            PerfLog.Line($"BROWSER ensure ok pid={_chat.BrowserProcessId}");
        }
        catch (Exception ex) {
            PerfLog.Line("BROWSER ensure FAILED: " + ex.GetType().Name + ": " + ex.Message);
            System.Windows.MessageBox.Show(
                "ChatGPT browser could not be initialized.\n\n" + ex.Message,
                "Resume Builder v1.0", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------- job browser (embedded in shell) ----------

    void NavigateTo(string destination) {
        RadioButton? match = destination switch {
            "Dashboard" => NavDashboard,
            "JobBrowser" => NavJobBrowser,
            "TaskQueue" => NavTaskQueue,
            "AiWorkspace" => NavAiWorkspace,
            "Documents" => NavDocuments,
            "Applications" => NavApplications,
            "Settings" => NavSettings,
            _ => null
        };
        if (match is not null && match.IsChecked != true) match.IsChecked = true;
        else ShowDestination(destination);
    }

    void Nav_Checked(object sender, RoutedEventArgs e) {
        if (sender is RadioButton { IsChecked: true, Tag: string tag })
            ShowDestination(tag);
    }

    void ShowDestination(string destination) {
        DashboardView.Visibility = Visibility.Collapsed;
        JobBrowserView.Visibility = Visibility.Collapsed;
        TaskQueueView.Visibility = Visibility.Collapsed;
        AiWorkspaceView.Visibility = Visibility.Collapsed;
        DocumentsView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        QueueCommandBar.Visibility = Visibility.Collapsed;

        switch (destination) {
            case "Dashboard":
                DashboardView.Visibility = Visibility.Visible;
                PageTitleText.Text = "Dashboard";
                RefreshDashboardPage();
                break;
            case "JobBrowser":
                JobBrowserView.Visibility = Visibility.Visible;
                PageTitleText.Text = "Job Browser";
                _ = JobBrowserView.EnsureReadyAsync();
                break;
            case "TaskQueue":
                TaskQueueView.Visibility = Visibility.Visible;
                QueueCommandBar.Visibility = Visibility.Visible;
                PageTitleText.Text = "Task Queue";
                UpdateJobDetailPanels();
                break;
            case "AiWorkspace":
                AiWorkspaceView.Visibility = Visibility.Visible;
                QueueCommandBar.Visibility = Visibility.Visible;
                PageTitleText.Text = "AI Workspace";
                UpdateJobDetailPanels();
                break;
            case "Documents":
                DocumentsView.Visibility = Visibility.Visible;
                PageTitleText.Text = "Resume Documents";
                DocumentsView.Refresh(_tasks);
                break;
            case "Applications":
                SettingsView.Visibility = Visibility.Visible;
                PageTitleText.Text = "Applications";
                SettingsHost.SetShellMode(applicationsFocus: true);
                break;
            case "Settings":
                SettingsView.Visibility = Visibility.Visible;
                PageTitleText.Text = "Settings";
                SettingsHost.SetShellMode(applicationsFocus: false);
                SettingsHost.ShowSection("Candidate Profile");
                break;
        }

        // Keep WebView2 attached and Visible (1×1 keep-alive) when AI Workspace is not shown so
        // background capture is not suspended by a Collapsed ancestor.
        PlaceChatView(destination == "AiWorkspace");
    }

    /// <summary>Hosts the ChatGPT WebView2 on the AI Workspace panel or the always-visible keep-alive.</summary>
    void PlaceChatView(bool onWorkspace) {
        if (_chatView is null) return;
        try {
            if (onWorkspace) {
                if (!ReferenceEquals(ChatHostPanel.Child, _chatView)) {
                    ChatKeepAliveHost.Child = null;
                    ChatHostPanel.Child = _chatView;
                }
            } else {
                if (!ReferenceEquals(ChatKeepAliveHost.Child, _chatView)) {
                    ChatHostPanel.Child = null;
                    ChatKeepAliveHost.Child = _chatView;
                }
            }
        } catch (Exception ex) {
            PerfLog.Line("BROWSER place host failed: " + ex.GetType().Name);
        }
    }

    void RefreshDashboardPage() {
        string? active = _activeJob is null ? null : $"{_activeJob.Company} — {_activeJob.Title}";
        DashboardView.Refresh(_tasks, _queue.State, active, _attempt);
    }

    void UpdateJobDetailPanels() {
        var job = TaskList.SelectedItem as JobTask ?? _activeJob;
        if (job is null) {
            DetailCompany.Text = "No job selected";
            DetailTitle.Text = "Select a job in the queue to see details.";
            DetailMeta.Text = "";
            WorkspaceCompany.Text = "No job selected";
            WorkspaceTitle.Text = "Prepare & Send uses the selected queue job.";
            WorkspaceMeta.Text = "";
            return;
        }
        var meta =
            $"Queue: {job.StatusDisplay}\nApplication: {job.ApplicationStatus}\n" +
            $"Readiness: {job.ReadinessDisplay}\nPlatform: {JobTracker.PlatformDisplayName(job.ApplicationPlatform)}\n" +
            $"Job id: {job.JobId}" +
            (string.IsNullOrWhiteSpace(job.FailureReason) ? "" : $"\nFailure: {job.FailureReason}");
        DetailCompany.Text = job.Company;
        DetailTitle.Text = job.Title;
        DetailMeta.Text = meta;
        WorkspaceCompany.Text = job.Company;
        WorkspaceTitle.Text = job.Title;
        WorkspaceMeta.Text = meta + (_attempt > 0 ? $"\nGPT attempt {_attempt}/{GptAttempts.MaxAttempts}" : "");
        ContextChip.Text = $"{job.Company} — {job.Title}";
    }

    void OpenAiWorkspace_Click(object sender, RoutedEventArgs e) => NavigateTo("AiWorkspace");
    void ShowTaskQueue_Click(object sender, RoutedEventArgs e) => NavigateTo("TaskQueue");

    void ThemeToggle_Click(object sender, RoutedEventArgs e) {
        _darkTheme = !_darkTheme;
        var app = System.Windows.Application.Current.Resources.MergedDictionaries;
        // Replace the theme root: Themes/Theme.xaml merges Colors.Dark — swap color dictionary.
        var colors = new ResourceDictionary {
            Source = new Uri(_darkTheme
                ? "Themes/Colors.Dark.xaml"
                : "Themes/Colors.Light.xaml", UriKind.Relative)
        };
        // Theme.xaml is first merge; rebuild app resources simply:
        System.Windows.Application.Current.Resources.MergedDictionaries.Clear();
        var theme = new ResourceDictionary { Source = new Uri("Themes/Theme.xaml", UriKind.Relative) };
        if (!_darkTheme) {
            // After Theme (dark), overlay light colors so DynamicResource picks them up.
            theme.MergedDictionaries.Add(colors);
        }
        System.Windows.Application.Current.Resources.MergedDictionaries.Add(theme);
        if (!_darkTheme)
            System.Windows.Application.Current.Resources.MergedDictionaries.Add(colors);
        ThemeToggleButton.Content = _darkTheme ? "Use light theme" : "Use dark theme";
    }

    void RefreshDashboardIfOpen() {
        SettingsHost.RefreshTracking();
        if (DashboardView.Visibility == Visibility.Visible) RefreshDashboardPage();
        if (DocumentsView.Visibility == Visibility.Visible) DocumentsView.Refresh(_tasks);
    }

    /// <summary>
    /// A job extracted in the job browser joins the queue through the same importer as an Incoming
    /// JSON file. It runs on a copy of the live list exactly like <see cref="RefreshInput"/>, then
    /// adds only what is new, so selection and every existing task object are left alone.
    /// </summary>
    JobImportOutcome ImportFromBrowser(JobImportData data) {
        var list=_tasks.ToList();
        var outcome=JobImporter.ImportOne(data,JobImporter.BrowserSource,list);

        if(outcome.Kind==JobImportKind.Imported) {
            foreach(var added in list.Where(t => !_tasks.Contains(t))) _tasks.Add(added);
            UpdateSummary();
            RefreshButtons();
            ImportMessage.Text=$"Imported from the job browser: {outcome.Company} — {outcome.Title}";
            // The Applications dashboard, if open, shows the new job straight away.
            RefreshDashboardIfOpen();
        } else if(outcome.Kind==JobImportKind.Duplicate && outcome.ApplyUrlRecorded) {
            // An existing job just gained its application link (saved by the importer): update its row.
            RefreshDashboardIfOpen();
        }
        return outcome;
    }

    /// <summary>Settings asks before offering a reset; a running queue refuses it.</summary>
    public bool IsQueueRunning => _queue.IsRunning;

    /// <summary>
    /// Clears the job history in the ONE live collection, on the UI thread, then saves an empty
    /// tasks.json. Everything else follows from that: the Active/History view, its counts, the
    /// selection and buttons, the summary, the dashboard, and the job browser's duplicate check,
    /// which closes over this same list. No restart. Artifacts are deleted by the caller first.
    /// </summary>
    public void ClearJobHistoryInPlace() {
        CancelCaptureWatchdog("job history cleared");
        _activeJob=null;
        _activePrepared=null;
        _activePreparedText=null;
        _attempt=0;
        _watcher.Disarm();

        _tasks.Clear();                     // CollectionChanged -> Active/History counts
        Storage.SaveTasks(_tasks);          // []

        TaskList.SelectedItem=null;
        ShowTaskView(history:false);
        UpdateSummary();
        UpdateViewSwitch();
        RefreshButtons();
        RefreshDashboardIfOpen();
        SettingsHost.RefreshInspector();
        ImportMessage.Text="Job history cleared.";
        CaptureStatus.Text="";
        DocumentStatus.Text="";
        PerfLog.Line("RESET job history cleared from the live queue");
    }

    /// <summary>
    /// The user clicked Apply on a job page in the job browser. ApplyCapture decides whether this is
    /// an application address for a task already in the queue; only then is the live list saved.
    /// </summary>
    ApplyCaptureResult RecordApplyUrlFromBrowser(string jobPageUrl,string applyUrl) {
        var result=ApplyCapture.Record(_tasks,jobPageUrl,applyUrl,DateTime.Now);
        if(result==ApplyCaptureResult.Recorded) {
            Storage.SaveTasks(_tasks);
            // The job's platform badge and the platform filter show the new value straight away.
            RefreshDashboardIfOpen();
        }
        return result;
    }

    /// <summary>How long to wait for a browser process to actually exit before reporting a timeout.</summary>
    static readonly TimeSpan BrowserExitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Builds a new WebView2 on the shared user-data folder, so the sign-in carries over.</summary>
    async Task<int> CreateChatViewAsync() {
        Directory.CreateDirectory(Storage.WebViewUserDataFolder);

        // WebView2 initialization is unreliable while its host is Collapsed; the caller must have
        // navigated to AI Workspace first. Still force a layout pass so the panel has a real size.
        if (AiWorkspaceView.Visibility != Visibility.Visible)
            NavigateTo("AiWorkspace");
        ChatHostPanel.UpdateLayout();

        _webEnvironment ??= await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null, userDataFolder: Storage.WebViewUserDataFolder);

        var view = new Microsoft.Web.WebView2.Wpf.WebView2();
        // Prefer the workspace panel when it is visible; otherwise park on the keep-alive host.
        if (AiWorkspaceView.Visibility == Visibility.Visible)
            ChatHostPanel.Child = view;
        else
            ChatKeepAliveHost.Child = view;
        PerfLog.Line("BROWSER EnsureCoreWebView2Async…");
        await view.EnsureCoreWebView2Async(_webEnvironment);
        view.CoreWebView2.Settings.AreDevToolsEnabled = false;
        // Do NOT set Source here. Create only initializes CoreWebView2; NavigateFreshChatAsync owns
        // the first load and waits for NavigationCompleted + composer readiness. Setting Source
        // raced with that Navigate and made NavigationCompleted fire in a few milliseconds on a
        // page that was not yet usable — every post-recycle send then failed immediately.
        _browserOpsCts?.Cancel();
        _browserOpsCts?.Dispose();
        _browserOpsCts = new CancellationTokenSource();
        var gen = _browserGen.BeginNew();
        _chatView = view;

        var pid = (int)view.CoreWebView2.BrowserProcessId;
        PerfLog.Line($"BROWSER created            pid={pid} gen={gen} (about:blank — navigate owns first load)");
        return pid;
    }

    /// <summary>
    /// Destroys the browser and waits for its process to actually exit.
    ///
    /// Dispose() only asks for teardown — it returns long before the process group is gone. Over a
    /// long run that let the next job's browser start while the previous tree was still alive. So the
    /// exit is awaited on two independent signals: the environment's BrowserProcessExited event and
    /// the OS process handle. Neither is a sleep; the bounded timeout is only a safety net.
    /// Returns true only when the process is confirmed gone.
    /// </summary>
    async Task<bool> DisposeChatViewAsync() {
        // Invalidate first so any in-flight FocusChatPane / RequestCopy continuation exits before
        // _chatView is cleared — and cannot attach to a browser created for the next job.
        try { _browserOpsCts?.Cancel(); } catch { }
        _browserGen.Invalidate();

        var view = _chatView;
        _chatView = null;                       // drop the reference first
        _sendCancellation?.Cancel();            // nothing may still be polling the old browser
        _readyCancellation?.Cancel();
        if (view is null) return true;

        var pid = _chat?.BrowserProcessId ?? 0;
        PerfLog.Line($"BROWSER dispose requested  pid={pid}");

        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnBrowserExited(object? _, CoreWebView2BrowserProcessExitedEventArgs e) {
            if (pid == 0 || e.BrowserProcessId == pid) {
                PerfLog.Line($"BROWSER exit event         pid={e.BrowserProcessId} kind={e.BrowserProcessExitKind}");
                exited.TrySetResult(true);
            }
        }

        var environment = _webEnvironment;
        if (environment is not null) environment.BrowserProcessExited += OnBrowserExited;

        try { ChatHostPanel.Child = null; } catch { }
        try { ChatKeepAliveHost.Child = null; } catch { }
        try { view.Dispose(); } catch { }       // asks the controller and browser to shut down

        var confirmed = await WaitForBrowserExitAsync(exited.Task, pid);

        if (environment is not null) environment.BrowserProcessExited -= OnBrowserExited;

        if (confirmed) {
            PerfLog.Line($"BROWSER recycle completed  pid={pid}");
        } else {
            // Never claim success. Drop the environment so the next job starts from a clean one.
            PerfLog.Line($"BROWSER recycle TIMED OUT  pid={pid} after {BrowserExitTimeout.TotalSeconds:F0}s — " +
                         "the old browser may still be running; the next job will build a fresh environment.");
            _webEnvironment = null;
        }

        PerfLog.Snapshot(confirmed ? "after actual browser exit" : "after unconfirmed browser exit");
        return confirmed;
    }

    /// <summary>Waits on the exit event and on the process handle together, bounded by a timeout.</summary>
    async Task<bool> WaitForBrowserExitAsync(Task<bool> exitEvent, int pid) {
        using var cancellation = new System.Threading.CancellationTokenSource(BrowserExitTimeout);

        var handleWait = Task.CompletedTask;
        try {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            handleWait = process.WaitForExitAsync(cancellation.Token);
        } catch (ArgumentException) {
            return true;                        // already gone before we could open a handle
        } catch (Exception) {
            handleWait = Task.Delay(System.Threading.Timeout.Infinite, cancellation.Token);
        }

        var finished = await Task.WhenAny(exitEvent, handleWait);
        if (finished == exitEvent) return true;

        try { await handleWait; return true; }  // the handle signalled the exit
        catch (OperationCanceledException) { return false; }
        catch (Exception) { return false; }
    }

    /// <summary>Called once a job's result is safely saved — never while a response is pending.</summary>
    async Task RecycleChatAsync() {
        if (_chat is null) return;
        PerfLog.Line($"BROWSER recycle begin alive={_chat.IsAlive} pid={_chat.BrowserProcessId}");
        using (PerfLog.Measure("chat recycle")) await _chat.RecycleAsync();
        PerfLog.Line($"BROWSER recycle end alive={_chat.IsAlive} timedOut={_chat.LastShutdownTimedOut}");
        PerfLog.Snapshot("after ChatGPT WebView2 recycle");
        if (_chat.LastShutdownTimedOut)
            DocumentStatus.Text += (DocumentStatus.Text.Length > 0 ? "  " : "") +
                "Note: the previous ChatGPT browser did not confirm shutdown; see diagnostics.log.";
    }

    /// <summary>Called when the queue finishes or stops and nothing is pending.</summary>
    async Task ReleaseChatAsync() {
        if (_chat is null || !_chat.IsAlive) return;
        using (PerfLog.Measure("chat dispose")) await _chat.ReleaseAsync();
        PerfLog.Snapshot("after queue WebView2 disposal");
    }

    void Refresh_Click(object sender,RoutedEventArgs e)=>RefreshInput();

    void RefreshInput() {
        var list=_tasks.ToList();
        var r=JobImporter.Import(Storage.LoadSettings(),list);
        _tasks.Clear();
        foreach(var t in list) _tasks.Add(t);
        UpdateSummary();
        RefreshButtons();
        if(r.JobsQueued>0) RefreshDashboardIfOpen();
        ImportMessage.Text = r.Errors.Count>0
            ? string.Join(Environment.NewLine,r.Errors)
            : r.FilesImported==0
                ? "No new input files."
                : $"Imported: {r.FilesImported} file(s) • New: {r.JobsQueued} • Existing: {r.JobsExisting}";
    }

    // ---------- Active / History view (display only; _tasks is never split) ----------

    /// <summary>Idempotent, because RefreshInput clears and re-adds the same task objects.</summary>
    void WatchTask(JobTask task) {
        task.PropertyChanged -= Task_PropertyChanged;
        task.PropertyChanged += Task_PropertyChanged;
    }

    void Task_PropertyChanged(object? sender,System.ComponentModel.PropertyChangedEventArgs e) {
        if (e.PropertyName != nameof(JobTask.Status) || sender is not JobTask task) return;
        // A task that starts running must never be hidden: leave History if that is where it was.
        if (_showHistory && task.Status == "Processing") ShowTaskView(history:false);
        UpdateViewSwitch();
    }

    void ActiveView_Click(object sender,RoutedEventArgs e)=>ShowTaskView(history:false);
    void HistoryView_Click(object sender,RoutedEventArgs e)=>ShowTaskView(history:true);

    void ShowTaskView(bool history) {
        if (_showHistory == history) return;
        var selected = TaskList.SelectedItem;
        _showHistory = history;
        _taskView.Refresh();
        // Refresh drops the selection; keep it when the selected task is still on show.
        if (selected is JobTask t && TaskViews.Belongs(t, history)) TaskList.SelectedItem = t;
        UpdateViewSwitch();
        RefreshButtons();
    }

    void UpdateViewSwitch() {
        ActiveViewButton.Content = $"Active ({TaskViews.ActiveCount(_tasks)})";
        HistoryViewButton.Content = $"History ({TaskViews.HistoryCount(_tasks)})";
        ActiveViewButton.FontWeight = _showHistory ? FontWeights.Normal : FontWeights.SemiBold;
        HistoryViewButton.FontWeight = _showHistory ? FontWeights.SemiBold : FontWeights.Normal;
        UpdateQueueEmptyState();
    }

    void UpdateSummary() {
        SummaryText.Text=$"{_tasks.Count} task(s) • {_tasks.Count(x=>x.Status=="Queued")} queued • {_tasks.Count(x=>x.Status=="Completed")} completed";
        UpdateQueueEmptyState();
    }

    void UpdateQueueEmptyState() {
        if (QueueEmptyText is null) return;
        var count = _showHistory ? TaskViews.HistoryCount(_tasks) : TaskViews.ActiveCount(_tasks);
        QueueEmptyText.Text = _showHistory
            ? "No completed jobs yet."
            : "No active jobs. Import from the Job Browser or refresh Incoming.";
        QueueEmptyText.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void TaskList_SelectionChanged(object sender,SelectionChangedEventArgs e) {
        RefreshButtons();
        UpdateJobDetailPanels();
    }

    void RetryFailed_Click(object sender,RoutedEventArgs e) {
        var n=0;
        foreach(var t in _tasks) if(t.Status=="Failed"){ t.Status="Queued"; n++; }
        if(n>0) Storage.SaveTasks(_tasks);
        UpdateSummary();
        RefreshButtons();
        CaptureStatus.Text = n>0 ? $"Re-queued {n} failed job(s)." : "No failed jobs to retry.";
    }

    void RefreshButtons() {
        var running=_queue.IsRunning;
        RetryFailedButton.IsEnabled=!running && _tasks.Any(t=>t.Status=="Failed");
        GenerateDocumentsButton.IsEnabled=!running && TaskList.SelectedItem is JobTask g && g.Status=="Completed";
        ProcessSelectedButton.IsEnabled=!running && TaskList.SelectedItem is JobTask j && j.Status!="Processing";
        StartQueueButton.IsEnabled=!running && _tasks.Any(t=>t.Status=="Queued");
        PauseQueueButton.IsEnabled=running;
        PauseQueueButton.Content=_queue.State==QueueState.Paused ? "▶ Resume Queue" : "⏸ Pause Queue";
        StopQueueButton.IsEnabled=running;
        SkipJobButton.IsEnabled=running && _queue.ActiveJobId is not null;
        UpdateBusyIndicators();
    }

    /// <summary>Binds indeterminate progress bars to real queue / Processing state — never simulated.</summary>
    void UpdateBusyIndicators() {
        var busy = _queue.IsRunning || _tasks.Any(t => t.Status == "Processing");
        if (ShellBusyBar is not null)
            ShellBusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (ChatBusyBar is not null)
            ChatBusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- shared single-job run (manual and queue take the same path) ----------

    /// <summary>
    /// Prepares one job, puts it on the clipboard, arms the capture and types it into ChatGPT.
    /// Returns false only when preparation itself failed — the step that can genuinely fail.
    /// </summary>
    async Task<bool> RunJobAsync(JobTask job) {
        var settings=Storage.LoadSettings();

        PerfLog.Snapshot("before job "+job.JobId);
        PreparedRequest prepared;
        try {
            using(PerfLog.Measure("job preparation"))
                prepared=RequestPreparation.Prepare(job,settings);
        } catch(Exception ex) {
            ImportMessage.Text="Prepare failed: "+ex.Message;
            return false;
        }

        // A6.6.13 — a watchdog belongs to exactly one job; a new active job retires any other.
        CancelCaptureWatchdog(string.Equals(_captureWatchdog.JobId,job.JobId,StringComparison.OrdinalIgnoreCase)
            ? "job restarted" : "active job changed");
        _readyJobId=null;

        job.Status="Processing";
        _activeJob=job;
        _activePreparedText=prepared.Text;      // used to refuse copies of our own prompt
        _activePrepared=prepared;               // reused unchanged by every retry; Prepare runs once
        _attempt=1;
        Storage.SaveTasks(_tasks);
        UpdateSummary();
        RefreshButtons();
        SettingsHost.RefreshInspector();
        TaskList.SelectedItem=job;
        TaskList.ScrollIntoView(job);
        UpdateJobDetailPanels();
        // ChatGPT lives only on the AI Workspace page. Always show it before a send — otherwise a
        // user who switched to Task Queue (or anywhere else) after job 1 never sees the recycled
        // browser for job 2, and EnsureCoreWebView2Async can also fail inside a Collapsed host.
        NavigateTo("AiWorkspace");

        await SendAttemptAsync(job,prepared,settings);
        return true;
    }

    /// <summary>
    /// One ChatGPT attempt for an already-prepared job: arm the capture, make sure a browser exists,
    /// open a FRESH conversation, type the SAME prepared text and send it. Every send-side problem
    /// goes to <see cref="HandleGptFailureAsync"/>, which retries or gives up — the queue is never
    /// left waiting on a job that cannot be sent. Nothing here re-prepares or rewrites a file.
    /// </summary>
    async Task SendAttemptAsync(JobTask job,PreparedRequest prepared,AppSettings settings) {
        _copyRequest.ResetForAttempt();          // this attempt gets its own single copy request

        // Clipboard: a clipboard problem never marks the preparation as failed.
        ClipboardResult clip;
        using(PerfLog.Measure("clipboard write")) clip=ClipboardService.SetText(prepared.Text);
        ImportMessage.Text=$"Prepared {job.Company} — {job.Title} successfully. "+clip.Message;

        // Arm the capture before the answer can possibly arrive.
        if(settings.AutoCaptureResult && _watcher.IsListening) {
            _watcher.Arm(prepared.Text);
            CaptureStatus.Text="Waiting for the AI answer — click Copy on the ChatGPT response and it will be captured automatically.";
        } else {
            _watcher.Disarm();
            CaptureStatus.Text = settings.AutoCaptureResult
                ? "Automatic capture is unavailable; paste the answer in Settings → Result."
                : "Automatic capture is turned off; paste the answer in Settings → Result.";
        }

        // Type it into ChatGPT. Convenience only: failure leaves the clipboard fallback.
        await EnsureChatAsync();                // lazily rebuilt after the previous job recycled it
        // A missing browser used to leave the job Processing for ever: it is a retryable send failure.
        if(Chat is null) { await HandleGptFailureAsync(job,GptFailure.SendSide); return; }

        // A6.6.12 — every job starts a FRESH conversation. Previously the app only navigated when the
        // host was not chatgpt.com, so every job appended another 35 KB prompt and a long answer to
        // one page; an empty ChatGPT tab already costs ~700 MB, so that grew without bound. The
        // WebView2 user-data folder is untouched, so the signed-in session carries over.
        bool navigated;
        using(PerfLog.Measure("navigate fresh chat")) navigated=await NavigateFreshChatAsync();
        if(!navigated) {
            PerfLog.Line($"GPT send-side {job.JobId} attempt {_attempt}: fresh-chat navigation/composer not ready");
            await HandleGptFailureAsync(job,GptFailure.SendSide); return;
        }

        // With auto-fill off the user types it themselves: not an attempt this code can retry.
        if(!settings.AutoFillComposer) return;

        ComposerResult fill;
        using(PerfLog.Measure("composer fill"))
            fill=await ChatComposer.FillAsync(Chat,prepared.Text);
        ImportMessage.Text=$"Prepared {job.Company} — {job.Title}. "+fill.Message;
        if(!fill.Success) {
            PerfLog.Line($"GPT send-side {job.JobId} attempt {_attempt}: composer fill failed — {SanitizeDiag(fill.Message)}");
            await HandleGptFailureAsync(job,GptFailure.SendSide); return;
        }
        if(!settings.AutoSend) {
            QueueStatus.Text="Auto-Send is off — press Enter in ChatGPT to send the prompt.";
            return;
        }

        // A6.6.12 — click Send. Copying the answer stays manual by design.
        _sendCancellation?.Cancel();
        _sendCancellation=new CancellationTokenSource();
        SendResult send;
        using(PerfLog.Measure("auto-send"))
            send=await ChatSender.SendAsync(new WebViewChatProbe(Chat),_sendCancellation.Token);

        if(send.Success) {
            QueueStatus.Text=$"{GptAttempts.AttemptStatus(_attempt)} — {job.Company} — {job.Title}: {send.Message}";
            PerfLog.Line($"GPT send ok {job.JobId} attempt {_attempt}/{GptAttempts.MaxAttempts}");
            _ = WatchForAnswerAsync(job,settings);     // A6.6.13: tell the user when it is ready

            // If the previous completed job temporarily brought ResumeBuilder
            // forward, return the user to the window they were working in now
            // that this new job has been successfully sent.
            RestorePreviousWorkWindow("next job auto-sent");
        }
        // A cancelled send is our own doing (a retry or a stop), never a failure of this attempt.
        else if(send.Outcome!=SendOutcome.Cancelled) {
            PerfLog.Line($"GPT send failed {job.JobId} attempt {_attempt}/{GptAttempts.MaxAttempts} " +
                         $"outcome={send.Outcome} — {SanitizeDiag(send.Message)}");
            await HandleGptFailureAsync(job,GptFailure.SendSide);
        }
    }

    /// <summary>One-line diagnostic text with no prompt/clipboard payload.</summary>
    static string SanitizeDiag(string? message) {
        if (string.IsNullOrWhiteSpace(message)) return "(none)";
        var t = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return t.Length <= 160 ? t : t[..160] + "…";
    }

    // ---------- ChatGPT retry policy (GptAttempts decides; this performs the effects) ----------

    /// <summary>
    /// One place every retryable ChatGPT failure arrives at. It retries the SAME prepared request in a
    /// fresh conversation while attempts remain, and otherwise marks only the QUEUE status Failed and
    /// advances. Application tracking — status, stage timestamps, ApplyUrl, platform, readiness — is
    /// never touched here.
    /// </summary>
    async Task HandleGptFailureAsync(JobTask job,GptFailure failure) {
        // A failure from a job that is no longer the active one can never affect the job running now.
        if(_activeJob!=job || job.Status!="Processing") {
            PerfLog.Line($"GPT {GptAttempts.Reason(failure)} ignored for {job.JobId}: no longer the active attempt");
            return;
        }

        PerfLog.Line(GptAttempts.AttemptLog(_attempt,failure,job.JobId));

        if(GptAttempts.CanRetry(_attempt) && _activePrepared is PreparedRequest prepared) {
            _attempt++;
            QueueStatus.Text=$"{job.Company} — {job.Title}: {GptAttempts.RetryMessage(_attempt,failure)}";
            await ResetForRetryAsync();
            await SendAttemptAsync(job,prepared,Storage.LoadSettings());
            return;
        }

        PerfLog.Line(GptAttempts.ExhaustedLog(job.JobId));
        await FailActiveJobAsync(job,GptAttempts.FailureReason(failure),GptAttempts.ExhaustedMessage(failure));
    }

    /// <summary>
    /// Clears everything that belongs to ONE attempt and rebuilds the browser, so a retry starts from
    /// a known state. The job, its prepared text and all its tracking data are left exactly as they are.
    /// </summary>
    async Task ResetForRetryAsync() {
        _sendCancellation?.Cancel();
        _readyCancellation?.Cancel();
        CancelCaptureWatchdog("GPT retry");
        _readyJobId=null;
        DismissAnswerReady("GPT retry");
        _watcher.Disarm();
        await RecycleChatAsync();               // a broken page must not survive into the next attempt
    }

    /// <summary>
    /// Gives up on the active job: QUEUE status only, with a reason, then recycle and move on. Nothing
    /// about the application (status, timestamps, ApplyUrl, platform) is changed.
    /// </summary>
    async Task FailActiveJobAsync(JobTask job,string failureReason,string message) {
        _sendCancellation?.Cancel();
        _readyCancellation?.Cancel();
        CancelCaptureWatchdog("job failed");
        _readyJobId=null;
        DismissAnswerReady("job failed");
        _watcher.Disarm();

        job.Status="Failed";
        job.FailureReason=failureReason;
        _queue.AbandonActive();
        _activeJob=null;
        _activePrepared=null;
        _activePreparedText=null;
        _attempt=0;
        Storage.SaveTasks(_tasks);
        UpdateSummary();
        RefreshButtons();
        QueueStatus.Text=$"{job.Company} — {job.Title}: {message}";
        PerfLog.Line($"QUEUE job terminal Failed {job.JobId} reason={failureReason}");
        PerfLog.Snapshot("after job "+job.JobId);

        await RecycleChatAsync();
        if(_queue.IsRunning) await AdvanceQueueAsync();
    }

    // ---------- A6.6.13 "answer ready" notification ----------

    System.Threading.CancellationTokenSource? _readyCancellation;
    ReadyToast? _readyToast;

    /// <summary>
    /// Watches ChatGPT's control state until the answer looks finished, then notifies the user.
    /// It never reads the answer and never copies it: the user presses ChatGPT's own shortcut.
    /// </summary>
    async Task WatchForAnswerAsync(JobTask job,AppSettings settings,bool afterRejection=false) {
        _readyCancellation?.Cancel();
        var cancellation=new System.Threading.CancellationTokenSource();
        _readyCancellation=cancellation;

        var web=Chat;
        if(web is null) return;

        CompletionOutcome outcome;
        var waited=System.Diagnostics.Stopwatch.StartNew();
        using(PerfLog.Measure("await answer "+job.JobId)) {
            // The ambiguous "idle but generation never seen" state notifies the user and keeps watching:
            // a capture cancels this watch, and nothing at all becomes NoResponseStart.
            do outcome=await ChatCompletionWatcher.WaitForAnswerAsync(
                   new WebViewCompletionProbe(web),cancellation.Token,null,
                   () => OnUnconfirmedReady(job,settings,afterRejection));
            // After a rejected answer the user still has to ask for a correction: keep watching until it is generated.
            while(afterRejection && outcome==CompletionOutcome.ReadyUnconfirmed && !cancellation.IsCancellationRequested &&
                  _activeJob==job && waited.ElapsedMilliseconds<ChatCompletionWatcher.MaxWaitMs);
        }

        // The job may have been captured, stopped, skipped or recycled while we were waiting.
        if(cancellation.IsCancellationRequested || _activeJob!=job) return;

        if(outcome == CompletionOutcome.Ready)
        {
            PerfLog.Line("READY " + job.JobId);

            // Background capture: read the assistant response from WebView2. No focus steal, no
            // SendInput, no clipboard write. Manual Ctrl+Shift+; remains available if this fails.
            ShowAnswerReady(job, settings, copyPathAlreadyFocused: true);
            await TryBackgroundCaptureAsync(job);
        } else if(outcome==CompletionOutcome.NoResponseStart) {

            // A confirmed Send produced no generation, and no answer was captured, within the budget.
            await HandleGptFailureAsync(job,GptFailure.ResponseStartTimeout);

        } else if(outcome==CompletionOutcome.Stalled) {

            await HandleGptFailureAsync(job,GptFailure.ResponseStalled);

        } else if(outcome==CompletionOutcome.TimedOut) {

            QueueStatus.Text=$"{job.Company} — {job.Title}: no finished answer was detected after " +
                $"{ChatCompletionWatcher.MaxWaitMs/60000} minutes. Check ChatGPT, then press {ChatCompletionWatcher.ShortcutText} or click Copy on the answer's code block.";
            await HandleGptFailureAsync(job,GptFailure.ResponseCeiling);
        }
    }

    /// <summary>
    /// Brings the window forward, focuses the ChatGPT pane and presses ChatGPT's own copy shortcut —
    /// the existing behaviour, now shared by the confirmed Ready path and the ambiguous one. It reads
    /// nothing from the page; the answer still arrives only through the clipboard.
    /// </summary>
    /// <returns>True when the copy shortcut was dispatched (not when capture has landed).</returns>
    async Task<bool> RequestCopyAsync(JobTask job) {
        var gen = _browserGen.Current;
        var ct = _browserOpsCts?.Token ?? CancellationToken.None;

        RememberPreviousWorkWindow();

        if (AiWorkspaceView.Visibility != Visibility.Visible)
            NavigateTo("AiWorkspace");

        GlobalHotkey.BringToFront(this);

        // Allow Windows to finish foreground activation.
        try { await Task.Delay(450, ct); } catch (OperationCanceledException) {
            PerfLog.Line($"KEY aborted (browser recycled) {job.JobId}");
            return false;
        }

        if (!CopyFocusPolicy.MayTouchBrowser(gen, _browserGen.Current, ct.IsCancellationRequested, _chatView is not null)) {
            PerfLog.Line($"KEY aborted (stale browser gen) {job.JobId}");
            return false;
        }

        await FocusChatPaneForInputAsync(gen, ct);

        if (!CopyFocusPolicy.MayTouchBrowser(gen, _browserGen.Current, ct.IsCancellationRequested, _chatView is not null)) {
            PerfLog.Line($"KEY aborted after focus (stale browser) {job.JobId}");
            return false;
        }

        var ourHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var isForeground = IsResumeBuilderForeground(ourHwnd);
        PerfLog.Line($"KEY pre-send {job.JobId} foreground={isForeground} " +
                     $"IsFocused={_chatView?.IsFocused} IsKeyboardFocusWithin={_chatView?.IsKeyboardFocusWithin}");

        if (!isForeground) {
            // Do not SendInput into another app. Keep the job armed and retry when we regain focus.
            PerfLog.Line("KEY NOT SENT - Resume Builder is not the foreground window " + job.JobId);
            CaptureStatus.Text =
                $"ChatGPT finished — bring Resume Builder to the front (or press {GlobalHotkey.DisplayText}), " +
                $"then press {ChatCompletionWatcher.ShortcutText} or click Copy on the answer. " +
                "Auto-copy will retry briefly while this window is in the background.";
            _ = RetryCopyWhenForegroundAsync(job, gen, ct);
            return false;
        }

        return await DispatchCopyShortcutAsync(job, gen, ct);
    }

    /// <summary>True when our top-level window or one of its descendants (e.g. WebView2) owns the foreground.</summary>
    bool IsResumeBuilderForeground(IntPtr ourHwnd) {
        if (GlobalHotkey.IsForegroundOwnedBy(ourHwnd)) return true;
        // Fallback: same process (covers rare hosted HWNDs that are not under the WPF root).
        return GlobalHotkey.IsForegroundOwnedByProcess(Environment.ProcessId);
    }

    async Task<bool> DispatchCopyShortcutAsync(JobTask job, int gen, CancellationToken ct) {
        if (!CopyFocusPolicy.MayTouchBrowser(gen, _browserGen.Current, ct.IsCancellationRequested, _chatView is not null))
            return false;
        // Capture already landed — do not press the shortcut again or touch a recycling browser.
        if (!_watcher.IsArmed || _activeJob != job) {
            PerfLog.Line($"KEY skipped — capture no longer waiting {job.JobId}");
            return false;
        }

        var seqBefore = _watcher.ArmedSequence;
        PerfLog.Line($"KEY {KeyboardSimulator.CopyShortcutText} {job.JobId}");

        try {
            try { await Task.Delay(200, ct); } catch (OperationCanceledException) { return false; }
            KeyboardSimulator.SendCopyShortcut();
        } catch (Exception ex) {
            PerfLog.Line($"KEY {KeyboardSimulator.CopyShortcutText} FAILED {job.JobId}: {ex.Message}");
            return false;
        }

        // Brief settle: ChatGPT's copy is async to the clipboard. If capture already accepted the
        // answer and recycled the browser, exit quietly — do not treat that as a focus failure.
        try { await Task.Delay(400, ct); } catch (OperationCanceledException) {
            PerfLog.Line($"KEY clipboard check skipped (browser recycled after shortcut) {job.JobId}");
            return true;
        }

        if (!_watcher.IsArmed) {
            PerfLog.Line($"KEY clipboard check skipped (capture already accepted) {job.JobId}");
            return true;
        }

        var changed = _watcher.HasClipboardChangedSinceArm();
        PerfLog.Line($"KEY clipboard after shortcut {job.JobId} changed={changed} " +
                     $"(armedSeq={seqBefore})");
        if (!changed) {
            CaptureStatus.Text =
                $"The copy shortcut was sent but the clipboard did not change. Press {ChatCompletionWatcher.ShortcutText} " +
                "yourself, or click Copy on the answer's json code block.";
        }
        return true;
    }

    /// <summary>
    /// Bounded wait for Resume Builder to own the foreground, then one copy retry — never an
    /// unbounded focus-stealing loop, and never a re-send of the ChatGPT prompt.
    /// </summary>
    async Task RetryCopyWhenForegroundAsync(JobTask job, int gen, CancellationToken ct) {
        var deadline = DateTime.UtcNow + CopyFocusPolicy.ForegroundRetryBudget;
        var ourHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;

        while (DateTime.UtcNow < deadline) {
            if (ct.IsCancellationRequested || !_browserGen.IsCurrent(gen)) return;
            if (_activeJob != job || job.Status != "Processing" || !_watcher.IsArmed) return;
            if (_watcher.HasClipboardChangedSinceArm()) return; // manual or other path already copied

            if (IsResumeBuilderForeground(ourHwnd)) {
                PerfLog.Line($"KEY foreground recovered — retrying copy {job.JobId}");
                await FocusChatPaneForInputAsync(gen, ct);
                await DispatchCopyShortcutAsync(job, gen, ct);
                return;
            }

            try { await Task.Delay(250, ct); } catch (OperationCanceledException) { return; }
        }

        PerfLog.Line($"KEY foreground retry exhausted {job.JobId} — waiting for manual Copy");
    }

    /// <summary>
    /// Establishes keyboard focus in the ChatGPT WebView2. Generation-scoped: a recycle cancels the
    /// token and bumps the generation so this exits without a misleading FAILED log.
    /// </summary>
    async Task FocusChatPaneForInputAsync(int generation, CancellationToken ct) {
        try {
            if (!CopyFocusPolicy.MayTouchBrowser(generation, _browserGen.Current, ct.IsCancellationRequested, _chatView is not null)) {
                PerfLog.Line($"FocusChatPane skipped — browser gen {generation} no longer live (current={_browserGen.Current})");
                return;
            }

            var web = _chatView;
            if (web is null) {
                PerfLog.Line($"FocusChatPane skipped — no live view (gen={generation})");
                return;
            }

            web.Visibility = Visibility.Visible;
            web.Focusable = true;

            var wpfFocus = web.Focus();
            var keyboardFocus = System.Windows.Input.Keyboard.Focus(web) != null;

            try {
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Input);
                await Task.Delay(100, ct);
            } catch (OperationCanceledException) {
                PerfLog.Line($"FocusChatPane skipped — cancelled during yield (gen={generation})");
                return;
            }

            if (!CopyFocusPolicy.MayTouchBrowser(generation, _browserGen.Current, ct.IsCancellationRequested, _chatView is not null)
                || !ReferenceEquals(_chatView, web)) {
                PerfLog.Line($"FocusChatPane skipped — browser replaced during focus (gen={generation})");
                return;
            }

            wpfFocus = web.Focus() || wpfFocus;
            keyboardFocus = System.Windows.Input.Keyboard.Focus(web) != null || keyboardFocus;
            try { await Task.Delay(50, ct); } catch (OperationCanceledException) {
                PerfLog.Line($"FocusChatPane skipped — cancelled after focus (gen={generation})");
                return;
            }

            PerfLog.Line(
                $"FocusChatPane WpfFocus={wpfFocus} " +
                $"KeyboardFocus={keyboardFocus} " +
                $"IsFocused={web.IsFocused} " +
                $"IsKeyboardFocusWithin={web.IsKeyboardFocusWithin} gen={generation}");
        } catch (OperationCanceledException) {
            PerfLog.Line($"FocusChatPane skipped — cancelled (gen={generation})");
        } catch (Exception ex) {
            PerfLog.Line("FocusChatPane ERROR: " + ex.Message);
        }
    }

    /// <summary>
    /// The ambiguous state: the page looks idle but generation was never observed, which may be an
    /// answer that finished before the first poll. It is NOT confirmed: no Ready, no capture watchdog,
    /// no queue advance. It does get ONE copy opportunity — the same keystroke the confirmed path
    /// sends — so a fast answer can be captured instead of being re-sent at the 180 s budget. The
    /// watch continues: a capture cancels it, and silence still ends as NoResponseStart.
    /// Every existing capture rule still applies (prompt echo, non-profile, wrong or late job).
    /// </summary>
    void OnUnconfirmedReady(JobTask job,AppSettings settings,bool afterRejection) {
        if(_activeJob!=job) return;
        PerfLog.Line($"READY {job.JobId} (unconfirmed: generation not observed; waiting for confirmed Ready — no scrape yet)");
        if(!afterRejection) ShowAnswerReady(job,settings, copyPathAlreadyFocused: true);
        // Do not read the page or send a copy shortcut here — the answer may still be incomplete.
    }

    void ShowAnswerReady(JobTask job,AppSettings settings, bool copyPathAlreadyFocused = false) {
        QueueStatus.Text=$"✓ Answer ready — {job.Company} — {job.Title}. Capturing in the background…";

        if(settings.ReadySound) System.Media.SystemSounds.Asterisk.Play();

        // Automatic path must not steal focus or flash the taskbar into the user's face.
        // Manual toast/hotkey still available as recovery if background capture fails.
        if (CopyFocusPolicy.ShouldFocusOnAnswerReady(copyPathAlreadyFocused, _watcher.IsArmed, _activeJob == job)
            && IsActive)
            FocusChatPane();

        if(!settings.ReadyToast) return;
        CloseReadyToast();
        var toastInstruction =
            $"Background capture is running. If it fails, press {GlobalHotkey.DisplayText}, then {ChatCompletionWatcher.ShortcutText} — or click Copy on the answer.";
        var toast=new ReadyToast("✓ Answer ready",$"{job.Company} — {job.Title}",toastInstruction);
        toast.Clicked+=() => { BringToFrontForCopy(); toast.Close(); };
        toast.Closed+=(_,_) => { if(ReferenceEquals(_readyToast,toast)) _readyToast=null; };
        _readyToast=toast;
        toast.Show();
    }

    /// <summary>
    /// After confirmed Ready: read the last assistant JSON payload from WebView2 without focus or clipboard.
    /// </summary>
    async Task TryBackgroundCaptureAsync(JobTask job) {
        var gen = _browserGen.Current;
        var ct = _browserOpsCts?.Token ?? CancellationToken.None;

        if (!CopyFocusPolicy.MayTouchBrowser(gen, _browserGen.Current, ct.IsCancellationRequested, _chatView is not null)
            || Chat is null) {
            PerfLog.Line($"CAPTURE background aborted — no live browser {job.JobId}");
            ArmManualCaptureFallback(job, "ChatGPT browser was recycled before the answer could be read.");
            return;
        }

        CaptureStatus.Text = $"{job.Company} — {job.Title}: reading the ChatGPT answer in the background…";
        PerfLog.Line($"CAPTURE background begin {job.JobId} gen={gen} attempt={_attempt}");

        ChatReadResult read;
        try {
            using (PerfLog.Measure("background capture " + job.JobId))
                read = await ChatResponseReader.ReadStableAsync(Chat, ct);
        } catch (OperationCanceledException) {
            PerfLog.Line($"CAPTURE background cancelled {job.JobId}");
            return;
        } catch (Exception ex) {
            PerfLog.Line($"CAPTURE background error {job.JobId}: {ex.GetType().Name}");
            ArmManualCaptureFallback(job, "Background read failed (" + ex.GetType().Name + ").");
            return;
        }

        if (!CopyFocusPolicy.MayTouchBrowser(gen, _browserGen.Current, ct.IsCancellationRequested, _chatView is not null)
            || _activeJob != job || job.Status != "Processing") {
            PerfLog.Line($"CAPTURE background discarded — stale after read {job.JobId}");
            return;
        }

        PerfLog.Line($"CAPTURE background status={read.Status} assistants={read.AssistantCount} " +
                     $"chars={read.Text.Length} detail={read.Detail} {job.JobId}");

        if (!read.Success || !ResultCapture.ShouldCapture(read.Text)) {
            ArmManualCaptureFallback(job,
                read.Status == ChatReadStatus.Ambiguous
                    ? "More than one JSON block was found — refused to guess. Use manual Copy on the resume JSON."
                    : "Background capture could not find a finished resume JSON on the page.");
            return;
        }

        await AcceptCapturedTextAsync(read.Text, job, source: "background");
    }

    void ArmManualCaptureFallback(JobTask job, string reason) {
        CaptureStatus.Text =
            $"{job.Company} — {job.Title}: {reason} " +
            $"Optional: press {GlobalHotkey.DisplayText}, then {ChatCompletionWatcher.ShortcutText}, or click Copy on the json code block.";
        QueueStatus.Text =
            $"Background capture pending manual fallback — {job.Company} — {job.Title}.";
        PerfLog.Line($"CAPTURE manual fallback armed {job.JobId}");
        if (_watcher.IsArmed)
            _ = RunCaptureWatchdogAsync(job);
    }

    /// <summary>The user clicked the notification or pressed Ctrl+Shift+': bring ResumeBuilder forward.</summary>
    void BringToFrontForCopy() {
        GlobalHotkey.BringToFront(this);
        FocusChatPane();
    }

    /// <summary>
    /// Registers Ctrl+Shift+' system-wide. It only brings this window forward and focuses the ChatGPT
    /// pane; the user's own Ctrl+Shift+; still does the copy.
    /// </summary>
    void RegisterFocusHotkey() {
        if(!Storage.LoadSettings().FocusHotkey) return;
        _hotkey.Pressed+=() => {
            PerfLog.Line("HOTKEY focus");
            BringToFrontForCopy();
            CloseReadyToast();                               // the user has responded to it
        };
        if(_hotkey.Register(this)) {
            PerfLog.Line("HOTKEY registered "+GlobalHotkey.DisplayText);
        } else {
            PerfLog.Line($"HOTKEY not registered (Win32 error {_hotkey.LastError})");
            ImportMessage.Text=$"{GlobalHotkey.DisplayText} could not be registered — another app is probably using it. " +
                "Click the notification instead.";
        }
    }

    void FocusChatPane() {
        // Sync entry for toast / hotkey — generation-scoped so a recycle cancels it cleanly.
        var gen = _browserGen.Current;
        var ct = _browserOpsCts?.Token ?? CancellationToken.None;
        _ = FocusChatPaneForInputAsync(gen, ct);
    }

    void CloseReadyToast() {
        var toast=_readyToast;
        _readyToast=null;
        try { toast?.Close(); } catch { }
    }

    /// <summary>Ends any pending "answer ready" watch and capture watchdog, and removes the notification.</summary>
    void DismissAnswerReady(string reason) {
        _readyCancellation?.Cancel();
        CancelCaptureWatchdog(reason);
        CloseReadyToast();
    }

    // ---------- A6.6.13 post-generation capture watchdog ----------

    void CancelCaptureWatchdog(string reason) {
        var cancelled=_captureWatchdog.Cancel();
        if(cancelled is not null) PerfLog.Line($"CAPTURE watchdog cancelled {cancelled} ({reason})");
    }

    /// <summary>
    /// Started only when ChatGPT has confirmably finished. If no valid capture arrives in time the job is
    /// failed as CaptureTimeout and the queue moves on; the job is never re-sent automatically.
    /// </summary>
    async Task RunCaptureWatchdogAsync(JobTask job) {
        var seconds=(int)_captureWatchdog.Timeout.TotalSeconds;
        _readyJobId=job.JobId;
        PerfLog.Line($"CAPTURE watchdog started {job.JobId} {seconds}s");

        if(await _captureWatchdog.RunAsync(job.JobId)!=WatchdogResult.TimedOut) return;

        // Only the job that is still active and still waiting can time out.
        if(_activeJob!=job || job.Status!="Processing" ||
           !string.Equals(_queue.ActiveJobId,job.JobId,StringComparison.OrdinalIgnoreCase)) {
            PerfLog.Line($"CAPTURE watchdog expired for {job.JobId}, which is no longer active — ignored");
            return;
        }
        await FailOnCaptureTimeoutAsync(job,seconds);
    }

    async Task FailOnCaptureTimeoutAsync(JobTask job,int seconds) {
        PerfLog.Line($"CAPTURE TIMEOUT {job.JobId} after {seconds}s");
        PerfLog.Line(CaptureWatchdog.TimeoutMessage);
        // v1: ChatGPT may well have answered — only the Copy/capture failed. The job is failed and the
        // queue moves on, but the request is NOT re-sent.
        PerfLog.Line(GptAttempts.CaptureTimeoutLog(job.JobId));

        // Nothing more is accepted for this job.
        _watcher.Disarm();
        _readyJobId=null;
        DismissAnswerReady("capture timed out");

        // Whatever profile-like text is on the clipboard now is most likely this job's answer. Remember it,
        // so a late event carrying it can never be attributed to the next job.
        var onClipboard=ClipboardService.TryGetText();
        var quarantine=ResultCapture.ShouldCapture(onClipboard) ? onClipboard : null;
        if(quarantine is not null) PerfLog.Line($"CAPTURE clipboard answer quarantined for timed-out job {job.JobId}");
        _queue.OnCaptureTimedOut(quarantine);

        job.Status="Failed";
        job.FailureReason=CaptureWatchdog.FailureReason;
        _activeJob=null;
        _activePrepared=null;
        _activePreparedText=null;
        _attempt=0;
        Storage.SaveTasks(_tasks);
        UpdateSummary();
        RefreshButtons();
        CaptureStatus.Text=$"{job.Company} — {job.Title}: {CaptureWatchdog.TimeoutMessage} " +
            "The job is marked Failed (CaptureTimeout); Retry Failed re-queues it.";
        PerfLog.Line($"QUEUE job terminal Failed {job.JobId} reason={CaptureWatchdog.FailureReason}");
        PerfLog.Snapshot("after job "+job.JobId);

        // Recycle exactly as after a completed job, then move on. The job is not re-sent.
        await RecycleChatAsync();
        if(_queue.IsRunning) await AdvanceQueueAsync();
    }

    /// <summary>
    /// Navigates to a brand-new ChatGPT conversation and waits until the page is actually usable.
    /// NavigationCompleted alone is not enough: after a recycle it can fire in a few milliseconds
    /// while the SPA still has no composer. Cookies/session live in the WebView2 user-data folder.
    /// </summary>
    async Task<bool> NavigateFreshChatAsync() {
        var web=Chat;
        if(web is null) {
            PerfLog.Line("NAV fresh-chat aborted: CoreWebView2 is null");
            return false;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var loaded=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(object? _,CoreWebView2NavigationCompletedEventArgs e) {
            PerfLog.Line($"NAV NavigationCompleted success={e.IsSuccess} " +
                         $"status={(int)e.HttpStatusCode} after {sw.ElapsedMilliseconds}ms");
            loaded.TrySetResult(e.IsSuccess);
        }
        web.NavigationCompleted+=OnCompleted;
        try {
            PerfLog.Line("NAV Navigate → chatgpt.com/ (fresh chat)");
            web.Navigate(ChatComposer.ChatUrl);
            var finished=await Task.WhenAny(loaded.Task,Task.Delay(TimeSpan.FromSeconds(20)));
            if(finished!=loaded.Task) { PerfLog.Line("WARN navigate fresh chat timed out"); return false; }
            if(!await loaded.Task) {
                PerfLog.Line("WARN navigate fresh chat NavigationCompleted reported failure");
                return false;
            }

            // SPA readiness: wait for the composer, not just the document load event.
            var composerReady = await ChatComposer.WaitForComposerAsync(web);
            PerfLog.Line($"NAV composer ready={composerReady} after {sw.ElapsedMilliseconds}ms total");
            if(!composerReady) {
                PerfLog.Line("WARN navigate fresh chat: composer never appeared");
                return false;
            }
            return true;
        } catch(Exception ex) {
            PerfLog.Line("WARN navigate fresh chat failed: "+ex.GetType().Name+": "+ex.Message);
            return false;
        } finally {
            web.NavigationCompleted-=OnCompleted;
        }
    }

    /// <summary>
    /// An automation step could not complete. Nothing was rejected, so the job stays Processing and
    /// the capture stays armed; the run simply waits for the one manual action named in the message.
    /// </summary>
    void PauseForManualAction(string instruction) {
        _queue.PauseForManualAction();
        QueueStatus.Text="Manual action needed — "+instruction;
        RefreshButtons();
    }

    void ProcessSelected_Click(object sender,RoutedEventArgs e) {
        if(_queue.IsRunning) return;                       // the queue owns the active job while it runs
        if(TaskList.SelectedItem is not JobTask job) return;
        // Explicit single-job run: Failed is allowed (manual retry of one job). Completed is not.
        if(job.Status == "Completed" || job.Status == "Processing") return;
        if(job.Status == "Failed") {
            job.Status = "Queued";
            job.FailureReason = "";
            Storage.SaveTasks(_tasks);
            PerfLog.Line($"QUEUE manual retry selected {job.JobId}");
        }
        _queue.BeginJob(job.JobId);                        // strike counting applies to manual runs too
        _ = RunJobAsync(job);
    }

    // ---------- A6.6.12 sequential queue ----------

    async void StartQueue_Click(object sender,RoutedEventArgs e) {
        var count=_queue.Start(_tasks);
        if(count==0) {
            QueueStatus.Text = _queue.IsRunning
                ? "Queue is already running."
                : "Nothing to run — no queued jobs.";
            RefreshButtons();
            return;
        }
        PerfLog.Line("QUEUE start, "+count+" job(s)");
        PerfLog.Snapshot("queue start");
        QueueStatus.Text=$"Queue started — {count} job(s).";
        RefreshButtons();
        NavigateTo("AiWorkspace");
        await AdvanceQueueAsync();
    }

    void PauseQueue_Click(object sender,RoutedEventArgs e) {
        if(_queue.State==QueueState.Paused) {
            _queue.Resume();
            QueueStatus.Text="Queue resumed.";
            RefreshButtons();
            if(_queue.ActiveJobId is null) _ = AdvanceQueueAsync();
            // An answer that was already finished before the pause gets a fresh bounded wait.
            else if(_activeJob is { Status: "Processing" } job && _watcher.IsArmed &&
                    string.Equals(_readyJobId,job.JobId,StringComparison.OrdinalIgnoreCase))
                _ = RunCaptureWatchdogAsync(job);
        } else {
            _queue.Pause();
            CancelCaptureWatchdog("queue paused");       // nothing fails while the user has paused
            QueueStatus.Text="Queue paused — the current job finishes, then the run stops advancing.";
            RefreshButtons();
        }
    }

    void StopQueue_Click(object sender,RoutedEventArgs e) {
        var inFlight=_queue.Stop();
        DismissAnswerReady("queue stopped");
        _sendCancellation?.Cancel();
        _watcher.Disarm();
        // The in-flight job never finished, so it goes back in the queue rather than being stranded.
        if(inFlight is not null) {
            var job=_tasks.FirstOrDefault(t=>t.JobId.Equals(inFlight,StringComparison.OrdinalIgnoreCase));
            if(job is not null && job.Status=="Processing") job.Status="Queued";
        }
        _activeJob=null;
        Storage.SaveTasks(_tasks);
        UpdateSummary();
        RefreshButtons();
        QueueStatus.Text="Queue stopped."+(inFlight is null ? "" : " The job in progress was put back in the queue.");
        _ = ReleaseChatAsync();
    }

    async void SkipJob_Click(object sender,RoutedEventArgs e) {
        var skipped=_queue.SkipActive();
        if(skipped is null) return;
        DismissAnswerReady("job skipped");
        var job=_tasks.FirstOrDefault(t=>t.JobId.Equals(skipped,StringComparison.OrdinalIgnoreCase));
        if(job is not null) job.Status="Failed";
        _watcher.Disarm();
        _activeJob=null;
        Storage.SaveTasks(_tasks);
        UpdateSummary();
        QueueStatus.Text=$"Skipped {job?.Company} — {job?.Title}. It stays retryable with Retry Failed.";
        await AdvanceQueueAsync();
    }

    /// <summary>Moves to the next queued job. A job that cannot be prepared is failed and skipped.</summary>
    async Task AdvanceQueueAsync() {
        // Depth tracks nested fail→advance while an outer AdvanceQueue awaits RunJobAsync.
        // Start() already refuses a second run while Running/Paused.
        _advanceDepth++;
        try {
            if(_queue.State==QueueState.Paused) { QueueStatus.Text="Queue paused."; RefreshButtons(); return; }

            while(true) {
                var next=_queue.Next();
                if(next is null) {
                    _activeJob=null;
                    DismissAnswerReady("queue finished");
                    _watcher.Disarm();
                    QueueStatus.Text = _queue.State==QueueState.Finished ? "Queue finished." : "Queue stopped.";
                    PerfLog.Line($"QUEUE finished state={_queue.State}");
                    PerfLog.Snapshot(_queue.State==QueueState.Finished ? "queue finished" : "queue stopped");
                    Storage.SaveTasks(_tasks);
                    UpdateSummary();
                    RefreshButtons();
                    await ReleaseChatAsync();
                    return;
                }

                if (next.Status != "Queued") {
                    // Defensive: Next() should never return a non-Queued job; never process Failed again.
                    PerfLog.Line($"QUEUE skip unexpected status {next.JobId} status={next.Status}");
                    _queue.AbandonActive();
                    continue;
                }

                QueueStatus.Text=$"Queue {_queue.Position} of {_queue.Total} — {next.Company} — {next.Title}";
                PerfLog.Line($"QUEUE advance -> {next.JobId} ({next.Company}) status={next.Status}");
                if(await RunJobAsync(next)) return;            // now waiting for this job's answer

                // Preparation failed: fail this one and keep going rather than stalling the run.
                PerfLog.Line($"QUEUE prepare-failed {next.JobId} — marking Failed and continuing");
                next.Status="Failed";
                next.FailureReason = "PrepareFailed";
                _queue.AbandonActive();
                Storage.SaveTasks(_tasks);
                UpdateSummary();
                RefreshButtons();
            }
        } finally {
            _advanceDepth = Math.Max(0, _advanceDepth - 1);
        }
    }

    // ---------- armed result capture ----------

    async void OnClipboardTextCaptured(string text) {
        // Manual fallback only — automatic capture does not write the clipboard.
        if(!ResultCapture.ShouldCapture(text)) return;
        var job=_activeJob;
        if(job is null) return;
        await AcceptCapturedTextAsync(text, job, source: "clipboard");
    }

    /// <summary>
    /// Shared accept path for background WebView reads and manual clipboard copies.
    /// </summary>
    async Task AcceptCapturedTextAsync(string text, JobTask job, string source) {
        switch(_queue.Classify(text,_activePreparedText)) {
            case CaptureDecision.NoActiveJob:
                return;
            case CaptureDecision.Duplicate:
                CaptureStatus.Text="That is an answer already captured for an earlier job — copy the new response.";
                return;
            case CaptureDecision.LateResponse:
                CaptureStatus.Text="That answer belongs to an earlier job that timed out, so it was not used — copy the new response.";
                PerfLog.Line("REFUSED late response from a timed-out job while "+job.JobId+" is active");
                return;
            case CaptureDecision.PromptEcho:
                CaptureStatus.Text="That text is part of your own prompt, not ChatGPT's answer. " +
                    $"Make sure the answer contains a json code block" +
                    (source == "clipboard" ? $", then press {ChatCompletionWatcher.ShortcutText} again." : ".");
                PerfLog.Line("REFUSED prompt echo for "+job.JobId+" via "+source);
                return;
        }
        if(!string.Equals(_queue.ActiveJobId,job.JobId,StringComparison.OrdinalIgnoreCase)) return;
        if(_activeJob!=job || job.Status!="Processing") return;

        PerfLog.Line("CAPTURE received "+job.JobId+" via "+source);
        _readyJobId=null;
        _readyCancellation?.Cancel();
        DismissAnswerReady("capture received");

        CapturedResult result;
        using(PerfLog.Measure("capture+normalize+validate+save")) result=ResultCapture.Accept(text,job.JobId);

        if(result.Saved) {
            _watcher.Disarm();
            _queue.OnCaptureSucceeded(text);
            job.Status="Completed";
            _activePrepared=null;
            _attempt=0;
            CaptureStatus.Text=result.Message+(result.Report is not null && result.Report.Changed
                ? Environment.NewLine+result.Report.Describe() : "");
            Storage.SaveTasks(_tasks);
            UpdateSummary();
            RefreshButtons();
            _activeJob=null;

            await GenerateDocumentsAsync(job,result.TargetPath);
            PerfLog.Snapshot("after job "+job.JobId);

            await RecycleChatAsync();

            if(_queue.TryAutoResume()) QueueStatus.Text="Manual action completed — resuming the queue.";

            if(_queue.IsRunning) await AdvanceQueueAsync();
            return;
        }

        CaptureStatus.Text=result.Message;
        await HandleGptFailureAsync(job,GptFailure.InvalidOutput);
    }

    // ---------- document generation ----------

    void GenerateDocuments_Click(object sender,RoutedEventArgs e) {
        if(TaskList.SelectedItem is not JobTask job) return;
        _ = GenerateDocumentsAsync(job,ResultCapture.TargetPathFor(job.JobId));
    }

    /// <summary>
    /// Generates the enabled documents from an already-validated profile file. A document failure is
    /// reported on its own line and never changes the job's Completed state or the saved JSON.
    /// </summary>
    public async Task GenerateDocumentsAsync(JobTask job,string profilePath) {
        var settings=Storage.LoadSettings();
        DocumentStatus.Text=$"Generating documents for {job.Company} — {job.Title}…";
        try {
            GenerationResult generation;
            // PDFsharp/MigraDoc renders off the UI thread; no browser is involved any more.
            using(PerfLog.Measure("documents "+job.JobId))
            generation=await Task.Run(() => ResumeGenerator.Generate(
                job.Company,job.Title,profilePath,settings,ProfileResultStore.EffectiveStylePath(job.JobId),
                job.JobId,job.Link));

            DocumentStatus.Text=generation.Describe();

            // Tracking: a document exists, so the application is Ready to send. This is the only
            // status change Resume Builder makes on its own — everything after it is the user's.
            if(generation.DocxGenerated || generation.PdfGenerated)
                if(JobTracker.MarkResumeReady(job,generation.DocxPath ?? generation.PdfPath)) {
                    JobTracker.SaveTrackingData(_tasks);
                    RefreshDashboardIfOpen();
                }

            // Style system: style corrections are recorded next to the job, so a styling surprise can be
            // explained afterwards without re-running anything.
            if(generation.AnyFailure || generation.StyleWarnings.Count>0)
                ProfileResultStore.SaveDocGenLog(job.JobId,
                    generation.Describe()+(generation.StyleWarnings.Count==0?"":Environment.NewLine+Environment.NewLine+
                        "Style adjustments:"+Environment.NewLine+"  - "+string.Join(Environment.NewLine+"  - ",generation.StyleWarnings)));
        } catch(Exception ex) {
            DocumentStatus.Text="Documents not generated — "+ResumeGenerator.Explain(ex)+" The tailored JSON is saved.";
            ProfileResultStore.SaveDocGenLog(job.JobId,ex.ToString());
        }
    }

    /// <summary>Lets the Settings window reuse the same generation path after a manual save.</summary>
    public Task GenerateForJobAsync(string jobId) {
        var job=_tasks.FirstOrDefault(t=>t.JobId.Equals(jobId,StringComparison.OrdinalIgnoreCase));
        return job is null ? Task.CompletedTask : GenerateDocumentsAsync(job,ResultCapture.TargetPathFor(jobId));
    }
}



