using System.IO;
using System.Windows;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
namespace ResumeBuilder;

public partial class SettingsWindow : Window {
    AppSettings _s=Storage.LoadSettings();
    public SettingsWindow(){ InitializeComponent(); LoadFields(); RefreshInspector(); InitTracking(); }

    // ---------- job application tracking dashboard ----------
    //
    // The views edit the live JobTask objects from MainWindow whenever this window has one as its
    // owner, so a status change here and a queue change there can never overwrite each other. Every
    // calculation lives in JobTracker; this is only the screen.

    /// <summary>The tasks being tracked: the main window's live list, or the saved file if standalone.</summary>
    IReadOnlyList<JobTask> TrackedTasks => (Owner as MainWindow)?.Tasks ?? (_standaloneTasks ??= Storage.LoadTasks());
    List<JobTask>? _standaloneTasks;

    /// <summary>Set while the dashboard is rebuilding, so rebinding a row does not look like an edit.</summary>
    bool _refreshingTracking;

    bool _boardView;

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

    /// <summary>One stage box in the pipeline strip.</summary>
    sealed class PipelineBox {
        public string Status { get; init; } = "";
        public int Count { get; init; }
        public string Conversion { get; init; } = "";
        public string Tooltip { get; init; } = "";
        public Visibility ConversionVisibility { get; init; }
        public Visibility ArrowVisibility { get; init; }
        public System.Windows.Media.Geometry? Icon { get; init; }
        public System.Windows.Media.Brush Background { get; init; } = System.Windows.Media.Brushes.Gainsboro;
        public System.Windows.Media.Brush Foreground { get; init; } = System.Windows.Media.Brushes.Black;
    }

    /// <summary>One Kanban column.</summary>
    sealed class BoardColumn {
        public string Status { get; init; } = "";
        public List<JobTask> Jobs { get; init; } = new();
        public int Count => Jobs.Count;
        public Visibility EmptyVisibility => Jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        public System.Windows.Media.Geometry? Icon { get; init; }
        public System.Windows.Media.Brush Background { get; init; } = System.Windows.Media.Brushes.Gainsboro;
        public System.Windows.Media.Brush Foreground { get; init; } = System.Windows.Media.Brushes.Black;
    }

    const string ActivityLast7="7 Days", ActivityLast30="30 Days", ActivityAllTime="All Time";

    /// <summary>
    /// The badge colours for a status, taken from the window's resources so XAML stays the single
    /// source of truth. A missing key falls back rather than throwing the dashboard away.
    /// </summary>
    System.Windows.Media.Brush StatusBrush(string status,string suffix) =>
        TryFindResource("Status"+status+suffix) as System.Windows.Media.Brush
        ?? TryFindResource("StatusViewed"+suffix) as System.Windows.Media.Brush
        ?? System.Windows.Media.Brushes.Gray;

    /// <summary>The vector icon for a status, from the same resources the list badge uses.</summary>
    System.Windows.Media.Geometry? StatusIcon(string status) =>
        TryFindResource("Icon"+status) as System.Windows.Media.Geometry;

    /// <summary>The exact day picked from the calendar, or null when a quick range is in force.</summary>
    DateTime? _exactDate;

    /// <summary>The quick range currently chosen. Ignored while an exact date is set.</summary>
    string _dateFilter=DateFilter.AllDates;

    /// <summary>
    /// The ticked application platforms and readiness states. Empty means all. They live only in this
    /// window: never saved to tasks.json, settings or a task.
    /// </summary>
    MultiSelectFilter<ApplicationPlatform>? _platformFilter;
    MultiSelectFilter<ApplicationReadiness>? _readinessFilter;

