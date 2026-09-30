using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace ResumeBuilder;

public partial class EmailTasksPage : System.Windows.Controls.UserControl {
    readonly ObservableCollection<EmailTask> _tasks = new();
    EmailExtractor.Draft? _draft;

    public Func<Task>? StartEmailTasks { get; set; }
    public Func<EmailTask, Task>? TailorOne { get; set; }
    public Func<string, string, Task<EmailExtractor.Draft?>>? ExtractWithGpt { get; set; }
    public Action? StopEmailTasks { get; set; }
    bool _running;
    bool _extracting;
    int _extractView;

    public void ShowExtractFailure() {
        PageStatus.Text = "Email extraction failed.";
    }

    public void SetRunning(bool running) {
        var wasRunning = _running;
        _running = running;
        StartEmailButton.Content = running ? "Stop Email Tasks" : "Start Email Tasks";
        TailorSelectedButton.IsEnabled = !running;
        if (running)
            PageStatus.Text = "Tailoring email tasks. The current task will finish before a stop.";
        else if (wasRunning)
            PageStatus.Text = "Email Tasks finished.";
        ShowEmailBusy(running || _extracting);
    }

    public bool SaveTasks() => Storage.SaveEmailTasks(_tasks);

    void ShowEmailBusy(bool busy) {
        if (EmailBusyBar is null) return;
        EmailBusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    public EmailTask? Find(string id) =>
        _tasks.FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public List<EmailTask> PendingInOrder() =>
        _tasks.Where(t => t.Status == EmailTaskStatus.Pending).OrderBy(t => t.CreatedAt).ToList();

    public EmailTasksPage() {
        InitializeComponent();
        var interrupted = false;
        foreach (var task in Storage.LoadEmailTasks()) {
            task.Status = EmailTaskStatus.Normalize(task.Status);
            if (task.Status == EmailTaskStatus.Processing) {
                task.Status = EmailTaskStatus.Pending;
                interrupted = true;
                PerfLog.Line("EMAIL recover-processing " + task.Id);
            }
            try { EmailOutput.ApplyStoredResume(task); }
            catch (Exception ex) { PerfLog.Line("EMAIL resume path " + ex.GetType().Name); }
            _tasks.Add(task);
        }
        if (interrupted) Storage.SaveEmailTasks(_tasks);
        EmailGrid.ItemsSource = _tasks;
        ShowEmpty();
        SenderBox.TextChanged += (_, _) => RefreshAddButton();
        CompanyBox.TextChanged += (_, _) => RefreshAddButton();
        TitleBox.TextChanged += (_, _) => RefreshAddButton();
        DescriptionBox.TextChanged += (_, _) => RefreshAddButton();
        ResultUrlBox.TextChanged += (_, _) => RefreshAddButton();
    }

    async void Extract_Click(object sender, RoutedEventArgs e) {
        if (_extracting || ExtractWithGpt is null) return;
        var pasted = PasteBox.Text ?? "";
        if (string.IsNullOrWhiteSpace(pasted)) {
            PageStatus.Text = "Paste one email or job description first.";
            return;
        }
        _extracting = true;
        if (sender is System.Windows.Controls.Button extractButton) extractButton.IsEnabled = false;
        ClearExtractedFields();
        PageStatus.Text = "Reading this email in the AI Workspace…";
        ShowEmailBusy(true);
        try {
            var draft = await ExtractWithGpt(pasted, "");
            if (draft is null) {
                if (PageStatus.Text != "Email extraction failed.")
                    PageStatus.Text = "Could not extract this email.";
                return;
            }
            if (Dispatcher.CheckAccess())
                ShowExtracted(draft);
            else
                Dispatcher.Invoke(() => ShowExtracted(draft));
            var view = _extractView;
            _ = LogExtractedAfterDelay(view);
        } finally {
            _extracting = false;
            ShowEmailBusy(_running);
            if (sender is System.Windows.Controls.Button button) button.IsEnabled = true;
        }
    }

    void PasteBox_Loaded(object sender, RoutedEventArgs e) => ShowPasteText();

    /// <summary>
    /// The shared TextBox template centers its content host, which hides the glyphs in this box.
    /// Stretch the host and force the dark-theme ink so typed and pasted text stays visible.
    /// </summary>
    void ShowPasteText() {
        PasteBox.Opacity = 1;
        PasteBox.IsReadOnly = false;
        PasteBox.Foreground = (System.Windows.Media.Brush)FindResource("Text.Primary");
        PasteBox.Background = (System.Windows.Media.Brush)FindResource("Bg.Input");
        PasteBox.CaretBrush = (System.Windows.Media.Brush)FindResource("Text.Primary");
        PasteBox.VerticalContentAlignment = System.Windows.VerticalAlignment.Stretch;
        PasteBox.ApplyTemplate();
        if (PasteBox.Template?.FindName("PART_ContentHost", PasteBox) is ScrollViewer host) {
            host.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
            host.VerticalAlignment = System.Windows.VerticalAlignment.Stretch;
            host.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            host.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            System.Windows.Documents.TextElement.SetForeground(host, PasteBox.Foreground);
        }
    }

    void ClearExtractedFields() {
        _draft = null;
        _extractView++;
        SetExtracted(SenderBox, "");
        SetExtracted(CompanyBox, "");
        SetExtracted(TitleBox, "");
        SetExtracted(ResultUrlBox, "");
        SetExtracted(DescriptionBox, "");
        RefreshAddButton();
    }

    void ShowExtracted(EmailExtractor.Draft draft) {
        _draft = draft;
        _extractView++;
        WriteExtractedFields();
        LogExtractedUi("EMAIL ui-after-assign");
        PerfLog.Line("EMAIL extract-fields sender=" + Filled(SenderBox.Text)
            + " company=" + Filled(CompanyBox.Text)
            + " title=" + Filled(TitleBox.Text)
            + " jd=" + Filled(DescriptionBox.Text)
            + " url=" + Filled(ResultUrlBox.Text));
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(WriteExtractedFields));
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(RefreshAddButton));
    }

    void WriteExtractedFields() {
        if (_draft is null) {
            RefreshAddButton();
            return;
        }
        SetExtracted(SenderBox, _draft.Sender);
        SetExtracted(CompanyBox, _draft.Company);
        SetExtracted(TitleBox, _draft.JobTitle);
        SetExtracted(ResultUrlBox, _draft.EmailUrl);
        SetExtracted(DescriptionBox, _draft.JobDescription);
        RefreshAddButton();
        PageStatus.Text = string.IsNullOrWhiteSpace(CompanyBox.Text)
            ? "Extracted. Company was left blank."
            : "Extracted. Review the fields, then add the task.";
    }

    /// <summary>
    /// The same rule as before: Add is on only while an extraction is on screen and it has
    /// a job title or a job description. Company, sender, and URL may stay blank.
    /// </summary>
    void RefreshAddButton() {
        AddButton.IsEnabled = _draft is not null
            && (!string.IsNullOrWhiteSpace(TitleBox.Text) || !string.IsNullOrWhiteSpace(DescriptionBox.Text));
    }

    async Task LogExtractedAfterDelay(int view) {
        await Task.Delay(300);
        await Dispatcher.InvokeAsync(() => {
            if (view != _extractView) return;
            LogExtractedUi("EMAIL ui-after-delay");
            if (_draft is null) {
                RefreshAddButton();
                return;
            }
            if (Cleared(SenderBox, _draft.Sender) || Cleared(CompanyBox, _draft.Company) || Cleared(TitleBox, _draft.JobTitle))
                WriteExtractedFields();
            else
                RefreshAddButton();
        });
    }

    void LogExtractedUi(string prefix) {
        var binding = SenderBox.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty);
        PerfLog.Line(prefix
            + " senderProp=" + Shown(_draft?.Sender)
            + " senderBox=" + Shown(SenderBox.Text)
            + " companyProp=" + Shown(_draft?.Company)
            + " companyBox=" + Shown(CompanyBox.Text)
            + " titleProp=" + Shown(_draft?.JobTitle)
            + " titleBox=" + Shown(TitleBox.Text)
            + " instance=" + RuntimeHelpers.GetHashCode(this)
            + " visible=" + IsVisible
            + " data=" + (DataContext?.GetType().Name ?? "none")
            + " bind=" + (binding?.ParentBinding.Path.Path ?? "none"));
    }

    static bool Cleared(System.Windows.Controls.TextBox box, string? property) =>
        !string.IsNullOrWhiteSpace(property) && string.IsNullOrWhiteSpace(box.Text);

    static string Shown(string? value) {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        var flat = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (flat.Length > 60) flat = flat[..60];
        return "\"" + flat + "\"";
    }

    static void SetExtracted(System.Windows.Controls.TextBox box, string? value) {
        box.ApplyTemplate();
        if (box.Template?.FindName("PART_ContentHost", box) is ScrollViewer host) {
            host.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
            host.VerticalAlignment = System.Windows.VerticalAlignment.Stretch;
            host.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            host.VerticalScrollBarVisibility = box.AcceptsReturn
                ? ScrollBarVisibility.Auto
                : ScrollBarVisibility.Disabled;
            host.ScrollToHorizontalOffset(0);
            host.ScrollToVerticalOffset(0);
        }
        if (!box.AcceptsReturn)
            box.Padding = new Thickness(10, 1, 10, 1);
        box.VerticalContentAlignment = System.Windows.VerticalAlignment.Top;
        box.Text = value ?? "";
    }

    static string Filled(string? value) => string.IsNullOrWhiteSpace(value) ? "false" : "true";

    void Add_Click(object sender, RoutedEventArgs e) {
        if (_draft is null) return;
        var original = PasteBox.Text;
        if (string.IsNullOrWhiteSpace(original)) original = _draft.OriginalText;
        var task = new EmailTask {
            Id = _draft.Id,
            CreatedAt = DateTimeOffset.Now,
            Sender = SenderBox.Text.Trim(),
            Company = BlankToNull(CompanyBox.Text),
            JobTitle = TitleBox.Text.Trim(),
            JobDescription = DescriptionBox.Text,
            EmailUrl = BlankToNull(ResultUrlBox.Text),
            OriginalText = original,
            Status = EmailTaskStatus.Pending
        };
        if (_tasks.Any(t => t.Id.Equals(task.Id, StringComparison.OrdinalIgnoreCase)))
            task.Id = EmailExtractor.NewId();
        _tasks.Insert(0, task);
        if (!Storage.SaveEmailTasks(_tasks)) {
            _tasks.Remove(task);
            PageStatus.Text = "Could not save email tasks.";
            return;
        }
        _draft = null;
        RefreshAddButton();
        ShowEmpty();
        EmailGrid.SelectedItem = task;
        PageStatus.Text = "Added to Email Tasks.";
        PerfLog.Line("EMAIL task-added " + task.Id);
    }

    async void StartEmail_Click(object sender, RoutedEventArgs e) {
        if (_running) {
            PageStatus.Text = "Stopping after the current email task.";
            StopEmailTasks?.Invoke();
            return;
        }
        if (StartEmailTasks is null) return;
        await StartEmailTasks();
    }

    async void TailorSelected_Click(object sender, RoutedEventArgs e) {
        if (_running) return;
        if (EmailGrid.SelectedItem is not EmailTask task || !task.CanTailor) {
            PageStatus.Text = "Select a Pending or Failed email task.";
            return;
        }
        await RequestTailor(task);
    }

    async void Tailor_Click(object sender, RoutedEventArgs e) {
        if (_running) return;
        await RequestTailor(TaskFrom(sender));
    }

    async Task RequestTailor(EmailTask? task) {
        if (task is null || !task.CanTailor || TailorOne is null) return;
        EmailGrid.SelectedItem = task;
        PerfLog.Line("EMAIL selected-request " + task.Id);
        await TailorOne(task);
    }

    void Resume_Click(object sender, RoutedEventArgs e) {
        var task = TaskFrom(sender);
        if (task is null || !task.CanOpenResume) return;
        EmailTask.OpenPath(task.DocxPath);
    }

    void Pdf_Click(object sender, RoutedEventArgs e) {
        var task = TaskFrom(sender);
        if (task is null || !task.CanOpenPdf) return;
        EmailTask.OpenPath(task.PdfPath);
    }

    void Folder_Click(object sender, RoutedEventArgs e) {
        var task = TaskFrom(sender);
        if (task is null || !task.CanOpenFolder) return;
        EmailTask.OpenPath(task.OutputFolder);
    }

    void OpenEmail_Click(object sender, RoutedEventArgs e) {
        var task = TaskFrom(sender);
        if (task is null || !task.CanOpenEmail) return;
        EmailTask.OpenEmail(task);
    }

    void Remove_Click(object sender, RoutedEventArgs e) {
        var task = TaskFrom(sender);
        if (task is null || task.Status == EmailTaskStatus.Processing) return;
        var index = _tasks.IndexOf(task);
        _tasks.Remove(task);
        if (!Storage.SaveEmailTasks(_tasks)) {
            if (index < 0) _tasks.Add(task);
            else _tasks.Insert(index, task);
            PageStatus.Text = "Could not save email tasks.";
            return;
        }
        ShowEmpty();
        PageStatus.Text = "Removed from Email Tasks.";
        PerfLog.Line("EMAIL task-removed " + task.Id);
    }

    async void Retry_Click(object sender, RoutedEventArgs e) {
        var task = TaskFrom(sender);
        if (task is null || task.Status != EmailTaskStatus.Failed) return;
        task.FailureReason = null;
        task.Status = EmailTaskStatus.Pending;
        if (!Storage.SaveEmailTasks(_tasks)) {
            PageStatus.Text = "Could not save email tasks.";
            return;
        }
        await RequestTailor(task);
    }

    static EmailTask? TaskFrom(object sender) =>
        sender is System.Windows.Controls.Button { DataContext: EmailTask task } ? task : null;

    static string? BlankToNull(string? text) {
        var trimmed = text?.Trim() ?? "";
        return trimmed.Length == 0 ? null : trimmed;
    }

    void ShowEmpty() =>
        EmptyText.Visibility = _tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
}
