using System.IO;
using System.Windows;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
namespace ResumeBuilder;

public partial class SettingsWindow : System.Windows.Controls.UserControl {
    AppSettings _s=Storage.LoadSettings();
    public SettingsWindow(){ InitializeComponent(); BuildImportFilterSwitches(); LoadFields(); RefreshInspector(); InitTracking(); HideDevelopmentToolsInRelease(); }

    /// <summary>
    /// The Development tab (Load Sample Jobs, Remove Sample Jobs, Clear Job History) is for testing
    /// this app, not for customers: shown in DEBUG builds, hidden — tab and nav button — in RELEASE.
    /// The actions themselves are unchanged.
    /// </summary>
    void HideDevelopmentToolsInRelease(){
#if !DEBUG
        NavDevelopment.Visibility=Visibility.Collapsed;
        DevelopmentTab.Visibility=Visibility.Collapsed;
#endif
    }

    // ---------- job import filters ----------
    //
    // The same five AppSettings values the Job Browser toolbar edits. There is no second model:
    // both screens build their checkboxes from JobImportFilter.Switches and read and write the one
    // settings.json. This page keeps the page's own Save-button convention; the browser popup saves
    // on each tick, and ReloadSettings() picks that up whenever this page is shown again.

    readonly List<System.Windows.Controls.CheckBox> _filterBoxes=new();

    void BuildImportFilterSwitches(){
        foreach(var descriptor in JobImportFilter.Switches){
            var box=new System.Windows.Controls.CheckBox{
                Content=descriptor.Label,
                Margin=new Thickness(0,6,0,6),
                Tag=descriptor.Reason
            };
            _filterBoxes.Add(box);
            FilterSwitchHost.Children.Add(box);
        }
        FilterFooterNote.Text=JobImportFilter.AppliesToFutureImports+
            " Jobs already imported are never removed or re-checked.";
    }

    /// <summary>Copies the persisted switches into the checkboxes.</summary>
    void LoadImportFilterSwitches(){
        for(var i=0;i<_filterBoxes.Count&&i<JobImportFilter.Switches.Count;i++)
            _filterBoxes[i].IsChecked=JobImportFilter.Switches[i].Get(_s);
    }

    /// <summary>Copies the checkboxes into a settings object being saved.</summary>
    void ApplyImportFilterSwitches(AppSettings target){
        for(var i=0;i<_filterBoxes.Count&&i<JobImportFilter.Switches.Count;i++)
            JobImportFilter.Switches[i].Set(target,_filterBoxes[i].IsChecked==true);
    }

    /// <summary>
    /// Re-reads settings.json into this page. The shell calls it whenever Settings is shown, so a
    /// filter toggled on the Job Browser toolbar appears here — and, just as importantly, is not
    /// written back out of a stale copy by the next Save.
    /// </summary>
    public void ReloadSettings(){ _s=Storage.LoadSettings(); LoadFields(); }

    /// <summary>The filter captions on show, for the UI harness.</summary>
    public IReadOnlyList<string> ImportFilterLabels()=>_filterBoxes.Select(b=>b.Content as string??"").ToList();

    /// <summary>Whether each filter is ticked here, in JobImportFilter.Switches order.</summary>
    public IReadOnlyList<bool> ImportFilterStates()=>_filterBoxes.Select(b=>b.IsChecked==true).ToList();

    /// <summary>Ticks one filter and saves, exactly as the page's own Save does. For the harness.</summary>
    public void SetImportFilterAndSave(JobFilterReason reason,bool enabled){
        var box=_filterBoxes.FirstOrDefault(b=>b.Tag is JobFilterReason r&&r==reason);
        if(box is null) return;
        box.IsChecked=enabled;
        SaveSettingsFromFields(announce:false);
    }

    // ---------- job application tracking dashboard ----------
    //
    // The views edit the live JobTask objects from MainWindow whenever this window has one as its
    // owner, so a status change here and a queue change there can never overwrite each other. Every
    // calculation lives in JobTracker; this is only the screen.

    /// <summary>The hosting main window when this view is embedded in the shell.</summary>
    MainWindow? HostMain => System.Windows.Window.GetWindow(this) as MainWindow;

    /// <summary>The tasks being tracked: the main window's live list, or the saved file if standalone.</summary>
    IReadOnlyList<JobTask> TrackedTasks => HostMain?.Tasks ?? (_standaloneTasks ??= Storage.LoadTasks());
    List<JobTask>? _standaloneTasks;

    /// <summary>Set while the dashboard is rebuilding, so rebinding a row does not look like an edit.</summary>
    bool _refreshingTracking;
    int _refreshSerial;

    /// <summary>One bar of the activity chart. Heights are pixels, worked out per refresh.</summary>
    sealed class ActivityBar {
        public string Label { get; init; } = "";
        public string CountLabel { get; init; } = "";
        public double BarHeight { get; init; }
        public double Width { get; init; }
        public Thickness Gap { get; init; }
        public System.Windows.Media.Brush BarBrush { get; init; } = System.Windows.Media.Brushes.Gray;
        public string Tooltip { get; init; } = "";
    }

    const string ActivityLast7="7 Days", ActivityLast30="30 Days", ActivityAllTime="All Time";

    /// <summary>The exact day picked from the calendar, or null when a quick range is in force.</summary>
    DateTime? _exactDate;

    /// <summary>The quick range currently chosen. Ignored while an exact date is set.</summary>
    string _dateFilter=DateFilter.AllDates;

    /// <summary>The ticked application platforms. Empty means all. Lives only in this window.</summary>
    MultiSelectFilter<ApplicationPlatform>? _platformFilter;
    string _platformChoiceKey = "";

    void InitTracking() {
        foreach(var filter in ApplicationStatus.Filter.Options) TrackingFilterBox.Items.Add(filter);
        var platforms=JobTracker.PlatformsPresent(TrackedTasks);
        _platformChoiceKey=string.Join("\n", platforms);
        _platformFilter=new(PlatformFilterButton,PlatformFilterPopup,PlatformCheckList,PlatformClearButton,
                            platforms,JobTracker.PlatformDisplayName,
                            JobTracker.PlatformFilterLabel,RefreshTracking,"All platforms");
        foreach(var range in DateFilter.Options) QuickDateList.Items.Add(range);
        foreach(var range in new[]{ActivityLast7,ActivityLast30,ActivityAllTime}) ActivityRangeBox.Items.Add(range);

        _refreshingTracking=true;
        TrackingFilterBox.SelectedIndex=0;      // All
        ActivityRangeBox.SelectedIndex=1;       // 30 days
        _refreshingTracking=false;

        RefreshTracking();
    }

