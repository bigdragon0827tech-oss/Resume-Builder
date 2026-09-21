using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;

namespace ResumeBuilder;

public partial class DashboardPage : UserControl {
    public event Action? RefreshInputRequested;
    public event Action? OpenJobBrowserRequested;
    public event Action? StartQueueRequested;
    public event Action? OpenReadyToApplyRequested;

    public DashboardPage() => InitializeComponent();

    public void Refresh(IReadOnlyList<JobTask> tasks, QueueState queueState, string? activeJobLabel, int attempt) {
        var activity = JobTracker.GetApplicationActivity(tasks);
        TodayCountText.Text = activity.Today.ToString();
        MonthCountText.Text = activity.Last30Days.ToString();
        TotalCountText.Text = activity.Total.ToString();
        ReadyToApplyCountText.Text = JobTracker.CountNeedsAction(tasks).ToString();

        var queued = tasks.Count(t => t.Status == "Queued");
        var failed = tasks.Count(t => t.Status == "Failed");
        var processing = tasks.Count(t => t.Status == "Processing");
        QueueStateText.Text = queueState switch {
            QueueState.Running => "Running",
            QueueState.Paused => "Paused",
            QueueState.Finished => "Finished",
            _ => "Idle"
        };
        QueueDetailText.Text = activeJobLabel is null
            ? $"{queued} queued · {processing} processing · {failed} failed"
            : $"{activeJobLabel}" + (attempt > 0 ? $" · GPT attempt {attempt}/{GptAttempts.MaxAttempts}" : "")
              + $"\n{queued} queued · {failed} failed";

        var stages = JobTracker.GetPipelineCounts(tasks);
        PipelineView.ItemsSource = stages.Select((stage, index) => {
            var previous = index == 0 ? 0 : stages[index - 1].Count;
            return new {
                stage.Status,
                stage.Count,
                Conversion = previous + stage.Count == 0
                    ? ""
                    : JobStatistics.WholePercent(JobStatistics.PairShare(previous, stage.Count)) + " of pair",
                ConversionVisibility = index > 0 && previous + stage.Count > 0
                    ? Visibility.Visible : Visibility.Collapsed
            };
        }).ToList();

        RecentList.ItemsSource = tasks
            .OrderByDescending(t => t.UpdatedAt)
            .Take(8)
            .Select(t => new {
                Title = $"{t.Company} — {t.Title}",
                Meta = $"{t.StatusDisplay} · {t.ApplicationStatus} · {t.TrackingDateDisplay}"
            })
            .ToList();
    }

    void RefreshInput_Click(object sender, RoutedEventArgs e) => RefreshInputRequested?.Invoke();
    void OpenBrowser_Click(object sender, RoutedEventArgs e) => OpenJobBrowserRequested?.Invoke();
    void StartQueue_Click(object sender, RoutedEventArgs e) => StartQueueRequested?.Invoke();
    void ReadyToApply_Click(object sender, RoutedEventArgs e) => OpenReadyToApplyRequested?.Invoke();
}
