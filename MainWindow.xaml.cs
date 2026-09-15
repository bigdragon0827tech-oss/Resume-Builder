using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace ResumeBuilder;

public partial class MainWindow : Window {
    readonly ObservableCollection<JobTask> _tasks = new(Storage.LoadTasks());
    readonly ClipboardWatcher _watcher = new();
    readonly QueueRunner _queue = new();
    SettingsWindow? _settings;
    JobTask? _activeJob;
    CoreWebView2Environment? _webEnvironment;

    public MainWindow() {
        InitializeComponent();

        // A job left Processing by a crash or a close would never be re-run; put it back in the queue.
        var recovered = QueueRunner.RecoverStaleProcessing(_tasks);
        if (recovered > 0) Storage.SaveTasks(_tasks);

        TaskList.ItemsSource = _tasks;
        Loaded += MainWindow_Loaded;
        Closed += (_, _) => _watcher.Dispose();
        _watcher.TextCaptured += OnClipboardTextCaptured;
        UpdateSummary();
        if (recovered > 0) QueueStatus.Text = $"Recovered {recovered} job(s) left in progress by the previous session.";
    }

    async void MainWindow_Loaded(object sender, RoutedEventArgs e) {
        _watcher.Attach(this);
        await InitializeChatGptAsync();
        RefreshInput();
    }

    async Task InitializeChatGptAsync() {
        try {
            var profileDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ResumeBuilder", "WebView2");
            Directory.CreateDirectory(profileDir);

            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: profileDir);

            _webEnvironment = environment;
            await ChatView.EnsureCoreWebView2Async(environment);
            ChatView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            ChatView.Source = new Uri("https://chatgpt.com/");
        }
        catch (Exception ex) {
            System.Windows.MessageBox.Show(
                "ChatGPT browser could not be initialized.\n\n" + ex.Message,
                "Resume Builder A6.6.10",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
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
        ImportMessage.Text = r.Errors.Count>0
            ? string.Join(Environment.NewLine,r.Errors)
            : r.FilesImported==0
                ? "No new input files."
                : $"Imported: {r.FilesImported} file(s) • New: {r.JobsQueued} • Existing: {r.JobsExisting}";
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

        PreparedRequest prepared;
        try {
            prepared=RequestPreparation.Prepare(job,settings);
        } catch(Exception ex) {
            ImportMessage.Text="Prepare failed: "+ex.Message;
            return false;
        }

        job.Status="Processing";
        _activeJob=job;
        Storage.SaveTasks(_tasks);
        UpdateSummary();
        RefreshButtons();
        _settings?.RefreshInspector();
        TaskList.SelectedItem=job;
        TaskList.ScrollIntoView(job);

        // Clipboard: a clipboard problem never marks the preparation as failed.
        var clip=ClipboardService.SetText(prepared.Text);
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
        if(ChatView.CoreWebView2 is not null) {
            if(!(ChatView.Source?.Host?.Contains("chatgpt.com",StringComparison.OrdinalIgnoreCase) ?? false))
                ChatView.CoreWebView2.Navigate(ChatComposer.ChatUrl);
            if(settings.AutoFillComposer) {
                var fill=await ChatComposer.FillAsync(ChatView.CoreWebView2,prepared.Text);
                ImportMessage.Text=$"Prepared {job.Company} — {job.Title}. "+fill.Message;
            }
        }
        return true;
    }

    void ProcessSelected_Click(object sender,RoutedEventArgs e) {
        if(_queue.IsRunning) return;                       // the queue owns the active job while it runs
        if(TaskList.SelectedItem is not JobTask job) return;
        _queue.BeginJob(job.JobId);                        // strike counting applies to manual runs too
        _ = RunJobAsync(job);
    }

    // ---------- A6.6.10 sequential queue ----------

    async void StartQueue_Click(object sender,RoutedEventArgs e) {
        var count=_queue.Start(_tasks);
        if(count==0) { QueueStatus.Text="Nothing to run — no queued jobs."; RefreshButtons(); return; }
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
        } else {
            _queue.Pause();
            QueueStatus.Text="Queue paused — the current job finishes, then the run stops advancing.";
            RefreshButtons();
        }
    }

    void StopQueue_Click(object sender,RoutedEventArgs e) {
        var inFlight=_queue.Stop();
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
    }

    async void SkipJob_Click(object sender,RoutedEventArgs e) {
        var skipped=_queue.SkipActive();
        if(skipped is null) return;
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
                _watcher.Disarm();
                QueueStatus.Text = _queue.State==QueueState.Finished ? "Queue finished." : "Queue stopped.";
                Storage.SaveTasks(_tasks);
                UpdateSummary();
                RefreshButtons();
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

        // A stale Copy of an answer already captured must never be written against another job.
        if(_queue.IsDuplicate(text)) {
            CaptureStatus.Text="That is an answer already captured for an earlier job — copy the new response.";
            return;
        }

        var result=ResultCapture.Accept(text,job.JobId);

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

            if(_queue.IsRunning) await AdvanceQueueAsync();
            return;
        }

        // Rejected response: the first one only asks for another Copy; the job stays Processing.
        if(_queue.OnCaptureFailed()==FailureOutcome.RetryCopy) {
            CaptureStatus.Text=result.Message+Environment.NewLine+
                "The job is still in progress — ask the AI to return the corrected JSON and click Copy again.";
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
            var generation=await ResumeGenerator.GenerateAsync(
                job.Company,job.Title,profilePath,settings,
                new System.Windows.Interop.WindowInteropHelper(this).Handle,_webEnvironment);

            DocumentStatus.Text=generation.Describe();
            if(generation.AnyFailure) ProfileResultStore.SaveDocGenLog(job.JobId,generation.Describe());
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