    // ---------- date filter: quick ranges plus an exact day ----------

    void DateFilterButton_Click(object s,RoutedEventArgs e) {
        DateFilterPopup.IsOpen=DateFilterButton.IsChecked==true;
        if(!DateFilterPopup.IsOpen) return;

        _refreshingTracking=true;
        QuickDateList.SelectedItem=_exactDate is null?_dateFilter:null;
        DateFilterCalendar.SelectedDate=_exactDate;
        DateFilterCalendar.DisplayDate=_exactDate ?? DateTime.Now;
        _refreshingTracking=false;
    }

    void DateFilterPopup_Closed(object? s,EventArgs e) => DateFilterButton.IsChecked=false;

    void QuickDate_Changed(object s,System.Windows.Controls.SelectionChangedEventArgs e) {
        if(_refreshingTracking || QuickDateList.SelectedItem is not string choice) return;

        _dateFilter=choice;
        _exactDate=null;
        DateFilterCalendar.SelectedDate=null;
        DateFilterButton.Content=choice;
        DateFilterPopup.IsOpen=false;
        RefreshTracking();
    }

    /// <summary>An exact day from the calendar. It uses the same tracking date as the quick ranges.</summary>
    void DateFilterCalendar_Changed(object s,System.Windows.Controls.SelectionChangedEventArgs e) {
        if(_refreshingTracking || DateFilterCalendar.SelectedDate is not DateTime day) return;

        _exactDate=day.Date;
        QuickDateList.SelectedItem=null;
        DateFilterButton.Content=day.ToString("MMM d, yyyy");
        DateFilterPopup.IsOpen=false;
        RefreshTracking();
    }

    // ---------- multi-select filters (Platforms, Readiness): checkbox popup, OR matching ----------

    /// <summary>
    /// One checkbox-popup filter: a toggle button, its popup, a checkbox per choice and a Clear link.
    /// Each tick filters at once; the popup stays open until an outside click; Clear unticks everything
    /// with a single refresh. The selection lives here only — never in tasks.json, settings or a task.
    /// </summary>
    sealed class MultiSelectFilter<T> where T : struct {
        readonly HashSet<T> _selected=new();
        readonly System.Windows.Controls.Primitives.ToggleButton _button;
        readonly System.Windows.Controls.Panel _list;
        readonly Func<IReadOnlyCollection<T>,string> _label;
        readonly Action _changed;
        bool _setting;

        public IReadOnlyCollection<T> Selected => _selected;

        readonly Func<T,string> _displayName;

        public MultiSelectFilter(System.Windows.Controls.Primitives.ToggleButton button,
                                 System.Windows.Controls.Primitives.Popup popup,
                                 System.Windows.Controls.Panel list, System.Windows.Controls.Button clear,
                                 IEnumerable<T> order, Func<T,string> displayName,
                                 Func<IReadOnlyCollection<T>,string> label, Action changed,
                                 string? allLabel = null) {
            _button=button; _list=list; _label=label; _changed=changed; _displayName=displayName;

            if(allLabel is not null) {
                var all=new System.Windows.Controls.CheckBox {
                    Content=allLabel, Tag=allLabel, IsChecked=true, FontSize=11.5, Margin=new Thickness(0,3,0,3)
                };
                all.Checked+=All_Changed;
                all.Unchecked+=All_Changed;
                list.Children.Add(all);
            }
            foreach(var choice in order) list.Children.Add(ChoiceBox(choice, false));
            button.Click+=(_,_) => popup.IsOpen=button.IsChecked==true;
            popup.Closed+=(_,_) => button.IsChecked=false;
            clear.Click+=(_,_) => Clear();
            button.Content=label(_selected);
        }

        System.Windows.Controls.CheckBox ChoiceBox(T choice, bool ticked) {
            var box=new System.Windows.Controls.CheckBox {
                Content=_displayName(choice), Tag=choice, IsChecked=ticked, FontSize=11.5, Margin=new Thickness(0,3,0,3)
            };
            box.Checked+=Check_Changed;
            box.Unchecked+=Check_Changed;
            return box;
        }

        /// <summary>Rebuilds the platform rows from the jobs now on screen. Keeps ticks that still exist.</summary>
        public void ReplaceChoices(IEnumerable<T> order) {
            var keep=new HashSet<T>(_selected);
            _setting=true;
            for(var i=_list.Children.Count-1; i>=0; i--)
                if(_list.Children[i] is System.Windows.Controls.CheckBox box && box.Tag is T)
                    _list.Children.RemoveAt(i);
            foreach(var choice in order) _list.Children.Add(ChoiceBox(choice, keep.Contains(choice)));
            _setting=false;
            ReadSelection();
            SyncAllBox();
            _button.Content=_label(_selected);
        }

        void Check_Changed(object s,RoutedEventArgs e) {
            if(_setting || s is not System.Windows.Controls.CheckBox { Tag: T choice } box) return;
            if(box.IsChecked==true) _selected.Add(choice); else _selected.Remove(choice);
            SyncAllBox();
            _button.Content=_label(_selected);
            _changed();
        }

        void All_Changed(object s,RoutedEventArgs e) {
            if(_setting || s is not System.Windows.Controls.CheckBox box) return;
            if(box.IsChecked==true) SelectOnly(Array.Empty<T>());
            else { _setting=true; box.IsChecked=true; _setting=false; }
        }

        void SyncAllBox() {
            foreach(var box in _list.Children.OfType<System.Windows.Controls.CheckBox>()) {
                if(box.Tag is not string) continue;
                var all=_selected.Count==0;
                if(box.IsChecked==all) continue;
                _setting=true;
                box.IsChecked=all;
                _setting=false;
            }
        }

        void ReadSelection() {
            _selected.Clear();
            foreach(var box in _list.Children.OfType<System.Windows.Controls.CheckBox>())
                if(box.IsChecked==true && box.Tag is T choice) _selected.Add(choice);
        }

        /// <summary>
        /// Makes exactly <paramref name="choices"/> the selection: ticks those boxes, unticks the rest,
        /// updates the label, and refreshes ONCE (not per box). With <paramref name="refresh"/> false the
        /// caller refreshes, e.g. when it resets several filters in one go. Clear is SelectOnly(nothing).
        /// </summary>
        public void SelectOnly(IEnumerable<T> choices,bool refresh=true) {
            var wanted=new HashSet<T>(choices);
            _setting=true;              // setting boxes must not refresh once per box
            foreach(var box in _list.Children.OfType<System.Windows.Controls.CheckBox>())
                box.IsChecked=box.Tag is T choice && wanted.Contains(choice);
            _setting=false;
            ReadSelection();
            SyncAllBox();
            _button.Content=_label(_selected);
            if(refresh) _changed();
        }

