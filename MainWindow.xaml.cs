using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace ResumeBuilder;

public partial class MainWindow : Window {
    readonly ObservableCollection<JobTask> _tasks = new(Storage.LoadTasks());
    SettingsWindow? _settings;

    public MainWindow() {
        InitializeComponent();
        TaskList.ItemsSource = _tasks;
        Loaded += MainWindow_Loaded;
        UpdateSummary();
    }

    async void MainWindow_Loaded(object sender, RoutedEventArgs e) {
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
                "Resume Builder A6.6.7",
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
        ImportMessage.Text = r.Errors.Count>0
            ? string.Join(Environment.NewLine,r.Errors)
            : r.FilesImported==0
                ? "No new input files."
                : $"Imported: {r.FilesImported} file(s) • New: {r.JobsQueued} • Existing: {r.JobsExisting}";
    }

    void UpdateSummary()=>SummaryText.Text=$"{_tasks.Count} task(s) • {_tasks.Count(x=>x.Status=="Queued")} queued";
    void TaskList_SelectionChanged(object sender,SelectionChangedEventArgs e) {
        ProcessSelectedButton.IsEnabled = TaskList.SelectedItem is JobTask j && j.Status=="Queued";
    }

    void ProcessSelected_Click(object sender,RoutedEventArgs e) {
        if(TaskList.SelectedItem is not JobTask job) return;

        // Step 1 — preparation. This is the operation that can actually fail.
        PreparedRequest prepared;
        try {
            prepared=RequestPreparation.Prepare(job,Storage.LoadSettings());
        } catch(Exception ex) {
            ImportMessage.Text="Prepare failed: "+ex.Message;
            return;
        }

        // The prepared input is saved and visible before the clipboard is touched.
        _settings?.RefreshInspector();
        if(ChatView.CoreWebView2 != null) ChatView.CoreWebView2.Navigate("https://chatgpt.com/");

        // Step 2 — clipboard. A clipboard problem never marks the preparation as failed.
        var clip=ClipboardService.SetText(prepared.Text);
        ImportMessage.Text=$"Prepared {job.Company} — {job.Title} successfully. "+clip.Message;
    }
}