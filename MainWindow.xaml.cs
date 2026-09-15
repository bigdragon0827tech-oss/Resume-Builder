using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace ResumeBuilder;

public partial class MainWindow : Window {
    readonly ObservableCollection<JobTask> _tasks = new(Storage.LoadTasks());
    readonly ClipboardWatcher _watcher = new();
    SettingsWindow? _settings;
    JobTask? _pendingJob;

    public MainWindow() {
        InitializeComponent();
        TaskList.ItemsSource = _tasks;
        Loaded += MainWindow_Loaded;
        Closed += (_, _) => _watcher.Dispose();
        _watcher.TextCaptured += OnClipboardTextCaptured;
        UpdateSummary();
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

            await ChatView.EnsureCoreWebView2Async(environment);
            ChatView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            ChatView.Source = new Uri("https://chatgpt.com/");
        }
        catch (Exception ex) {
            System.Windows.MessageBox.Show(
                "ChatGPT browser could not be initialized.\n\n" + ex.Message,
                "Resume Builder A6.6.8",
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

    void TaskList_SelectionChanged(object sender,SelectionChangedEventArgs e) {
        ProcessSelectedButton.IsEnabled = TaskList.SelectedItem is JobTask j && j.Status!="Processing";
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
        RetryFailedButton.IsEnabled=_tasks.Any(t=>t.Status=="Failed");
        ProcessSelectedButton.IsEnabled = TaskList.SelectedItem is JobTask j && j.Status!="Processing";
    }

    // ---------- prepare and send ----------

    async void ProcessSelected_Click(object sender,RoutedEventArgs e) {
        if(TaskList.SelectedItem is not JobTask job) return;
        var settings=Storage.LoadSettings();

        // Step 1 — preparation. This is the operation that can actually fail.
        PreparedRequest prepared;
        try {
            prepared=RequestPreparation.Prepare(job,settings);
        } catch(Exception ex) {
            ImportMessage.Text="Prepare failed: "+ex.Message;
            return;
        }

        // The prepared input is saved and visible before anything else is attempted.
        job.Status="Processing";
        _pendingJob=job;
        Storage.SaveTasks(_tasks);
        UpdateSummary();
        RefreshButtons();
        _settings?.RefreshInspector();

        // Step 2 — clipboard. A clipboard problem never marks the preparation as failed.
        var clip=ClipboardService.SetText(prepared.Text);
        ImportMessage.Text=$"Prepared {job.Company} — {job.Title} successfully. "+clip.Message;

        // Step 3 — arm the capture before the answer can possibly arrive.
        if(settings.AutoCaptureResult && _watcher.IsListening) {
            _watcher.Arm(prepared.Text);
            CaptureStatus.Text="Waiting for the AI answer — click Copy on the ChatGPT response and it will be captured automatically.";
        } else {
            _watcher.Disarm();
            CaptureStatus.Text = settings.AutoCaptureResult
                ? "Automatic capture is unavailable; paste the answer in Settings → Result."
                : "Automatic capture is turned off; paste the answer in Settings → Result.";
        }

        // Step 4 — type it into ChatGPT. Convenience only: failure leaves the clipboard fallback.
        if(ChatView.CoreWebView2 is null) return;
        if(!(ChatView.Source?.Host?.Contains("chatgpt.com",StringComparison.OrdinalIgnoreCase) ?? false))
            ChatView.CoreWebView2.Navigate(ChatComposer.ChatUrl);

        if(!settings.AutoFillComposer) return;
        var fill=await ChatComposer.FillAsync(ChatView.CoreWebView2,prepared.Text);
        ImportMessage.Text=$"Prepared {job.Company} — {job.Title}. "+fill.Message;
    }

    // ---------- armed result capture ----------

    void OnClipboardTextCaptured(string text) {
        if(!ResultCapture.ShouldCapture(text)) return;   // not a profile: ignored, never stored

        var jobId=_pendingJob?.JobId ?? RequestPreparation.Load()?.JobId;
        var result=ResultCapture.Accept(text,jobId);

        if(result.Saved) {
            _watcher.Disarm();
            if(_pendingJob is not null) _pendingJob.Status="Completed";
            CaptureStatus.Text=result.Message+(result.Report is not null && result.Report.Changed
                ? Environment.NewLine+result.Report.Describe() : "");
        } else {
            if(_pendingJob is not null) _pendingJob.Status="Failed";
            CaptureStatus.Text=result.Message+Environment.NewLine+
                "Still waiting — copy a corrected answer, or use Settings → Result.";
        }

        Storage.SaveTasks(_tasks);
        UpdateSummary();
        RefreshButtons();
        _pendingJob=result.Saved ? null : _pendingJob;
    }
}