        void Clear() {
            if(_selected.Count==0) return;
            SelectOnly(Array.Empty<T>());
        }
    }

    void TrackingFilter_Changed(object s,System.Windows.Controls.SelectionChangedEventArgs e) {
        if(!_refreshingTracking) RefreshTracking();
    }

    void TrackingSearch_Changed(object s,System.Windows.Controls.TextChangedEventArgs e) {
        if(!_refreshingTracking) RefreshTracking();
    }

    void TrackingRefresh_Click(object s,RoutedEventArgs e) {
        _standaloneTasks=null;                  // re-read from disk when there is no main window
        RefreshTracking();
    }

    /// <summary>The platform popup lists only platforms that exist on a job. "All platforms" stays first.</summary>
    void RefreshPlatformChoices(IReadOnlyList<JobTask> tasks) {
        if(_platformFilter is null) return;
        var present=JobTracker.PlatformsPresent(tasks);
        var key=string.Join("\n", present);
        if(key==_platformChoiceKey) return;
        _platformChoiceKey=key;
        _platformFilter.ReplaceChoices(present);
    }

    /// <summary>Rebuilds every part of the dashboard from the current tasks and filters.</summary>
    public void RefreshTracking() {
        if(TrackingGrid is null || _refreshingTracking) return;
        var refresh = ++_refreshSerial;
        var total = System.Diagnostics.Stopwatch.StartNew();
        PerfLog.Line("APPLICATIONS PERF refresh-enter n=" + refresh);
        _refreshingTracking=true;
        try {
            var step = System.Diagnostics.Stopwatch.StartNew();
            var tasks=TrackedTasks;
            PerfLog.Line("APPLICATIONS PERF load-tasks ms=" + step.ElapsedMilliseconds + " count=" + tasks.Count);

            RefreshPlatformChoices(tasks);

            // Resume paths come from SQLite. A missing file stays missing; this does not walk
            // the resume folders. An application URL stored in SQLite fills an empty one here,
            // and is never replaced by the job posting.
            step.Restart();
            PerfLog.Line("APPLICATIONS QUERY count=" + tasks.Count + " ms=" + step.ElapsedMilliseconds);

            step.Restart();
            var filtered=JobTracker.ApplyFilters(tasks,TrackingSearchBox.Text,
                                                 TrackingFilterBox.SelectedItem as string,
                                                 _dateFilter,null,_exactDate,
                                                 _platformFilter?.Selected);
            PerfLog.Line("APPLICATIONS PERF filter ms=" + step.ElapsedMilliseconds + " shown=" + filtered.Count);

            step.Restart();
            var selected=TrackingGrid.SelectedItem as JobTask;
            TrackingGrid.ItemsSource=filtered;
            if(selected is not null && filtered.Contains(selected)) TrackingGrid.SelectedItem=selected;

            ListEmptyText.Visibility=filtered.Count==0?Visibility.Visible:Visibility.Collapsed;
            ListEmptyText.Text=tasks.Count==0
                ? "No applications found."
                : "No applications match the current filters.";

            var activity=JobTracker.GetApplicationActivity(tasks);
            TodayCountText.Text=activity.Today.ToString();
            MonthCountText.Text=activity.Last30Days.ToString();
            TotalCountText.Text=activity.Total.ToString();

            PipelineViewedCount.Text=JobTracker.CountStatus(tasks,ApplicationStatus.Viewed).ToString();
            PipelineReadyCount.Text=JobTracker.CountStatus(tasks,ApplicationStatus.Ready).ToString();
            PipelineAppliedCount.Text=JobTracker.CountStatus(tasks,ApplicationStatus.Applied).ToString();
            PipelineInterviewCount.Text=JobTracker.CountStatus(tasks,ApplicationStatus.Interview).ToString();
            PipelineFailedCount.Text=JobTracker.CountStatus(tasks,ApplicationStatus.Failed).ToString();
            PipelineDoneCount.Text=JobTracker.CountStatus(tasks,ApplicationStatus.Done).ToString();

            var moves=JobTracker.GetTransitions(tasks);
            ArrowAppliedInterviewCount.Text=moves.AppliedToInterview.ToString();
            ArrowAppliedFailedCount.Text=moves.AppliedToFailed.ToString();
            ArrowInterviewFailedCount.Text=moves.InterviewToFailed.ToString();
            PerfLog.Line("APPLICATIONS PERF bind-ui ms=" + step.ElapsedMilliseconds);

            step.Restart();
            RefreshActivityChart(tasks);
            PerfLog.Line("APPLICATIONS PERF chart ms=" + step.ElapsedMilliseconds);

            PerfLog.Line($"TRACKING dashboard refreshed total={tasks.Count} applied={PipelineAppliedCount.Text} "+
                         $"interview={PipelineInterviewCount.Text} failed={PipelineFailedCount.Text} done={PipelineDoneCount.Text}");
            PerfLog.Line("APPLICATIONS PERF total ms=" + total.ElapsedMilliseconds + " n=" + refresh);
        } finally {
            _refreshingTracking=false;
        }
    }

    /// <summary>A plain WPF bar chart: one Border per day, scaled against the busiest day on show.</summary>
    void RefreshActivityChart(IReadOnlyList<JobTask> tasks) {
        const double PlotHeight=100;

        var days=(ActivityRangeBox.SelectedItem as string) switch {
            ActivityLast7 => 7,
            ActivityAllTime => 0,
            _ => 30
        };

        var counts=JobTracker.GetDailyApplicationCounts(tasks,days);
        var peak=counts.Count==0?0:counts.Max(c => c.Count);

        // Bars share the card's width, so a 30-day window stays inside it without scrolling.
        var (width,gap)=counts.Count switch {
            <= 8 => (46.0,4.0),
            <= 14 => (34.0,3.0),
            <= 24 => (24.0,2.0),
            <= 40 => (16.0,2.0),
            _ => (10.0,1.0)
        };

        // With many bars only every nth date is labelled, so the axis never overlaps itself.
        var labelEvery=counts.Count switch { <= 10 => 1, <= 16 => 2, <= 32 => 4, _ => 7 };
        var accent=TryFindResource("AccentSoft") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.SteelBlue;
        var faint=TryFindResource("GridLine") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gainsboro;

        ActivityChart.ItemsSource=counts.Select((c,index) => new ActivityBar{
            // Label the last bar always, then work backwards, so "today" is never the hidden one.
            Label=(counts.Count-1-index)%labelEvery==0?c.Label:"",
            CountLabel=c.Count>0?c.Count.ToString():"",
            Width=width,
            Gap=new Thickness(gap,0,gap,0),
            // A day with no applications keeps a faint sliver, so the axis reads as a row of days.
            BarHeight=peak==0?2:Math.Max(2,c.Count/(double)peak*PlotHeight),
            BarBrush=c.Count>0?accent:faint,
            Tooltip=$"{c.Date:MMM d, yyyy}{Environment.NewLine}{c.Count} Application{(c.Count==1?"":"s")}"
        }).ToList();

        ActivityPeakText.Text=peak.ToString();

        var empty=peak==0;
        ActivityChart.Visibility=empty?Visibility.Collapsed:Visibility.Visible;
        ActivityGuides.Visibility=empty?Visibility.Collapsed:Visibility.Visible;
        ActivityEmptyText.Visibility=empty?Visibility.Visible:Visibility.Collapsed;
    }

