using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using System.Runtime.InteropServices;

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
    SettingsWindow? _settings;
    JobTask? _activeJob;
    CoreWebView2Environment? _webEnvironment;
    Microsoft.Web.WebView2.Wpf.WebView2? _chatView;
    ChatHost? _chat;
    System.Threading.CancellationTokenSource? _sendCancellation;
    string? _activePreparedText;

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

            if (current == resumeBuilder)
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
            var current = GlobalHotkey.CurrentForegroundWindow();

            var resumeBuilder =
                new System.Windows.Interop.WindowInteropHelper(this).Handle;

            // Important:
            // if the user has already switched somewhere else manually,
            // do not steal focus from that new window.
            if (current != resumeBuilder)
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
        if (recovered > 0) Storage.SaveTasks(_tasks);

        _taskView = TaskViews.CreateView(_tasks, () => _showHistory);
        TaskList.ItemsSource = _taskView;
        foreach (var t in _tasks) WatchTask(t);
        _tasks.CollectionChanged += (_, e) => {
            if (e.NewItems is not null) foreach (JobTask t in e.NewItems) WatchTask(t);
            UpdateViewSwitch();
        };
        Loaded += MainWindow_Loaded;
        Closed += (_, _) => { DismissAnswerReady("app closed"); _hotkey.Dispose(); _watcher.Dispose(); _ = DisposeChatViewAsync(); };
        _watcher.TextCaptured += OnClipboardTextCaptured;
        UpdateSummary();
        UpdateViewSwitch();
        if (recovered > 0) QueueStatus.Text = $"Recovered {recovered} job(s) left in progress by the previous session.";
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
        try { await _chat.EnsureAsync(); }
        catch (Exception ex) {
            System.Windows.MessageBox.Show(
                "ChatGPT browser could not be initialized.\n\n" + ex.Message,
                "Resume Builder v1.0", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------- job browser ----------

    JobBrowserWindow? _jobBrowser;

    /// <summary>
    /// Opens the built-in job browser, or brings it forward if it is already open. It runs on its own
    /// WebView2 profile in its own window, so it is untouched by the ChatGPT browser's recycling and
    /// takes no part in the queue. Owning it means closing Resume Builder closes it too.
    /// </summary>
    void JobBrowser_Click(object sender,RoutedEventArgs e) {
        if(_jobBrowser is not null) {
            if(_jobBrowser.WindowState==WindowState.Minimized) _jobBrowser.WindowState=WindowState.Normal;
            _jobBrowser.Activate();
            return;
        }

        _jobBrowser=new JobBrowserWindow {
            Owner=this,
            ImportJob=ImportFromBrowser,
            JobExists = url => _tasks.Any(t =>
                JobUrls.Normalize(t.Link) ==
                JobUrls.Normalize(url))
        };
        _jobBrowser.Closed+=(_,_) => _jobBrowser=null;
        _jobBrowser.Show();
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
        }
        return outcome;
    }

    /// <summary>
    /// Rebuilds the Applications dashboard's cards, chart, pipeline and board after this window
    /// changed tracking data. Rows already update themselves through JobTask's change notification;
    /// the aggregates only through RefreshTracking. Nothing runs when Settings is not open.
    /// </summary>
    void RefreshDashboardIfOpen() {
        if(_settings is { IsLoaded: true }) _settings.RefreshTracking();
    }

    /// <summary>How long to wait for a browser process to actually exit before reporting a timeout.</summary>
    static readonly TimeSpan BrowserExitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Builds a new WebView2 on the shared user-data folder, so the sign-in carries over.</summary>
    async Task<int> CreateChatViewAsync() {
        Directory.CreateDirectory(Storage.WebViewUserDataFolder);

        _webEnvironment ??= await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null, userDataFolder: Storage.WebViewUserDataFolder);

        var view = new Microsoft.Web.WebView2.Wpf.WebView2();
        ChatHostPanel.Child = view;
        await view.EnsureCoreWebView2Async(_webEnvironment);
        view.CoreWebView2.Settings.AreDevToolsEnabled = false;
        view.Source = new Uri(ChatComposer.ChatUrl);
        _chatView = view;

        var pid = (int)view.CoreWebView2.BrowserProcessId;
        PerfLog.Line($"BROWSER created            pid={pid}");
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
        using (PerfLog.Measure("chat recycle")) await _chat.RecycleAsync();
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

    void Settings_Click(object sender,RoutedEventArgs e) {
        if(_settings is null || !_settings.IsLoaded) {
            _settings=new SettingsWindow();
            _settings.Owner=this;
            _settings.Show();
        } else _settings.Activate();
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
    }

    void UpdateSummary()=>SummaryText.Text=$"{_tasks.Count} task(s) • {_tasks.Count(x=>x.Status=="Queued")} queued • {_tasks.Count(x=>x.Status=="Completed")} completed";

    void TaskList_SelectionChanged(object sender,SelectionChangedEventArgs e)=>RefreshButtons();

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
        Storage.SaveTasks(_tasks);
        UpdateSummary();
        RefreshButtons();
        _settings?.RefreshInspector();
        TaskList.SelectedItem=job;
        TaskList.ScrollIntoView(job);

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
        if(Chat is null) return true;

        // A6.6.12 — every job starts a FRESH conversation. Previously the app only navigated when the
        // host was not chatgpt.com, so every job appended another 35 KB prompt and a long answer to
        // one page; an empty ChatGPT tab already costs ~700 MB, so that grew without bound. The
        // WebView2 user-data folder is untouched, so the signed-in session carries over.
        using(PerfLog.Measure("navigate fresh chat")) await NavigateFreshChatAsync();

        if(!settings.AutoFillComposer) return true;

        ComposerResult fill;
        using(PerfLog.Measure("composer fill"))
            fill=await ChatComposer.FillAsync(Chat,prepared.Text);
        ImportMessage.Text=$"Prepared {job.Company} — {job.Title}. "+fill.Message;
        if(!fill.Success || !settings.AutoSend) {
            if(!settings.AutoSend) QueueStatus.Text="Auto-Send is off — press Enter in ChatGPT to send the prompt.";
            else PauseForManualAction("The prompt could not be typed in automatically.");
            return true;
        }

        // A6.6.12 — click Send. Copying the answer stays manual by design.
        _sendCancellation?.Cancel();
        _sendCancellation=new CancellationTokenSource();
        SendResult send;
        using(PerfLog.Measure("auto-send"))
            send=await ChatSender.SendAsync(new WebViewChatProbe(Chat),_sendCancellation.Token);

        if(send.Success) {
            QueueStatus.Text=$"{job.Company} — {job.Title}: {send.Message}";
            _ = WatchForAnswerAsync(job,settings);     // A6.6.13: tell the user when it is ready
            
            // If the previous completed job temporarily brought ResumeBuilder
            // forward, return the user to the window they were working in now
            // that this new job has been successfully sent.
            RestorePreviousWorkWindow("next job auto-sent");
        }
        else if(send.Outcome!=SendOutcome.Cancelled) PauseForManualAction(send.Message);
        return true;
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
            do outcome=await ChatCompletionWatcher.WaitForAnswerAsync(new WebViewCompletionProbe(web),cancellation.Token);
            // After a rejected answer the user still has to ask for a correction: keep watching until it is generated.
            while(afterRejection && outcome==CompletionOutcome.ReadyUnconfirmed && !cancellation.IsCancellationRequested &&
                  _activeJob==job && waited.ElapsedMilliseconds<ChatCompletionWatcher.MaxWaitMs);
        }

        // The job may have been captured, stopped, skipped or recycled while we were waiting.
        if(cancellation.IsCancellationRequested || _activeJob!=job) return;

        if(outcome == CompletionOutcome.Ready)
        {
            PerfLog.Line("READY " + job.JobId);

            RememberPreviousWorkWindow();

            GlobalHotkey.BringToFront(this);

            // Allow Windows to finish foreground activation.
            await Task.Delay(450);

            FocusChatPane();

            // Allow WebView2 to acquire keyboard focus.
            await Task.Delay(150);

            if (_chatView?.CoreWebView2 != null)
            {
                await _chatView.CoreWebView2.ExecuteScriptAsync("""
                    (() => {
                        const el = document.activeElement;

                        if (el && typeof el.blur === 'function') {
                            el.blur();
                        }

                        document.body.tabIndex = -1;
                        document.body.focus();
                    })();
                """);

                await Task.Delay(150);
            }

            if (_chatView?.IsFocused == true)
            {
                PerfLog.Line("KEY Ctrl+Shift+I " + job.JobId);

                try
                {
                    await Task.Delay(350);
                    KeyboardSimulator.SendCtrlShiftI();
                }
                catch(Exception ex)
                {
                    PerfLog.Line("KEY Ctrl+Shift+I FAILED " +
                                job.JobId + ": " + ex.Message);
                }
            }
            else
            {
                PerfLog.Line(
                    "KEY NOT SENT - WebView has no keyboard focus " +
                    job.JobId);
            }

            ShowAnswerReady(job, settings);

            if(_watcher.IsArmed)
                _ = RunCaptureWatchdogAsync(job);
        } else if(outcome==CompletionOutcome.ReadyUnconfirmed) {

            PerfLog.Line($"READY {job.JobId} (generation not observed; no capture watchdog)");
            if(!afterRejection) ShowAnswerReady(job,settings);

        } else if(outcome==CompletionOutcome.TimedOut) {

            QueueStatus.Text=$"{job.Company} — {job.Title}: no finished answer was detected after " +
                $"{ChatCompletionWatcher.MaxWaitMs/60000} minutes. Check ChatGPT, then press {ChatCompletionWatcher.ShortcutText} or click Copy on the answer's code block.";
        }
    }

    void ShowAnswerReady(JobTask job,AppSettings settings) {
        var instruction=_hotkey.IsRegistered
            ? $"From any app: press {GlobalHotkey.DisplayText}, then {ChatCompletionWatcher.ShortcutText}."
            : $"Press {ChatCompletionWatcher.ShortcutText} to copy the answer's code block (or click that code block's Copy button).";
        QueueStatus.Text=$"✓ Answer ready — {job.Company} — {job.Title}. {instruction}";

        if(settings.ReadySound) System.Media.SystemSounds.Asterisk.Play();

        if(IsActive) FocusChatPane();                          // no focus stealing: only when already in front
        else if(settings.ReadyFlash) {
            WindowAttention.FlashUntilForeground(this);
        }

        if(!settings.ReadyToast) return;
        CloseReadyToast();
        var toastInstruction=_hotkey.IsRegistered
            ? $"Press {GlobalHotkey.DisplayText}, then {ChatCompletionWatcher.ShortcutText}.  (Or click here.)"
            : $"Click here, then press {ChatCompletionWatcher.ShortcutText} (or click Copy on the answer's code block).";
        var toast=new ReadyToast("✓ Answer ready",$"{job.Company} — {job.Title}",toastInstruction);
        toast.Clicked+=() => { BringToFrontForCopy(); toast.Close(); };
        toast.Closed+=(_,_) => { if(ReferenceEquals(_readyToast,toast)) _readyToast=null; };
        _readyToast=toast;
        toast.Show();
    }

    /// <summary>The user clicked the notification or pressed Ctrl+Shift+': bring ResumeBuilder forward.</summary>
    void BringToFrontForCopy() {
        // ??? have to confirm
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

    void FocusChatPane()
    {
        try
        {
            var web = _chatView;

            if (web == null)
            {
                PerfLog.Line("FocusChatPane FAILED - _chatView is null");
                return;
            }

            var wpfFocus = web.Focus();
            var keyboardFocus = System.Windows.Input.Keyboard.Focus(web);

            PerfLog.Line(
                $"FocusChatPane WpfFocus={wpfFocus} " +
                $"KeyboardFocus={(keyboardFocus != null)} " +
                $"IsFocused={web.IsFocused} " +
                $"IsKeyboardFocusWithin={web.IsKeyboardFocusWithin}");
        }
        catch(Exception ex)
        {
            PerfLog.Line("FocusChatPane ERROR: " + ex.Message);
        }
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
        _activePreparedText=null;
        Storage.SaveTasks(_tasks);
        UpdateSummary();
        RefreshButtons();
        CaptureStatus.Text=$"{job.Company} — {job.Title}: {CaptureWatchdog.TimeoutMessage} " +
            "The job is marked Failed (CaptureTimeout); Retry Failed re-queues it.";
        PerfLog.Snapshot("after job "+job.JobId);

        // Recycle exactly as after a completed job, then move on. The job is not re-sent.
        await RecycleChatAsync();
        if(_queue.IsRunning) await AdvanceQueueAsync();
    }

    /// <summary>
    /// Navigates to a brand-new ChatGPT conversation and waits for it to load. Cookies and the
    /// signed-in session live in the WebView2 user-data folder, so nothing is lost by navigating.
    /// </summary>
    async Task NavigateFreshChatAsync() {
        var web=Chat;
        if(web is null) return;

        var loaded=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(object? _,CoreWebView2NavigationCompletedEventArgs e)=>loaded.TrySetResult(e.IsSuccess);
        web.NavigationCompleted+=OnCompleted;
        try {
            web.Navigate(ChatComposer.ChatUrl);
            await Task.WhenAny(loaded.Task,Task.Delay(TimeSpan.FromSeconds(20)));
        } catch(Exception ex) {
            PerfLog.Line("WARN navigate fresh chat failed: "+ex.Message);
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
        _queue.BeginJob(job.JobId);                        // strike counting applies to manual runs too
        _ = RunJobAsync(job);
    }

    // ---------- A6.6.12 sequential queue ----------

    async void StartQueue_Click(object sender,RoutedEventArgs e) {
        var count=_queue.Start(_tasks);
        if(count==0) { QueueStatus.Text="Nothing to run — no queued jobs."; RefreshButtons(); return; }
        PerfLog.Line("QUEUE start, "+count+" job(s)");
        PerfLog.Snapshot("queue start");
        QueueStatus.Text=$"Queue started — {count} job(s).";
        RefreshButtons();
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
        if(_queue.State==QueueState.Paused) { QueueStatus.Text="Queue paused."; RefreshButtons(); return; }

        while(true) {
            var next=_queue.Next();
            if(next is null) {
                _activeJob=null;
                DismissAnswerReady("queue finished");
                _watcher.Disarm();
                QueueStatus.Text = _queue.State==QueueState.Finished ? "Queue finished." : "Queue stopped.";
                PerfLog.Snapshot(_queue.State==QueueState.Finished ? "queue finished" : "queue stopped");
                Storage.SaveTasks(_tasks);
                UpdateSummary();
                RefreshButtons();
                // Nothing is pending: give the browser back entirely. It is rebuilt on the next job.
                await ReleaseChatAsync();
                return;
            }

            QueueStatus.Text=$"Queue {_queue.Position} of {_queue.Total} — {next.Company} — {next.Title}";
            if(await RunJobAsync(next)) return;            // now waiting for this job's answer

            // Preparation failed: fail this one and keep going rather than stalling the run.
            next.Status="Failed";
            _queue.AbandonActive();
            Storage.SaveTasks(_tasks);
            UpdateSummary();
            RefreshButtons();
        }
    }

    // ---------- armed result capture ----------

    async void OnClipboardTextCaptured(string text) {
        if(!ResultCapture.ShouldCapture(text)) return;   // not a profile: ignored, never stored

        var job=_activeJob;
        if(job is null) return;                          // no active job: nothing to attribute it to

        switch(_queue.Classify(text,_activePreparedText)) {
            case CaptureDecision.NoActiveJob:
                return;
            case CaptureDecision.Duplicate:
                // A stale Copy of an answer already captured must never be written against another job.
                CaptureStatus.Text="That is an answer already captured for an earlier job — copy the new response.";
                return;
            case CaptureDecision.LateResponse:
                // A6.6.13 — the answer of a job whose capture timed out never belongs to the job active now.
                CaptureStatus.Text="That answer belongs to an earlier job that timed out, so it was not used — copy the new response.";
                PerfLog.Line("REFUSED late response from a timed-out job while "+job.JobId+" is active");
                return;
            case CaptureDecision.PromptEcho:
                // "Copy last code block" can pick up a code block from our own prompt if an answer has none.
                // That is not ChatGPT's answer: refuse it, count no strike, and keep waiting.
                CaptureStatus.Text="That copied text is part of your own prompt, not ChatGPT's answer. " +
                    $"Make sure the answer contains a json code block, then press {ChatCompletionWatcher.ShortcutText} again.";
                PerfLog.Line("REFUSED prompt echo for "+job.JobId);
                return;
        }
        if(!string.Equals(_queue.ActiveJobId,job.JobId,StringComparison.OrdinalIgnoreCase)) return;

        // The user has copied an answer for this job: the watchdog and the "ready" prompt are done.
        PerfLog.Line("CAPTURE received "+job.JobId);
        _readyJobId=null;
        DismissAnswerReady("capture received");

        CapturedResult result;
        using(PerfLog.Measure("capture+normalize+validate+save")) result=ResultCapture.Accept(text,job.JobId);

        if(result.Saved) {
            _watcher.Disarm();
            _queue.OnCaptureSucceeded(text);
            job.Status="Completed";
            CaptureStatus.Text=result.Message+(result.Report is not null && result.Report.Changed
                ? Environment.NewLine+result.Report.Describe() : "");
            Storage.SaveTasks(_tasks);
            UpdateSummary();
            RefreshButtons();
            _activeJob=null;

            // Documents are generated only after the JSON is saved, and only from that file.
            await GenerateDocumentsAsync(job,result.TargetPath);
            PerfLog.Snapshot("after job "+job.JobId);

            // A6.6.12 — the answer is captured, validated, saved and rendered, so nothing needs the
            // browser any more. Destroy it; the next job builds a fresh one.
            await RecycleChatAsync();

            // A manual rescue (the user pressed Enter themselves) resumes the run; an explicit Pause does not.
            if(_queue.TryAutoResume()) QueueStatus.Text="Manual action completed — resuming the queue.";

            if(_queue.IsRunning) await AdvanceQueueAsync();
            return;
        }

        // Rejected response: the first one only asks for another Copy; the job stays Processing.
        if(_queue.OnCaptureFailed()==FailureOutcome.RetryCopy) {
            CaptureStatus.Text=result.Message+Environment.NewLine+
                "The job is still in progress — ask the AI to return the corrected JSON and click Copy again.";
            // Watch for the corrected answer, so its Copy is bounded by the watchdog too.
            _ = WatchForAnswerAsync(job,Storage.LoadSettings(),afterRejection:true);
            return;
        }

        job.Status="Failed";
        _watcher.Disarm();
        _activeJob=null;
        Storage.SaveTasks(_tasks);
        UpdateSummary();
        RefreshButtons();
        CaptureStatus.Text=result.Message+Environment.NewLine+
            "Second attempt rejected — the job is marked Failed and the raw response was kept. Retry Failed re-queues it.";

        if(_queue.IsRunning) await AdvanceQueueAsync();
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



