using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UserControl = System.Windows.Controls.UserControl;
using MessageBox = System.Windows.MessageBox;

namespace ResumeBuilder;

public partial class DocumentsPage : UserControl {
    public sealed class DocumentRow {
        public string JobId { get; init; } = "";
        public string Company { get; init; } = "";
        public string Title { get; init; } = "";
        public string DateText { get; init; } = "";
        public string? DocxPath { get; init; }
        public string? PdfPath { get; init; }
        public string? FolderPath { get; init; }
        public bool HasDocx => File.Exists(DocxPath);
        public bool HasPdf => File.Exists(PdfPath);
        public bool HasFolder => Directory.Exists(FolderPath);
        public string DocxLabel => HasDocx ? "Yes" : "—";
        public string PdfLabel => HasPdf ? "Yes" : "—";
    }

    IReadOnlyList<JobTask> _tasks = Array.Empty<JobTask>();
    List<DocumentRow> _rows = new();

    public DocumentsPage() => InitializeComponent();

    public void Refresh(IReadOnlyList<JobTask> tasks) {
        _tasks = tasks;
        JobStore.ApplyResumeLinks(tasks);
        _rows = tasks
            .Where(t => t.ResumeGenerated)
            .OrderByDescending(t => t.ReadyAt ?? t.UpdatedAt)
            .Select(ToRow)
            .ToList();
        ApplyFilter();
    }

    static DocumentRow ToRow(JobTask t) {
        var resume = t.ResumePath;
        var folder = string.IsNullOrWhiteSpace(resume) ? null : Path.GetDirectoryName(resume);
        string? pdf = null;
        if (!string.IsNullOrWhiteSpace(resume)) {
            var candidate = Path.ChangeExtension(resume, ".pdf");
            if (File.Exists(candidate)) pdf = candidate;
            else if (folder is not null) {
                var name = Path.GetFileNameWithoutExtension(resume);
                var alt = Path.Combine(folder, name + ".pdf");
                if (File.Exists(alt)) pdf = alt;
            }
        }
        return new DocumentRow {
            JobId = t.JobId,
            Company = t.Company,
            Title = t.Title,
            DateText = (t.ReadyAt ?? t.UpdatedAt).ToString("yyyy-MM-dd"),
            DocxPath = File.Exists(resume) ? resume : null,
            PdfPath = pdf,
            FolderPath = folder
        };
    }

    void ApplyFilter() {
        var q = (SearchBox.Text ?? "").Trim();
        var filtered = string.IsNullOrEmpty(q)
            ? _rows
            : _rows.Where(r =>
                (r.Company + " " + r.Title + " " + r.JobId)
                .Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        DocsGrid.ItemsSource = filtered;
        EmptyText.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _rows.Count == 0
            ? "No generated resumes yet."
            : "No documents match the search.";
    }

    void Search_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();
    void Refresh_Click(object sender, RoutedEventArgs e) => Refresh(_tasks);

    void OpenRoot_Click(object sender, RoutedEventArgs e) {
        var root = Storage.LoadSettings().ResumeRootFolder;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) {
            MessageBox.Show("Configure an existing Resume Root folder in Settings first.");
            return;
        }
        TryOpen(root);
    }

    void OpenDocx_Click(object sender, RoutedEventArgs e) {
        if ((sender as FrameworkElement)?.Tag is DocumentRow row && row.HasDocx) TryOpen(row.DocxPath!);
    }
    void OpenPdf_Click(object sender, RoutedEventArgs e) {
        if ((sender as FrameworkElement)?.Tag is DocumentRow row && row.HasPdf) TryOpen(row.PdfPath!);
    }
    void OpenFolder_Click(object sender, RoutedEventArgs e) {
        if ((sender as FrameworkElement)?.Tag is DocumentRow row && row.HasFolder) TryOpen(row.FolderPath!);
    }

    void DocsGrid_DoubleClick(object sender, MouseButtonEventArgs e) {
        if (DocsGrid.SelectedItem is DocumentRow row) {
            if (row.HasDocx) TryOpen(row.DocxPath!);
            else if (row.HasFolder) TryOpen(row.FolderPath!);
        }
    }

    static void TryOpen(string path) {
        try {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        } catch (Exception ex) {
            PerfLog.Line("DOCUMENTS open failed " + ex.GetType().Name);
            MessageBox.Show("Unable to open that file.");
        }
    }
}