    // ---------- row and card actions ----------

    /// <summary>
    /// The job a menu item or in-row control belongs to. A context menu inside a template carries the
    /// row's own task as its DataContext, so this works for both the list and the board without
    /// depending on what happens to be selected.
    /// </summary>
    static JobTask? JobOf(object sender) => (sender as FrameworkElement)?.DataContext as JobTask;

    void TrackingSetStatus_Click(object s,RoutedEventArgs e) {
        if(s is not System.Windows.Controls.MenuItem item || item.Tag is not string status) return;
        ChangeStatus(JobOf(s) ?? TrackingGrid.SelectedItem as JobTask,status);
    }

    /// <summary>The in-row dropdown. Rebinding during a refresh must not be mistaken for a choice.</summary>
    void TrackingStatusBox_Changed(object s,System.Windows.Controls.SelectionChangedEventArgs e) {
        if(_refreshingTracking) return;
        if(s is not System.Windows.Controls.ComboBox box || box.SelectedItem is not string status) return;
        // A recycled row re-binds its dropdown to its new job's CURRENT status after a rebuild; that is
        // not a choice, and answering it would overwrite the real confirmation with "already <status>".
        if(JobOf(s) is not JobTask job || job.ApplicationStatus==status) return;
        ChangeStatus(job,status);
    }

    /// <summary>
    /// Mark Applied: the user's own record that they applied. Offered from Viewed and Ready only; on a
    /// job already Applied or later it says so instead of changing anything.
    /// </summary>
    void TrackingMarkApplied_Click(object s,RoutedEventArgs e) {
        var job=JobOf(s) ?? TrackingGrid.SelectedItem as JobTask;
        if(job is null){ TrackingStatus.Text="Select a job first."; return; }
        if(!JobTracker.CanMarkApplied(job)) {
            TrackingStatus.Text=$"{job.Company} — {job.Title} is already {job.ApplicationStatus}"+
                                (job.AppliedAt is DateTime at ? $" (applied {at:MMM d, yyyy})." : ".");
            return;
        }
        ChangeStatus(job,ApplicationStatus.Applied,j => JobTracker.MarkApplied(j));
    }

    /// <summary>
    /// Every status change from this window: apply, save the live list, rebuild the dashboard. The
    /// default change is JobTracker.UpdateStatus; Mark Applied passes JobTracker.MarkApplied.
    /// </summary>
    void ChangeStatus(JobTask? job,string status,Func<JobTask,bool>? change=null) {
        if(job is null){ TrackingStatus.Text="Select a job first."; return; }

        if(!(change ?? (j => JobTracker.UpdateStatus(j,status)))(job)) {
            TrackingStatus.Text=$"{job.Company} — {job.Title} is already {status}.";
            return;
        }

        JobTracker.SaveTrackingData(TrackedTasks);
        // Rebuilding the grid from inside a row's own event is deferred, so the control that raised it
        // is not torn down underneath the event.
        Dispatcher.BeginInvoke(new Action(RefreshTracking));
        TrackingStatus.Text=$"{job.Company} — {job.Title} is now {job.ApplicationStatus}. Saved.";
    }

    void TrackingGrid_DoubleClick(object s,System.Windows.Input.MouseButtonEventArgs e) =>
        OpenJobUrl(TrackingGrid.SelectedItem as JobTask);

    /// <summary>
    /// Widening the window can leave the grid scrolled to an offset that no longer exists, which
    /// shows as a strip of blank space on the right. The DataGrid owns horizontal scrolling on its
    /// own (the dashboard's ScrollViewer is vertical only), so clamping its offset here is enough.
    /// </summary>
    void TrackingGrid_SizeChanged(object s,SizeChangedEventArgs e) {
        if(!e.WidthChanged) return;

        var scroller=Descendant<System.Windows.Controls.ScrollViewer>(TrackingGrid);
        if(scroller is null) return;

        if(scroller.HorizontalOffset>scroller.ScrollableWidth)
            scroller.ScrollToHorizontalOffset(scroller.ScrollableWidth);
    }