    void InitTracking() {
        // All, the filter-only "Not applied yet" group, then the five real statuses.
        foreach(var filter in ApplicationStatus.Filter.Options) TrackingFilterBox.Items.Add(filter);
        _platformFilter=new(PlatformFilterButton,PlatformFilterPopup,PlatformCheckList,PlatformClearButton,
                            JobTracker.PlatformFilterOrder,JobTracker.PlatformDisplayName,
                            JobTracker.PlatformFilterLabel,RefreshTracking);
        _readinessFilter=new(ReadinessFilterButton,ReadinessFilterPopup,ReadinessCheckList,ReadinessClearButton,
                             JobTracker.ReadinessFilterOrder,JobTracker.ReadinessText,
                             JobTracker.ReadinessFilterLabel,RefreshTracking);
        foreach(var range in DateFilter.Options) QuickDateList.Items.Add(range);
        foreach(var range in new[]{ActivityLast7,ActivityLast30,ActivityAllTime}) ActivityRangeBox.Items.Add(range);

        _refreshingTracking=true;
        TrackingFilterBox.SelectedIndex=0;      // All
        ActivityRangeBox.SelectedIndex=1;       // 30 days
        ListToggle.IsChecked=true;              // the Checked handler is inert while refreshing
        _refreshingTracking=false;

        // The flow graph is drawn against the canvas's real width, so it is rebuilt when that changes.
        FlowCanvas.SizeChanged+=(_,_) => { if(_boardView) BuildFlowGraph(TrackedTasks); };

        UpdateViewToggle();
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

        public MultiSelectFilter(System.Windows.Controls.Primitives.ToggleButton button,
                                 System.Windows.Controls.Primitives.Popup popup,
                                 System.Windows.Controls.Panel list, System.Windows.Controls.Button clear,
                                 IEnumerable<T> order, Func<T,string> displayName,
                                 Func<IReadOnlyCollection<T>,string> label, Action changed) {
            _button=button; _list=list; _label=label; _changed=changed;

            foreach(var choice in order) {
                var box=new System.Windows.Controls.CheckBox {
                    Content=displayName(choice), Tag=choice, FontSize=11.5, Margin=new Thickness(0,3,0,3)
                };
                box.Checked+=Check_Changed;
                box.Unchecked+=Check_Changed;
                list.Children.Add(box);
            }
            button.Click+=(_,_) => popup.IsOpen=button.IsChecked==true;
            popup.Closed+=(_,_) => button.IsChecked=false;
            clear.Click+=(_,_) => Clear();
            button.Content=label(_selected);
        }

        void Check_Changed(object s,RoutedEventArgs e) {
            if(_setting || s is not System.Windows.Controls.CheckBox { Tag: T choice } box) return;
            if(box.IsChecked==true) _selected.Add(choice); else _selected.Remove(choice);
            _button.Content=_label(_selected);
            _changed();
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
            _selected.Clear();
            foreach(var box in _list.Children.OfType<System.Windows.Controls.CheckBox>())
                if(box.IsChecked==true && box.Tag is T choice) _selected.Add(choice);   // only real choices
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

    /// <summary>
    /// The "Ready to apply" card: show exactly the jobs it counts — the action queue. Switches to List,
    /// clears search, platforms and date, sets Status to the "Not applied yet" group and Readiness to
    /// only Ready to apply, then refreshes once. Both filters stay visible, so the user can undo them.
    /// The controls' own change handlers are inert while _refreshingTracking is set.
    /// </summary>
    void ReadyToApplyCard_Click(object s,RoutedEventArgs e) {
        _refreshingTracking=true;
        try {
            ListToggle.IsChecked=true;
            _boardView=false; UpdateViewToggle();
            TrackingSearchBox.Text="";
            TrackingFilterBox.SelectedItem=ApplicationStatus.Filter.NotAppliedYet;
            _dateFilter=DateFilter.AllDates;
            _exactDate=null;
            QuickDateList.SelectedItem=null;
            DateFilterCalendar.SelectedDate=null;
            DateFilterButton.Content=DateFilter.AllDates;
            _platformFilter?.SelectOnly(Array.Empty<ApplicationPlatform>(),refresh:false);
            _readinessFilter?.SelectOnly(new[]{ApplicationReadiness.ReadyToApply},refresh:false);
        } finally {
            _refreshingTracking=false;
        }
        RefreshTracking();
        TrackingStatus.Text=$"Showing the {ReadyToApplyCountText.Text} job(s) ready to apply and not applied for yet.";
    }

    void TrackingSearch_Changed(object s,System.Windows.Controls.TextChangedEventArgs e) {
        if(!_refreshingTracking) RefreshTracking();
    }

    void TrackingRefresh_Click(object s,RoutedEventArgs e) {
        _standaloneTasks=null;                  // re-read from disk when there is no main window
        RefreshTracking();
    }

    void TrackingListView_Click(object s,RoutedEventArgs e) {
        _boardView=false; UpdateViewToggle();
        if(!_refreshingTracking) RefreshTracking();
    }

    void TrackingBoardView_Click(object s,RoutedEventArgs e) {
        _boardView=true; UpdateViewToggle();
        if(!_refreshingTracking) RefreshTracking();
    }

    /// <summary>
    /// List and Board swap both the table and the graph above it: the daily activity chart belongs to
    /// the list, the pipeline flow graph to the board. The stage strip is hidden in board mode because
    /// the flow graph already shows those counts.
    /// </summary>
    void UpdateViewToggle() {
        if(ListViewCard is null) return;
        ListViewCard.Visibility=_boardView?Visibility.Collapsed:Visibility.Visible;
        BoardViewCard.Visibility=_boardView?Visibility.Visible:Visibility.Collapsed;
        ActivityCard.Visibility=_boardView?Visibility.Collapsed:Visibility.Visible;
        FlowCard.Visibility=_boardView?Visibility.Visible:Visibility.Collapsed;
        PipelineView.Visibility=_boardView?Visibility.Collapsed:Visibility.Visible;
        PipelineHeader.Visibility=_boardView?Visibility.Collapsed:Visibility.Visible;
    }

    /// <summary>Rebuilds every part of the dashboard from the current tasks and filters.</summary>
    public void RefreshTracking() {
        if(TrackingGrid is null || _refreshingTracking) return;
        _refreshingTracking=true;
        try {
            var tasks=TrackedTasks;

            // Reconnect any job whose document exists but whose path predates tracking, so its
            // Resume actions work instead of silently doing nothing.
            if(JobTracker.RelinkResumes(tasks,Storage.LoadSettings().ResumeRootFolder)>0)
                JobTracker.SaveTrackingData(tasks);

            var filtered=JobTracker.ApplyFilters(tasks,TrackingSearchBox.Text,
                                                 TrackingFilterBox.SelectedItem as string,
                                                 _dateFilter,null,_exactDate,
                                                 _platformFilter?.Selected,_readinessFilter?.Selected);

            var selected=TrackingGrid.SelectedItem as JobTask;
            TrackingGrid.ItemsSource=filtered;
            if(selected is not null && filtered.Contains(selected)) TrackingGrid.SelectedItem=selected;

            // Empty states tell the two cases apart: nothing tracked at all, or nothing matching.
            ListEmptyText.Visibility=filtered.Count==0?Visibility.Visible:Visibility.Collapsed;
            ListEmptyText.Text=tasks.Count==0
                ? "No applications found."
                : "No applications match the current filters.";

            BoardView.ItemsSource=ApplicationStatus.Ordered.Select(status => new BoardColumn{
                Status=status,
                Jobs=JobTracker.GetTasksByStatus(filtered,status),
                Icon=StatusIcon(status),
                Background=StatusBrush(status,"Bg"),
                Foreground=StatusBrush(status,"Fg")
            }).ToList();

            // The summary cards and the pipeline describe everything, not the current filter.
            var activity=JobTracker.GetApplicationActivity(tasks);
            TodayCountText.Text=activity.Today.ToString();
            MonthCountText.Text=activity.Last30Days.ToString();
            TotalCountText.Text=activity.Total.ToString();
            ReadyToApplyCountText.Text=JobTracker.CountNeedsAction(tasks).ToString();

            var stats=JobTracker.GetStatistics(tasks);
            var stages=JobTracker.GetPipelineCounts(tasks);
            PipelineView.ItemsSource=stages.Select((stage,index) => {
                // Under each stage: the previous stage's share of the two. The first has no previous.
                var previous=index==0?0:stages[index-1].Count;
                return new PipelineBox{
                    Status=stage.Status, Count=stage.Count,
                    Conversion=previous+stage.Count==0?"":JobStatistics.WholePercent(JobStatistics.PairShare(previous,stage.Count))+" of pair",
                    ConversionVisibility=index>0 && previous+stage.Count>0?Visibility.Visible:Visibility.Collapsed,
                    ArrowVisibility=index<ApplicationStatus.Ordered.Length-1?Visibility.Visible:Visibility.Collapsed,
                    Tooltip=StageTooltip(stages,index),
                    Icon=StatusIcon(stage.Status),
                    Background=StatusBrush(stage.Status,"Bg"),
                    Foreground=StatusBrush(stage.Status,"Fg")
                };
            }).ToList();

            TrackingCountsText.Text=
                $"Total jobs: {stats.Total}    Viewed: {stats.Viewed}    Ready: {stats.Ready}    "+
                $"Applied: {stats.Applied}    Interview: {stats.Interview}    Done: {stats.Done}";
            TrackingRatesText.Text=
                $"Applied rate: {JobStatistics.Percent(stats.AppliedRate)} ({stats.AppliedOrLater}/{stats.Total})    "+
                $"Interview rate: {JobStatistics.Percent(stats.InterviewRate)} ({stats.InterviewOrLater}/{stats.AppliedOrLater})    "+
                $"Completion rate: {JobStatistics.Percent(stats.CompletionRate)} ({stats.Done}/{stats.InterviewOrLater})";

            if(_boardView) BuildFlowGraph(tasks);
            else RefreshActivityChart(tasks);

            PerfLog.Line($"TRACKING dashboard refreshed total={stats.Total} applied={stats.Applied} "+
                         $"interview={stats.Interview} done={stats.Done}");
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

    /// <summary>
    /// "Applied / 30 jobs / 60% Ready vs 40% Applied" — the pair share, spelled out for both sides so
    /// the percentage cannot be mistaken for a conversion rate. Shared by the pipeline and the graph.
    /// </summary>
    static string StageTooltip(IReadOnlyList<PipelineStage> stages,int index) {
        var stage=stages[index];
        var text=$"{stage.Status}{Environment.NewLine}{stage.Count} job{(stage.Count==1?"":"s")}";

        if(index==0) return text;
        var previous=stages[index-1];
        if(previous.Count+stage.Count==0) return text;

        return text+Environment.NewLine+JobStatistics.PairSplit(previous.Status,previous.Count,stage.Status,stage.Count);
    }

    /// <summary>
    /// The board's graph: five bands whose height is proportional to the stage count, joined by
    /// tapering connectors, so the drop-off from stage to stage is visible at a glance. Plain WPF
    /// shapes on a Canvas — no charting dependency.
    /// </summary>
    void BuildFlowGraph(IReadOnlyList<JobTask> tasks) {
        if(FlowCanvas is null) return;
        FlowCanvas.Children.Clear();

        var stages=JobTracker.GetPipelineCounts(tasks);
        var peak=stages.Max(s => s.Count);

        FlowEmptyText.Visibility=peak==0?Visibility.Visible:Visibility.Collapsed;
        if(peak==0) return;

        var width=FlowCanvas.ActualWidth;
        var height=FlowCanvas.ActualHeight;
        if(width<60 || height<60) return;          // not laid out yet; SizeChanged will call back

        const double NodeWidth=70, LabelHeight=44, MinBand=5;
        var plotHeight=Math.Max(30,height-LabelHeight);
        var gap=stages.Count>1?(width-stages.Count*NodeWidth)/(stages.Count-1):0;
        if(gap<8) return;                           // too narrow to draw honestly

        // Band geometry first, so the connectors can be drawn underneath the nodes.
        var tops=new double[stages.Count];
        var heights=new double[stages.Count];
        var lefts=new double[stages.Count];
        for(var i=0;i<stages.Count;i++) {
            heights[i]=Math.Max(MinBand,stages[i].Count/(double)peak*plotHeight);
            tops[i]=(plotHeight-heights[i])/2;
            lefts[i]=i*(NodeWidth+gap);
        }

        // A transparent full-height target per stage, added first so it sits behind everything. A
        // stage with one job is a five-pixel band; without this, its statistics would be unhoverable.
        for(var i=0;i<stages.Count;i++) {
            var target=new System.Windows.Controls.Border {
                Width=NodeWidth+gap*0.6,
                Height=height,
                Background=System.Windows.Media.Brushes.Transparent,
                ToolTip=StageTooltip(stages,i)
            };
            System.Windows.Controls.Canvas.SetLeft(target,lefts[i]-(target.Width-NodeWidth)/2);
            System.Windows.Controls.Canvas.SetTop(target,0);
            FlowCanvas.Children.Add(target);
        }

        for(var i=0;i<stages.Count-1;i++) {
            var flow=new System.Windows.Shapes.Polygon {
                Fill=StatusBrush(stages[i].Status,"Bg"),
                Opacity=0.85,
                ToolTip=$"{stages[i].Status} → {stages[i+1].Status}{Environment.NewLine}"+
                        (stages[i].Count+stages[i+1].Count==0?"no jobs in either stage"
                            :JobStatistics.PairSplit(stages[i].Status,stages[i].Count,stages[i+1].Status,stages[i+1].Count)),
                Points=new System.Windows.Media.PointCollection {
                    new System.Windows.Point(lefts[i]+NodeWidth,tops[i]),
                    new System.Windows.Point(lefts[i+1],tops[i+1]),
                    new System.Windows.Point(lefts[i+1],tops[i+1]+heights[i+1]),
                    new System.Windows.Point(lefts[i]+NodeWidth,tops[i]+heights[i])
                }
            };
            FlowCanvas.Children.Add(flow);
        }

        for(var i=0;i<stages.Count;i++) {
            var stage=stages[i];

            var node=new System.Windows.Controls.Border {
                Width=NodeWidth,
                Height=heights[i],
                CornerRadius=new CornerRadius(4),
                Background=StatusBrush(stage.Status,"Fg"),
                Opacity=0.9,
                ToolTip=StageTooltip(stages,i)
            };
            System.Windows.Controls.Canvas.SetLeft(node,lefts[i]);
            System.Windows.Controls.Canvas.SetTop(node,tops[i]);
            FlowCanvas.Children.Add(node);

            // Icon, name and count sit under the band, where there is always room for them.
            var caption=new System.Windows.Controls.StackPanel { Width=NodeWidth+gap*0.6, HorizontalAlignment=System.Windows.HorizontalAlignment.Center };

            var title=new System.Windows.Controls.StackPanel { Orientation=System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment=System.Windows.HorizontalAlignment.Center };
            title.Children.Add(new System.Windows.Shapes.Path {
                Data=StatusIcon(stage.Status), Fill=StatusBrush(stage.Status,"Fg"),
                Width=11, Height=11, Stretch=System.Windows.Media.Stretch.Uniform,
                VerticalAlignment=System.Windows.VerticalAlignment.Center, Margin=new Thickness(0,0,4,0)
            });
            title.Children.Add(new System.Windows.Controls.TextBlock {
                Text=stage.Status, FontSize=10.5, FontWeight=FontWeights.SemiBold,
                Foreground=StatusBrush(stage.Status,"Fg")
            });
            caption.Children.Add(title);

            caption.Children.Add(new System.Windows.Controls.TextBlock {
                Text=stage.Count.ToString(), FontSize=15, FontWeight=FontWeights.Bold,
                Foreground=StatusBrush(stage.Status,"Fg"), HorizontalAlignment=System.Windows.HorizontalAlignment.Center
            });

            System.Windows.Controls.Canvas.SetLeft(caption,lefts[i]-(caption.Width-NodeWidth)/2);
            System.Windows.Controls.Canvas.SetTop(caption,plotHeight+6);
            FlowCanvas.Children.Add(caption);
        }
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

    /// <summary>Opens the recorded application page (ApplyUrl). The job's Jobright link is Open Job's, never used here.</summary>
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
            : $"The application link for {job.Company} — {job.Title} could not be opened.";
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

    void LoadFields(){ ResumeBox.Text=_s.OriginalResume; PromptBox.Text=_s.MasterPrompt; BaselineStatus.Text=File.Exists(BaselineProfileImporter.BaselineProfilePath) ? "Imported" : "Not imported"; IncomingBox.Text=_s.IncomingFolder; ImportedBox.Text=_s.ImportedFolder; RootBox.Text=_s.ResumeRootFolder; DocxBox.IsChecked=_s.Docx; PdfBox.IsChecked=_s.Pdf; AutoFillBox.IsChecked=_s.AutoFillComposer; AutoCaptureBox.IsChecked=_s.AutoCaptureResult; AutoSendBox.IsChecked=_s.AutoSend; ReadyToastBox.IsChecked=_s.ReadyToast; ReadySoundBox.IsChecked=_s.ReadySound; ReadyFlashBox.IsChecked=_s.ReadyFlash; FocusHotkeyBox.IsChecked=_s.FocusHotkey; }
    string? PickFile(string filter){ var d=new Microsoft.Win32.OpenFileDialog{Filter=filter}; return d.ShowDialog()==true?d.FileName:null; }
    string? PickFolder(){ using var d=new Forms.FolderBrowserDialog(); return d.ShowDialog()==Forms.DialogResult.OK?d.SelectedPath:null; }
    void ResumeBrowse_Click(object s,RoutedEventArgs e){var p=PickFile("Resume files|*.docx;*.pdf|All files|*.*");if(p!=null)ResumeBox.Text=p;}
    void PromptBrowse_Click(object s,RoutedEventArgs e){var p=PickFile("Text files|*.txt;*.md|All files|*.*");if(p!=null)PromptBox.Text=p;}
    void IncomingBrowse_Click(object s,RoutedEventArgs e){var p=PickFolder();if(p!=null)IncomingBox.Text=p;}
    void ImportedBrowse_Click(object s,RoutedEventArgs e){var p=PickFolder();if(p!=null)ImportedBox.Text=p;}
    void RootBrowse_Click(object s,RoutedEventArgs e){var p=PickFolder();if(p!=null)RootBox.Text=p;}
    void Save_Click(object s,RoutedEventArgs e){
        if(!string.IsNullOrWhiteSpace(IncomingBox.Text)&&!Directory.Exists(IncomingBox.Text)){System.Windows.MessageBox.Show("Incoming folder does not exist.");return;}
        if(!string.IsNullOrWhiteSpace(ImportedBox.Text)&&!Directory.Exists(ImportedBox.Text)){System.Windows.MessageBox.Show("Imported folder does not exist.");return;}
        _s=new(){OriginalResume=ResumeBox.Text.Trim(),CandidateProfile=_s.CandidateProfile,MasterPrompt=PromptBox.Text.Trim(),IncomingFolder=IncomingBox.Text.Trim(),ImportedFolder=ImportedBox.Text.Trim(),ResumeRootFolder=RootBox.Text.Trim(),Docx=DocxBox.IsChecked==true,Pdf=PdfBox.IsChecked==true,AutoFillComposer=AutoFillBox.IsChecked==true,AutoCaptureResult=AutoCaptureBox.IsChecked==true,AutoSend=AutoSendBox.IsChecked==true,ReadyToast=ReadyToastBox.IsChecked==true,ReadySound=ReadySoundBox.IsChecked==true,ReadyFlash=ReadyFlashBox.IsChecked==true,FocusHotkey=FocusHotkeyBox.IsChecked==true};
        Storage.SaveSettings(_s); System.Windows.MessageBox.Show("Settings saved.");
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

    void ClearTestQueue_Click(object s, RoutedEventArgs e) {
        var tasks=Storage.LoadTasks(); var n=tasks.RemoveAll(SampleJobs.IsTestJob); Storage.SaveTasks(tasks);
        System.Windows.MessageBox.Show($"Removed {n} sample/stress task(s). Restart A to refresh the visible queue.");
    }


    public void RefreshInspector() {
        if(PreparedInputBox is null) return;
        PreparedInputBox.Text=RequestPreparation.Load()?.Text ?? "No job has been prepared yet.";
    }


    void CopyPrepared_Click(object s, RoutedEventArgs e) {
        var prepared=RequestPreparation.Load();
        if(prepared is null){System.Windows.MessageBox.Show("No prepared input exists yet.");return;}
        CopyToClipboard(prepared.Text,"Prepared input");
    }

    /// <summary>
    /// Single copy path for this window. On failure it pre-selects the text box and points at the
    /// saved .txt file, so the user never has to hunt for the prepared input.
    /// </summary>
    void CopyToClipboard(string text,string label) {
        var clip=ClipboardService.SetText(text);
        if(clip.Success){ System.Windows.MessageBox.Show(label+" copied. "+clip.Message); return; }

        PreparedInputBox.Focus();
        PreparedInputBox.SelectAll();
        System.Windows.MessageBox.Show(
            clip.Message+Environment.NewLine+Environment.NewLine+
            "The text is already selected in Job Details — press Ctrl+C to copy it."+Environment.NewLine+
            "A plain-text copy is also saved at:"+Environment.NewLine+RequestPreparation.PreparedTextPath);
    }

    void ImportBaseline_Click(object s,RoutedEventArgs e) {
        try {
            BaselineProfileImporter.CreateBaselineFromDocx(ResumeBox.Text.Trim());
            BaselineStatus.Text="Imported";
            System.Windows.MessageBox.Show("Original resume text imported successfully. No resume facts were invented.");
        } catch(Exception ex) { System.Windows.MessageBox.Show("Baseline import failed:\n\n"+ex.Message); }
    }

    void PrepareProfileCreation_Click(object s,RoutedEventArgs e) {
        try {
            var request=BaselineProfileImporter.BuildProfileCreationRequest(PromptBox.Text.Trim());
            var prepared=new PreparedRequest{JobId="BASELINE",Company="",Title="Initial Candidate Profile",Text=request};
            RequestPreparation.Save(prepared);
            PreparedInputBox.Text=request;
            System.Windows.MessageBox.Show("Profile-creation request prepared. Review it in Job Details, then paste it into ChatGPT.");
            // Clipboard last: a clipboard problem must not make the preparation look like a failure.
            CopyToClipboard(request,"Profile-creation request");
        } catch(Exception ex) { System.Windows.MessageBox.Show("Could not prepare profile creation:\n\n"+ex.Message); }
    }


    void PasteResult_Click(object s,RoutedEventArgs e) {
        var text=ClipboardService.TryGetText();
        if(text is not null){ ResultInputBox.Text=text; ProfileStatus.Text="Pasted "+text.Length+" characters from the clipboard."; }
        else ProfileStatus.Text="The clipboard holds no text, or it stayed locked after several retries. Paste into the result box with Ctrl+V.";
    }

    /// <summary>
    /// Manual fallback. A6.6.9: this routes exactly like automatic capture — a job's answer goes to
    /// results\&lt;jobId&gt;.json and only the BASELINE flow writes candidate-profile.json. Using the
    /// fallback for a job must never overwrite the baseline every future job is tailored from.
    /// </summary>
    async void SaveProfile_Click(object s,RoutedEventArgs e) {
        var jobId=RequestPreparation.Load()?.JobId;
        var result=ResultCapture.Accept(ResultInputBox.Text,jobId);

        if(!result.Saved) {
            ProfileStatus.Text="FAIL — "+result.Message;
            System.Windows.MessageBox.Show("Profile could not be normalized:\n\n"+(result.Error ?? result.Message));
            return;
        }

        var detail=result.Report?.Describe() ?? "";
        ProfileStatus.Text="PASS — "+System.IO.Path.GetFileName(result.TargetPath)+
            " normalized, validated and saved."+Environment.NewLine+detail;
        System.Windows.MessageBox.Show("Profile saved successfully to "+result.TargetPath+
            Environment.NewLine+Environment.NewLine+detail);

        // Same generation path as automatic capture, so the manual fallback produces documents too.
        if(!ResultCapture.IsBaseline(jobId) && Owner is MainWindow main)
            await main.GenerateForJobAsync(jobId!);
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
/// True when an address may be opened (JobTracker.IsOpenableUrl: absolute http/https). Enables the
/// List's Apply button only for a usable ApplyUrl. Display only; one-way.
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
