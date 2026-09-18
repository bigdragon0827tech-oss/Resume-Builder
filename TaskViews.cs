using System.Collections.ObjectModel;
using System.Windows.Data;

namespace ResumeBuilder;

/// <summary>
/// The Active / History split of the task queue. Display only: both views are filters over the one
/// live task collection, so nothing is copied, moved or deleted, and tasks.json is untouched.
///
/// Active is everything that is not Completed — Queued, Processing and Failed (Failed stays Active
/// because it still needs Retry). History is Completed only. A Processing task is therefore always
/// in Active.
/// </summary>
public static class TaskViews {
    public const string CompletedStatus = "Completed";

    public static bool IsHistory(JobTask task) => task.Status == CompletedStatus;

    /// <summary>True when <paramref name="task"/> is shown in the view selected by <paramref name="history"/>.</summary>
    public static bool Belongs(JobTask task, bool history) => IsHistory(task) == history;

    public static int ActiveCount(IEnumerable<JobTask> tasks) => tasks.Count(t => !IsHistory(t));

    public static int HistoryCount(IEnumerable<JobTask> tasks) => tasks.Count(IsHistory);

    /// <summary>
    /// A filtered view over <paramref name="tasks"/> that re-filters by itself when a task's Status
    /// changes, so a job that completes leaves Active and appears in History without a refresh.
    /// <paramref name="showHistory"/> is read on every filter pass; call Refresh after changing it.
    /// </summary>
    public static ListCollectionView CreateView(ObservableCollection<JobTask> tasks, Func<bool> showHistory) {
        var view = new ListCollectionView(tasks) {
            Filter = item => item is JobTask t && Belongs(t, showHistory()),
            IsLiveFiltering = true,
        };
        view.LiveFilteringProperties.Add(nameof(JobTask.Status));
        return view;
    }
}