    static T? Descendant<T>(DependencyObject root) where T : DependencyObject {
        for(var i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);i++) {
            var child=System.Windows.Media.VisualTreeHelper.GetChild(root,i);
            if(child is T match) return match;
            if(Descendant<T>(child) is T nested) return nested;
        }
        return null;
    }

    /// <summary>
    /// A DataGrid does not select on right-click, so the row under the cursor is selected first —
    /// otherwise the context menu would silently act on whatever was selected before.
    /// </summary>
    void TrackingGrid_RightButtonDown(object s,System.Windows.Input.MouseButtonEventArgs e) {
        for(var element=e.OriginalSource as DependencyObject; element is not null;
            element=System.Windows.Media.VisualTreeHelper.GetParent(element))
            if(element is System.Windows.Controls.DataGridRow row) { row.IsSelected=true; return; }
    }

    void TrackingOpenUrl_Click(object s,RoutedEventArgs e) =>
        OpenJobUrl(JobOf(s) ?? TrackingGrid.SelectedItem as JobTask);

    void OpenJobUrl(JobTask? job) {
        if(job is null){ TrackingStatus.Text="Select a job first."; return; }
        if(string.IsNullOrWhiteSpace(job.Link)) { TrackingStatus.Text=$"{job.Company} — {job.Title} has no job link."; return; }

        TrackingStatus.Text=JobTracker.OpenJobUrl(job)
            ? $"Opened the posting for {job.Company} — {job.Title} in your browser."
            : $"That job link could not be opened: {job.Link}";
    }

    /// <summary>
    /// Apply copies the folder of this application's linked DOCX (the stored resume path, not a
    /// company/title lookup), then opens the application page exactly as before. A missing DOCX
    /// copies nothing.
    /// </summary>
    void TrackingApply_Click(object s,RoutedEventArgs e) {
        var job=JobOf(s) ?? TrackingGrid.SelectedItem as JobTask;
        if(job is null){ TrackingStatus.Text="Select a job first."; return; }
        var copied=ResumeUpload.CopyResumePath(job,CopyText).Copied;
        switch(JobTracker.RouteApply(job)) {
            case ApplyRoute.OpenStored:
                TrackingStatus.Text=JobTracker.OpenApplyUrl(job)
                    ? $"Opened the application for {job.Company} — {job.Title} in your browser."
                    : "Unable to open application page.";
                break;
            case ApplyRoute.CaptureOnJobright:
                HostMain?.BeginApplyCapture(job);
                TrackingStatus.Text=$"Opening {job.Company} — {job.Title} in the Job Browser to capture the application link.";
                break;
            default:
                TrackingStatus.Text=$"No application link is recorded for {job.Company} — {job.Title}.";
                break;
        }
        if(copied) TrackingStatus.Text+=" Resume folder path copied";
    }

    /// <summary>Opens the recorded application page (ApplyUrl). The job posting stays on Open Job.</summary>
    void TrackingOpenApply_Click(object s,RoutedEventArgs e) {
        var job=JobOf(s) ?? TrackingGrid.SelectedItem as JobTask;
        if(job is null){ TrackingStatus.Text="Select a job first."; return; }
        if(JobTracker.ApplyUrlToOpen(job) is null) {
            TrackingStatus.Text=$"No application link is recorded for {job.Company} — {job.Title} yet. "+
                                "Click Apply on the job in the Job Browser to record it.";
            return;
        }

        TrackingStatus.Text=JobTracker.OpenApplyUrl(job)
            ? $"Opened the application for {job.Company} — {job.Title} in your browser."
            : "Unable to open application page.";
    }

    void TrackingOpenResume_Click(object s,RoutedEventArgs e) {
        var job=JobOf(s) ?? TrackingGrid.SelectedItem as JobTask;
        if(job is null){ TrackingStatus.Text="Select a job first."; return; }

        TrackingStatus.Text=JobTracker.OpenResume(job)
            ? $"Opened the resume for {job.Company} — {job.Title}."
            : $"No generated resume was found for {job.Company} — {job.Title}.";
    }

    void TrackingOpenResumeFolder_Click(object s,RoutedEventArgs e) {
        var job=JobOf(s) ?? TrackingGrid.SelectedItem as JobTask;
        if(job is null){ TrackingStatus.Text="Select a job first."; return; }

        TrackingStatus.Text=JobTracker.OpenResumeFolder(job)
            ? $"Opened the resume folder for {job.Company} — {job.Title}."
            : $"No resume folder was found for {job.Company} — {job.Title}.";
    }

    /// <summary>
    /// Copy Resume Path: the folder holding this job's exact generated DOCX (JobTask.ResumePath, the
    /// file Open Resume opens), as a plain absolute path for a job site's file picker. Nothing is
    /// copied without one.
    /// </summary>
    void TrackingCopyResumePath_Click(object s,RoutedEventArgs e) {
        var job=JobOf(s) ?? TrackingGrid.SelectedItem as JobTask;
        if(job is null){ TrackingStatus.Text="Select a job first."; return; }

        var copy=ResumeUpload.CopyResumePath(job,CopyText);
        TrackingStatus.Text=copy.Message;
        PerfLog.Line(copy.Copied ? "TRACKING copy resume path "+job.JobId : "TRACKING copy resume path "+job.JobId+" FAILED - "+copy.Message);
    }

    /// <summary>Copies the stable Documents\ResumeAutomation\CurrentResume.docx path (the newest resume).</summary>
    void TrackingCopyCurrentResumePath_Click(object s,RoutedEventArgs e) {
        var copy=ResumeUpload.CopyCurrentResumePath(ResumeUpload.CurrentResumePathFor(Storage.LoadSettings().ResumeRootFolder),CopyText);
        TrackingStatus.Text=copy.Message;
    }

    /// <summary>The one clipboard implementation, shaped for ResumeUpload.</summary>
    static (bool Success,string Message) CopyText(string text){
        var result=ClipboardService.SetText(text);
        return (result.Success,result.Message);
    }

    /// <summary>The prompt file Job Tasks and Email Tasks already send. Resume mode uses Master Prompt; Normal mode uses Normal Prompt.</summary>
    bool TailoringUsesNormalPrompt => PromptModes.IsNormal(_s.PromptMode);

    void LoadFields(){ ResumeBox.Text=_s.OriginalResume;
        PromptBox.Text=TailoringUsesNormalPrompt ? _s.NormalPrompt : _s.MasterPrompt;
        IncomingBox.Text=_s.IncomingFolder; ImportedBox.Text=_s.ImportedFolder; RootBox.Text=_s.ResumeRootFolder; DocxBox.IsChecked=_s.Docx; PdfBox.IsChecked=_s.Pdf; AutoFillBox.IsChecked=_s.AutoFillComposer; AutoCaptureBox.IsChecked=_s.AutoCaptureResult; AutoSendBox.IsChecked=_s.AutoSend; ReadyToastBox.IsChecked=_s.ReadyToast; ReadySoundBox.IsChecked=_s.ReadySound; ReadyFlashBox.IsChecked=_s.ReadyFlash; FocusHotkeyBox.IsChecked=_s.FocusHotkey;
        GptJobDelayBox.Text=_s.GptJobDelaySeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        RateLimitCooldownBox.Text=_s.RateLimitCooldownMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        LoadImportFilterSwitches();
        LoadEditableProfile();
        RefreshPromptAdaptStatus(); }
    string? PickFile(string filter){ var d=new Microsoft.Win32.OpenFileDialog{Filter=filter}; return d.ShowDialog()==true?d.FileName:null; }
    string? PickFolder(){ using var d=new Forms.FolderBrowserDialog(); return d.ShowDialog()==Forms.DialogResult.OK?d.SelectedPath:null; }
    async void ResumeBrowse_Click(object s,RoutedEventArgs e) {
        var path = PickFile("Word resume|*.docx");
        if (path is null) return;
        if (!string.Equals(System.IO.Path.GetExtension(path), ".docx", StringComparison.OrdinalIgnoreCase)) {
            System.Windows.MessageBox.Show("Choose a .docx resume.");
            return;
        }
        if (HostMain is not MainWindow main) {
            System.Windows.MessageBox.Show("Open Settings from Resume Builder to rebuild the candidate profile.");
            return;
        }
        if (!main.CanStartBaselineProfile(out var busy)) {
            System.Windows.MessageBox.Show(busy);
            return;
        }

        ResumeBrowseButton.IsEnabled = false;
        try {
            ProfileWorkflowStatus.Text = "Importing resume…";
            _s.OriginalResume = path;
            ResumeBox.Text = path;
            if (!Storage.SaveSettings(_s)) {
                ResumeBrowseButton.IsEnabled = true;
                ProfileWorkflowStatus.Text = "The resume path was not saved. " + (Storage.WriteBlockedReason(Storage.SettingsPath) ?? "settings.json is protected this session.");
                return;
            }
            try { HtmlTailor.RememberOriginal(_s); }
            catch (Exception htmlEx) { PerfLog.Line("HTML source-html-failed " + htmlEx.GetType().Name); }

            BaselineProfileImporter.CreateBaselineFromDocx(path);
            var request = BaselineProfileImporter.BuildProfileCreationRequest(_s.MasterPrompt);
            var prepared = new PreparedRequest {
                JobId = ResultCapture.BaselineJobId,
                Company = "Candidate profile",
                Title = "Initial Candidate Profile",
                PromptMode = PromptModes.Resume,
                Text = request
            };
            RequestPreparation.Save(prepared);
            ProfileWorkflowStatus.Text = "Generating candidate profile…";
            await main.StartBaselineProfileAsync(prepared);
        } catch (Exception ex) {
            ResumeBrowseButton.IsEnabled = true;
            PerfLog.Line("PROFILE workflow failed " + ex.GetType().Name);
            ProfileWorkflowStatus.Text = "The candidate profile was not updated. Try again.";
            System.Windows.MessageBox.Show("The candidate profile was not updated. The previous profile is unchanged.", "Resume Builder");
        }
    }

    void PromptBrowse_Click(object s,RoutedEventArgs e){
        var p=PickFile("Text files|*.txt;*.md|All files|*.*");
        if(p==null) return;
        PromptBox.Text=p;
        SaveSettingsFromFields(announce:false);
        var saved=TailoringUsesNormalPrompt ? _s.NormalPrompt : _s.MasterPrompt;
        if(string.Equals(saved, p, StringComparison.OrdinalIgnoreCase))
            _=AdaptSelectedPromptAsync();
    }

    void RefreshPromptAdaptStatus() {
        var path = PromptBox.Text.Trim();
        if (File.Exists(path) && PromptConversion.Matches(path))
            PromptAdaptStatus.Text = PromptConversion.ReadyStatus;
        else if (PromptAdaptStatus.Text != PromptConversion.PreparingStatus
                 && PromptAdaptStatus.Text != PromptConversion.FailedStatus)
            PromptAdaptStatus.Text = "";
    }

    async Task AdaptSelectedPromptAsync() {
        var path = PromptBox.Text.Trim();
        if (!File.Exists(path)) {
            PromptAdaptStatus.Text = "";
            return;
        }
        if (PromptConversion.Matches(path)) {
            PromptAdaptStatus.Text = PromptConversion.ReadyStatus;
            return;
        }
        if (HostMain is not MainWindow main) {
            PromptAdaptStatus.Text = PromptConversion.FailedStatus;
            ProfileLoadStatus.Visibility = Visibility.Visible;
            ProfileLoadStatus.Text = "Open Settings from Resume Builder to prepare the Tailoring Prompt. The previous prepared prompt was kept.";
            return;
        }
        PromptAdaptStatus.Text = PromptConversion.PreparingStatus;
        var (ok, detail) = await main.AdaptTailoringPromptAsync(path, resumeMode: !TailoringUsesNormalPrompt);
        if (detail == "in-progress") return;
        PromptAdaptStatus.Text = ok ? PromptConversion.ReadyStatus : PromptConversion.FailedStatus;
        if (!ok) {
            ProfileLoadStatus.Visibility = Visibility.Visible;
            ProfileLoadStatus.Text = detail;
        }
    }

    void IncomingBrowse_Click(object s,RoutedEventArgs e){var p=PickFolder();if(p!=null)IncomingBox.Text=p;}
    void ImportedBrowse_Click(object s,RoutedEventArgs e){var p=PickFolder();if(p!=null)ImportedBox.Text=p;}
    void RootBrowse_Click(object s,RoutedEventArgs e){var p=PickFolder();if(p!=null)RootBox.Text=p;}
    void Save_Click(object s,RoutedEventArgs e){
        SaveSettingsFromFields(announce:true);
        var path=PromptBox.Text.Trim();
        var saved=TailoringUsesNormalPrompt ? _s.NormalPrompt : _s.MasterPrompt;
        if(File.Exists(path) && string.Equals(saved, path, StringComparison.OrdinalIgnoreCase))
            _=AdaptSelectedPromptAsync();
    }

    /// <summary>
    /// Builds one settings object from every field on the page and saves it. The import filters are
    /// part of that object, so saving any page carries the current filter values instead of the
    /// defaults — the AppSettings here is rebuilt from scratch, and a field left out would be reset.
    /// </summary>
    void SaveSettingsFromFields(bool announce){
        if(!string.IsNullOrWhiteSpace(IncomingBox.Text)&&!Directory.Exists(IncomingBox.Text)){System.Windows.MessageBox.Show("Incoming folder does not exist.");return;}
        if(!string.IsNullOrWhiteSpace(ImportedBox.Text)&&!Directory.Exists(ImportedBox.Text)){System.Windows.MessageBox.Show("Imported folder does not exist.");return;}
        var promptPath=PromptBox.Text.Trim();
        if(TailoringUsesNormalPrompt&&!File.Exists(promptPath)){
            System.Windows.MessageBox.Show("Choose an existing Tailoring Prompt file."); return; }
        // ChatGPT pacing: refused rather than guessed — invalid text is never saved as 0.
        if(RateLimit.ValidateJobDelay(GptJobDelayBox.Text,out var jobDelaySeconds) is string delayError){System.Windows.MessageBox.Show(delayError);return;}
        if(RateLimit.ValidateCooldown(RateLimitCooldownBox.Text,out var cooldownMinutes) is string cooldownError){System.Windows.MessageBox.Show(cooldownError);return;}
        var saved=new AppSettings{OriginalResume=ResumeBox.Text.Trim(),
                 StyleReferenceResume=_s.StyleReferenceResume,
                 CandidateProfile=_s.CandidateProfile,
                 MasterPrompt=TailoringUsesNormalPrompt ? _s.MasterPrompt : promptPath,
                 PromptMode=_s.PromptMode,
                 NormalPrompt=TailoringUsesNormalPrompt ? promptPath : _s.NormalPrompt,
                 IncomingFolder=IncomingBox.Text.Trim(),ImportedFolder=ImportedBox.Text.Trim(),ResumeRootFolder=RootBox.Text.Trim(),Docx=DocxBox.IsChecked==true,Pdf=PdfBox.IsChecked==true,AutoFillComposer=AutoFillBox.IsChecked==true,AutoCaptureResult=AutoCaptureBox.IsChecked==true,AutoSend=AutoSendBox.IsChecked==true,ReadyToast=ReadyToastBox.IsChecked==true,ReadySound=ReadySoundBox.IsChecked==true,ReadyFlash=ReadyFlashBox.IsChecked==true,FocusHotkey=FocusHotkeyBox.IsChecked==true};
        ApplyImportFilterSwitches(saved);
        saved.GptJobDelaySeconds=jobDelaySeconds;
        saved.RateLimitCooldownMinutes=cooldownMinutes;
        _s=saved;
        // Refused only when settings.json could not be read this session (its data is kept safe).
        var written=Storage.SaveSettings(_s);
        if (written) {
            try { HtmlTailor.RememberOriginal(saved); }
            catch (Exception htmlEx) { PerfLog.Line("HTML source-html-failed " + htmlEx.GetType().Name); }
        }

        // The Job Browser toolbar shows the same five values; keep it in step without a reopen.
        HostMain?.RefreshJobImportFilters();

        if(!written) System.Windows.MessageBox.Show("Settings were NOT saved: "+(Storage.WriteBlockedReason(Storage.SettingsPath) ?? "settings.json is protected this session."));
        else if(announce) System.Windows.MessageBox.Show("Settings saved.");
    }

    void LoadSampleJobs_Click(object s, RoutedEventArgs e) =>
        WriteJobs(SampleJobs.CreateSampleJobs(), "sample-job", "3 sample jobs created in Incoming, one file each.");

    /// <summary>50 synthetic jobs for queue stress testing. Nothing is written until this is clicked.</summary>
    void LoadStressJobs_Click(object s, RoutedEventArgs e) =>
        WriteJobs(SampleJobs.CreateStressJobs(50), "stress-job",
            "50 synthetic jobs created in Incoming, one file each. They use invented companies and job descriptions.");

    /// <summary>One file per job, because the importer takes exactly one job per file.</summary>
    void WriteJobs(List<JobImportData> jobs, string filePrefix, string message) {
        var settings=Storage.LoadSettings();
        if(string.IsNullOrWhiteSpace(settings.IncomingFolder)||!Directory.Exists(settings.IncomingFolder)){System.Windows.MessageBox.Show("Configure an existing Incoming folder first.");return;}
        var stamp=DateTime.Now.ToString("yyyyMMdd-HHmmssfff");
        var options=new System.Text.Json.JsonSerializerOptions{WriteIndented=true};
        for(var i=0;i<jobs.Count;i++)
            File.WriteAllText(Path.Combine(settings.IncomingFolder,$"{filePrefix}-{stamp}-{i+1:D3}.json"),
                              System.Text.Json.JsonSerializer.Serialize(jobs[i],options));
        System.Windows.MessageBox.Show(message+Environment.NewLine+Environment.NewLine+"Click Refresh Input on the main screen to import them.");
    }

    /// <summary>The narrow one: synthetic sample/stress jobs only. Kept distinct from Clear Job History.</summary>
    void ClearTestQueue_Click(object s, RoutedEventArgs e) {
        var tasks=Storage.LoadTasks(); var n=tasks.RemoveAll(SampleJobs.IsTestJob); Storage.SaveTasks(tasks);
        System.Windows.MessageBox.Show($"Removed {n} sample/stress task(s)."+
            (HostMain is not null ? " Click Refresh Input on the main screen to reload the queue." : ""));
    }

    /// <summary>
    /// Clear Job History: plan -> confirm -> delete artifacts -> clear the live list -> save []. The
    /// plan is built first and is exactly what the confirmation describes. Nothing outside Resume
    /// Builder's own folders is ever in it (JobHistoryReset).
    /// </summary>
    void ClearJobHistory_Click(object s, RoutedEventArgs e) {
        var main=HostMain;
        var tasks=(main?.Tasks ?? Storage.LoadTasks()).ToList();

        if(main?.IsQueueRunning==true || JobHistoryReset.IsProcessing(tasks)) {
            System.Windows.MessageBox.Show(
                "The queue is still running, or a job is being processed.\n\nStop the queue and let the current job finish, then try again. Nothing was changed.",
                "Clear Job History");
            return;
        }
        if(tasks.Count==0) {
            System.Windows.MessageBox.Show("There are no jobs to clear.","Clear Job History");
            return;
        }

        var withDocuments=ClearDocumentsBox.IsChecked==true;
        var plan=JobHistoryReset.Plan(tasks,Storage.LoadSettings(),withDocuments);

        var message=$"This will permanently remove:\n\n"+
                    $"    • {plan.JobSummary()}\n"+
                    $"    • {plan.Files.Count} saved result and prepared-request file(s)\n"+
                    (withDocuments
                        ? $"    • {plan.Folders.Count} generated resume folder(s) (DOCX/PDF)\n"
                        : "\nGenerated resume documents are NOT included.\n")+
                    "\nYour settings, prompt files, candidate profile, Job Browser sign-in, diagnostics log and Incoming/Imported files are not touched.\n\n"+
                    "This cannot be undone. Continue?";

        if(System.Windows.MessageBox.Show(message,"Clear all job history?",MessageBoxButton.YesNo,
                                          MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes) return;

        // Deleting documents reaches into a folder of the user's own, so it is confirmed separately.
        if(withDocuments && plan.Folders.Count>0 &&
           System.Windows.MessageBox.Show(
               $"{plan.Folders.Count} resume folder(s) will be deleted from:\n\n    {plan.ResumeRoot}\n\n"+
               "Only folders whose own resume-info.json matches one of these jobs are removed. Continue?",
               "Delete generated resumes?",MessageBoxButton.YesNo,MessageBoxImage.Warning,
               MessageBoxResult.No)!=MessageBoxResult.Yes) return;

        var report=JobHistoryReset.Execute(plan);

        // Only now is the live list emptied and tasks.json saved.
        if(main is not null) main.ClearJobHistoryInPlace();
        else { _standaloneTasks=new List<JobTask>(); Storage.SaveTasks(_standaloneTasks); }

        RefreshTracking();
        RefreshInspector();
        TrackingStatus.Text=$"Cleared {plan.JobCount} job(s). "+report.Describe();
        PerfLog.Line($"RESET cleared {plan.JobCount} jobs, {report.FilesDeleted} files, "+
                     $"{report.FoldersDeleted} folders, {report.Failures.Count} failure(s)");

        System.Windows.MessageBox.Show(
            $"Cleared {plan.JobCount} job(s).\n\n{report.Describe()}",
            report.AnyFailure ? "Cleared, with problems" : "Job history cleared",
            MessageBoxButton.OK, report.AnyFailure ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }


    public void RefreshInspector() { }

    /// <summary>
    /// Shell mode: Applications-only hides the settings section nav; Settings shows it.
    /// Still one live control and the same TrackedTasks from MainWindow.
    /// </summary>
    public void SetShellMode(bool applicationsFocus) {
        if (SectionNavPanel is not null)
            SectionNavPanel.Visibility = applicationsFocus ? Visibility.Collapsed : Visibility.Visible;
        if (SectionNavColumn is not null)
            SectionNavColumn.Width = applicationsFocus ? new GridLength(0) : new GridLength(200);
        if (applicationsFocus) ShowSection("Applications");
        else ShowSection("Candidate Profile");
    }

    void SectionNav_Checked(object sender, RoutedEventArgs e) {
        if (sender is System.Windows.Controls.RadioButton { IsChecked: true, Tag: string tag })
            ShowSection(tag);
    }

    /// <summary>Selects a settings tab by header text.</summary>
    public void ShowSection(string header) {
        if (SettingsTabs is null) return;
        if (string.Equals(header, "Applications", StringComparison.OrdinalIgnoreCase))
            PerfLog.Line("APPLICATIONS PERF show-section");
        foreach (System.Windows.Controls.TabItem tab in SettingsTabs.Items)
            if (string.Equals(tab.Header as string, header, StringComparison.OrdinalIgnoreCase)) {
                SettingsTabs.SelectedItem = tab;
                if (string.Equals(header, "Applications", StringComparison.OrdinalIgnoreCase))
                    RefreshTracking();
                // Sync left nav radio without re-entrancy loops
                if (SectionNavPanel?.Visibility == Visibility.Visible)
                    foreach (var child in FindSectionRadios())
                        if (string.Equals(child.Tag as string, header, StringComparison.OrdinalIgnoreCase))
                            child.IsChecked = true;
                return;
            }
    }

    IEnumerable<System.Windows.Controls.RadioButton> FindSectionRadios() {
        if (SectionNavPanel is null) yield break;
        foreach (var rb in FindVisualChildren<System.Windows.Controls.RadioButton>(SectionNavPanel))
            if (rb.GroupName == "SettingsSection") yield return rb;
    }

    static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject {
        if (parent is null) yield break;
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++) {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
        }
    }

    public void ShowBaselineResult(bool saved, string detail) {
        ResumeBrowseButton.IsEnabled = true;
        LoadEditableProfile();
        ProfileWorkflowStatus.Text = saved
            ? "Candidate profile updated."
            : "Candidate profile was not updated. " + detail;
    }

    void LoadEditableProfile() {
        var fields = CandidateProfileStore.LoadEditableFields(out var error);
        if (fields is null) {
            ProfileEditorPanel.Visibility = Visibility.Collapsed;
            ProfileReviewPanel.Visibility = Visibility.Collapsed;
            ProfileLoadStatus.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
            ProfileLoadStatus.Text = error is null ? "" : "The candidate profile could not be opened for editing. The file was not changed. " + error;
            return;
        }
        ProfileLoadStatus.Visibility = Visibility.Collapsed;
        ProfileEditorPanel.Visibility = Visibility.Visible;
        ProfileNameBox.Text = fields.Name;
        ProfileTitleBox.Text = fields.Title;
        ProfileLocationBox.Text = fields.Location;
        ProfileEmailBox.Text = fields.Email;
        ProfilePhoneBox.Text = fields.Phone;
        ProfileLinkedinBox.Text = fields.Linkedin;
        ProfileSummaryBox.Text = fields.Summary;
        try {
            var review = CandidateProfileStore.LoadReviewText();
            ProfileSkillsReview.Text = review.Skills;
            ProfileExperienceReview.Text = review.Experience;
            ProfileEducationReview.Text = review.Education;
            ProfileCertificationsReview.Text = review.Certifications;
            ProfileReviewPanel.Visibility = Visibility.Visible;
        } catch (Exception ex) {
            ProfileReviewPanel.Visibility = Visibility.Collapsed;
            ProfileLoadStatus.Visibility = Visibility.Visible;
            PerfLog.Line("PROFILE review failed " + ex.GetType().Name);
            ProfileLoadStatus.Text = "The profile review could not be shown. The file was not changed.";
        }
    }

    void SaveCandidateDetails_Click(object sender, RoutedEventArgs e) {
        try {
            CandidateProfileStore.SaveEditableFields(new EditableProfileFields {
                Name = ProfileNameBox.Text,
                Title = ProfileTitleBox.Text,
                Location = ProfileLocationBox.Text,
                Email = ProfileEmailBox.Text,
                Phone = ProfilePhoneBox.Text,
                Linkedin = ProfileLinkedinBox.Text,
                Summary = ProfileSummaryBox.Text
            });
            System.Windows.MessageBox.Show("Candidate profile saved.");
        } catch (Exception ex) {
            PerfLog.Line("PROFILE save failed " + ex.GetType().Name);
            System.Windows.MessageBox.Show(
                "The candidate profile was not saved. The existing file is unchanged.");
        }
    }

}
/// <summary>
/// Display text for an application platform badge. Goes through JobTracker.PlatformDisplayName
/// ("iCIMS", not the enum's ICims) so the UI never shows raw enum names. Display only; one-way.
/// </summary>
public sealed class PlatformDisplayConverter : System.Windows.Data.IValueConverter {
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        value is ApplicationPlatform platform ? JobTracker.PlatformDisplayName(platform) : "";

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}

/// <summary>
/// True when an address may be opened (JobTracker.IsOpenableUrl: absolute http/https).
/// Display only; one-way. The Apply button uses CanApply, not this converter.
/// </summary>
public sealed class OpenableUrlConverter : System.Windows.Data.IValueConverter {
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        JobTracker.IsOpenableUrl(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}

/// <summary>Enables Mark Applied from the row's ApplicationStatus: Viewed or Ready only. One-way.</summary>
public sealed class CanMarkAppliedConverter : System.Windows.Data.IValueConverter {
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        JobTracker.CanMarkApplied(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}

/// <summary>
/// A job's date text for display: ConverterParameter "Stages" gives every recorded stage date (the
/// List's Date tooltip); anything else gives the Board card date ("Applied Sep 18" once applied). One-way.
/// </summary>
public sealed class JobDateTextConverter : System.Windows.Data.IValueConverter {
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        value is not JobTask job ? ""
        : parameter as string == "Stages" ? JobTracker.StageDatesText(job)
        : JobTracker.BoardDateText(job);

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}
