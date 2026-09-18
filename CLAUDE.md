# ResumeBuilder — development rules for Claude Code

These rules are permanent. Follow them in every session unless the user explicitly overrides them.

## The project

- **This directory is the authoritative development project.** Current production baseline:
  **v1.0** (assembly version 1.0.0; .NET 8 / `net8.0-windows`, WPF + WinForms interop, WebView2).
  v1.0 is the first numbered release and follows the A6.x development line; the "A6.6.x" labels in this
  file and in source comments are historical — they record when a feature arrived, not the current
  version. The folder is still named `ResumeBuilder-A6.6.7`; that is only its location, not its version.
- The user is **not a developer**. Never hand them source snippets, patches, or instructions to edit
  files themselves. Make every change yourself, in this project.
- Never reconstruct the project from conversation history or from an older ZIP. Read the current
  local source first, every time.
- Never create a second/replacement project directory during normal development.
- Never go back to A6.6.6, A6.6.5 or any older copy unless the user explicitly asks for a rollback.

## Development loop

    user request
      -> inspect the current local source
      -> edit these files in place
      -> run the relevant regression tests
      -> dotnet clean && dotnet build
      -> fix anything that fails, yourself
      -> report that it is ready for manual testing

## Build rule

Before telling the user a change is ready for manual testing, always run:

```bash
dotnet clean
dotnet build
```

**0 errors and 0 warnings are required.** Fix anything that appears; do not report readiness with
warnings outstanding, and do not ask the user to fix build problems.

## Versioning rule

- **Do not bump the version for individual edits or repair attempts.** If a milestone takes five
  rounds of fixes, all five rounds keep the same version.
- Only change the version when the user explicitly starts or approves the next milestone.
- When the version does change, update every reference together: the version properties in
  `ResumeBuilder.csproj` (`Version`, `AssemblyVersion`, `FileVersion`, `InformationalVersion` — the only
  place the numeric version is defined), `MainWindow.xaml` (window `Title` and the header `TextBlock`),
  `MainWindow.xaml.cs` (MessageBox caption), `README.md` (title), the "Current production baseline"
  line at the top of this file, and any version text in `SettingsWindow.xaml` (there is none today).
  Leave no mixed version strings.
- User-facing text uses the short form (`v1.0`); project metadata uses semantic versions (`1.0.0`,
  `1.0.0.0` for assembly and file version, `1.0` informational). `IncludeSourceRevisionInInformationalVersion`
  is off so the informational version stays exactly as written instead of gaining a `+<commit>` suffix.
- Do not rewrite historical milestone labels ("A6.6.12 introduced…", "A6.6.13 adds…") in comments or
  here when the version changes — they are facts about when something arrived.

## ZIP rule

- **Do not create ZIP files during normal development.** No packaging, no extract-and-rebuild cycles.
- Create a ZIP only when the user explicitly asks ("create release ZIP", "package this version").
  Then, in order: run all regression tests -> `dotnet clean` -> `dotnet build` -> require 0 errors and
  0 warnings -> create the ZIP (source only, exclude `bin/` and `obj/`) -> extract it to a clean temp
  directory -> build the extracted copy -> require 0 errors and 0 warnings -> deliver it.

## Testing rule

Run the relevant regression checks before asking for manual testing. Do not regress any of:

- candidate profile normalization
- strict profile validation
- the normalize -> strict validate -> save workflow
- skills / experience / education normalization
- date normalization
- removal of unsupported schema fields
- prepared job payload generation
- external Resume Master Prompt loading
- JSON serialization
- clipboard handling
- prepared-request persistence

**Do not claim a UI feature is verified just because the project builds.** When something genuinely
needs UI interaction, say so plainly and name the *single* manual test to perform next.

Test harnesses are console projects that `<Compile Include="...">` the real source files (never copies).
One-off harnesses live outside the project and are deleted afterwards; re-runnable ones live in
`tests\` and are excluded from the application build by `<Compile Remove="tests\**" />`.

### Real user data — handle carefully

`%LOCALAPPDATA%\ResumeBuilder` holds the live `settings.json`, `tasks.json`, `candidate-profile.json`,
`prepared-request.json` and `prepared-request.txt`. Any test that writes there must back up those
files first and restore them afterwards. Clipboard tests must capture the user's clipboard and
restore it when finished.

## Architecture rules — do not violate

- **The Resume Master Prompt stays outside C#.** It is read at runtime from the file configured in
  Settings. Never embed large prompt/Markdown text in a C# string literal: that is what broke several
  earlier versions (CS1002 / CS1010 / CS1056 cascades in `Services.cs`).
- **All JSON is produced with `System.Text.Json`.** Never build or patch JSON by string concatenation.
- **The candidate profile workflow is normalize -> strict validate -> save**
  (`CandidateProfileStore.NormalizeAndSave`). Do not weaken the strict validation that runs after
  normalization.
- **Normalization may rename, reshape and drop known AI output variations. It must never invent
  candidate information**, and must never change the count or order of `experience` or `education`
  entries.
- **Do not silently change the canonical profile schema** (`info`, `summary`, `skills[]`,
  `experience[]`, `certifications[]`, `education[]`) or the job payload shape. Change either only for
  a demonstrated bug, and say so explicitly.
- **All clipboard access goes through `ClipboardService` in `Clipboard.cs`.** No window or service may
  call `System.Windows.Clipboard`, `System.Windows.Forms.Clipboard` or `DataObject` directly, and no
  second retry loop may be introduced. Clipboard success is decided by reading the clipboard back,
  not by the absence of an exception — on this machine `OleSetClipboard` succeeds while the
  `OleFlushClipboard` behind `SetDataObject(data, copy: true)` throws `CLIPBRD_E_CANT_OPEN`.
- **Job preparation must never depend on the clipboard.** Prepare and persist first, copy afterwards;
  a clipboard failure is never reported as a failed preparation.
- **The AI answer is never read out of the ChatGPT page.** `ChatComposer` is write-only: it types the
  prepared request into the composer and stops there. A third-party DOM changes without notice, and a
  drifted selector on the read path would feed a truncated or wrong answer into the profile. The answer
  comes back through the clipboard (`ClipboardWatcher` + `ResultCapture`), which depends on no page
  structure. Do not add response scraping. (A6.6.11 automates the Send *control* only — see the
  Auto-Send section below — and still never reads or copies a reply.)
- **The clipboard watcher stays scoped.** Armed only between sending a request and capturing its answer,
  reads only through `ClipboardService`, ignores what this app copied itself, and discards anything that
  is not a recognisable profile without storing it anywhere.
- **`candidate-profile.json` is the baseline and the input to every job.** Tailored per-job answers are
  written to `results\<jobId>.json` (`ProfileResultStore`); only the BASELINE flow writes the baseline
  file. Never let a job result overwrite the baseline — that would tailor job 2 from job 1's output.
- **A failed capture saves nothing.** The raw answer goes to `results\<jobId>.raw.txt`, the job is marked
  Failed, and the message says why. The manual Settings → Result path must keep working as the fallback
  for every automation failure.

## Usage efficiency

Claude usage is a limited development resource. Spend it on correctness, not on re-derivation.

- Treat this CLAUDE.md, the Git history and the source itself as the persistent project context.
  Read them instead of relying on a long conversation, and instead of re-reading what is already known.
- Search narrowly (grep/glob for the symbol) before opening a large file; read only the relevant range.
- Batch related inspections and related edits into single calls where practical.
- Do not run redundant builds or test suites. One `dotnet clean && dotnet build` per change set, and run
  only the regression suites the change can actually affect.
- Keep progress updates short. Do not re-explain work that is already finished.
- After a feature or fix is completed, tested and committed, recommend starting a fresh conversation
  for the next independent task.
- **Never trade away correctness, regression testing, or source safety to save tokens.** If a check is
  needed to know whether something works, run it.

## Source control

- This project **is a Git repository** (branch `main`, baseline commit "Baseline: ResumeBuilder A6.6.7").
  Inspect `git status` / `git diff` before substantial changes and use commits as checkpoints instead of
  development ZIPs.
- Commit only what the user asked for, and **never reset, revert, discard or overwrite their changes**
  without explicit authorization. Do not tag or push unless asked.
- The repo is configured with `core.autocrlf false` and a repo-local identity; leave both alone.

## File map

| File | Contents |
| --- | --- |
| `MainWindow.xaml(.cs)` | Task queue, Prepare & Send, armed capture wiring, embedded ChatGPT WebView2 |
| `SettingsWindow.xaml(.cs)` | General settings + automation toggles, Job Details inspector, manual Result capture, Development tab |
| `Services.cs` | `Storage`, `JobImporter`, `RequestPreparation`, `BaselineProfileImporter`, `CandidateProfileStore` |
| `Normalization.cs` | `ProfileNormalizer`, `NormalizationReport` — AI output -> canonical schema |
| `Clipboard.cs` | `ClipboardService` (the single clipboard implementation) and `ClipboardWatcher` (armed capture) |
| `ResultCapture.cs` | `ResultCapture` (capture gate + routing), `ProfileResultStore` (per-job result files) |
| `ChatAutomation.cs` | `ChatComposer` — write-only WebView2 composer fill |
| `DocumentGeneration.cs` | `ResumeDocument`, `BulletLine`, `DocxWriter`, `ResumeGenerator` — DOCX from a validated profile |
| `PdfGeneration.cs` | `PdfWriter` (PDFsharp/MigraDoc), `ResumeFontResolver` — PDF without a browser |
| `ResumeOutputManager.cs` | `ResumeOutputManager`, `ResumeOutputPaths`, `ResumeOutputMetadata` — dated job folders, unique names, resume-info.json |
| `JobTracker.cs` | `JobTracker`, `ApplicationStatus`, `JobStatistics` — application lifecycle, filtering, statistics, opening a job link |
| `JobBrowser.cs` | `JobBrowser` (profile path, home URL, log scrubbing), `JobImportData`, `IJobPageExtractor`, `BrowserImportOutcome` — the job-browser foundation |
| `JobrightPageExtractor.cs` | `JobrightPageExtractor` — every Jobright-specific assumption: reads the page's JSON-LD `JobPosting` on the Import click |
| `JobBrowserWindow.xaml(.cs)` | The built-in job browser: a second WebView2 on its own profile, with Back/Forward/Refresh/Home |
| `ResumeStyle.cs` | `ResumeStyle`, `TextStyle`, `StyleSchema`, `StyleLimits`, `StylePresets` — the style tokens and presets |
| `ResumeStyleNormalizer.cs` | `StyleNormalizer` (preset + merge + clamp + enforce), `StyleValidator` (the same rules as errors) |
| `ChatHost.cs` | `ChatHost` — creates and destroys the ChatGPT WebView2 between jobs |
| `QueueRunner.cs` | `QueueRunner`, `QueueState`, `FailureOutcome` — sequencing state machine, no I/O |
| `Diagnostics.cs` | `PerfLog` (timing/memory log), `PollPolicy` (shared adaptive poll cadence) |
| `ReadyToast.cs` | `ReadyToast` (right-side, non-activating "answer ready" notification), `WindowAttention` (taskbar flash) |
| `CaptureWatchdog.cs` | `CaptureWatchdog` — 30 s bounded wait for the Copy after a confirmed READY (no I/O, injectable delay) |
| `GlobalHotkey.cs` | `GlobalHotkey` — system-wide Ctrl+Shift+' that only brings Resume Builder forward |
| `ApplyCapture.cs` | `ApplyCapture`, `ApplyCaptureResult` — decides whether a user's Apply destination is recorded, and on which task |
| `ApplicationPlatformDetector.cs` | `ApplicationPlatform` enum, `ApplicationPlatformDetector` (ApplyUrl -> platform), `TolerantPlatformConverter` |
| `TaskViews.cs` | `TaskViews` — the Active / History display filter over the one task collection |
| `Models.cs` | `JobBatch`, `JobInput`, `JobTask`, `AppSettings`, `PreparedRequest` |

## Document generation (A6.6.9)

- **Documents are generated only from an already-validated profile file**, after the JSON is saved and
  the job is marked Completed. `ResumeGenerator` only ever *reads* `results\<jobId>.json`; it never
  writes a profile and never touches `candidate-profile.json`.
- **A document failure never changes the job's Completed state** and never destroys the saved JSON.
  Failures are reported on their own status line, logged to `results\<jobId>.docgen.txt`, and the
  Generate Documents button regenerates from the saved JSON.
- **DOCX comes from `DocumentFormat.OpenXml`** — no Word, no COM, no Office automation, ever.
  **PDF comes from WebView2 `PrintToPdfAsync`** over the HTML rendering; do not add another PDF engine.
- **Both renderers are driven by the same `ResumeDocument`**, so DOCX and PDF always carry identical
  content. A test asserts the DOCX round-trips every model line; keep it that way.
- **Output is organized by day and job** (see "Resume output folders" below). The earlier rule —
  `<Company> - <Role>.docx` written flat into the Resume Root and overwritten — was explicitly
  replaced by the user; do not restore it.
- Documents are written to a temp file and moved into place, so a crash cannot leave a truncated file.
- The `Docx` / `Pdf` checkboxes are authoritative; both off means "disabled", which is reported as a
  skip, not a failure.

## Task queue Active / History view

- **Display only.** `MainWindow._tasks` stays the one task collection; `tasks.json`, `JobTask`, the
  queue, the importer and the Settings dashboard (which reads `MainWindow.Tasks`, the full list) are
  untouched. Never delete a Completed task to tidy the list — results, Resume buttons and the dashboard
  depend on it.
- `TaskList` keeps its single ListBox; its `ItemsSource` is `TaskViews.CreateView` — a live-filtering
  `ListCollectionView` over `_tasks`. **Active** = every status except Completed (Queued, Processing,
  Failed — Failed stays so Retry is reachable). **History** = Completed only. Do not add a second list:
  Prepare & Send and Generate Documents depend on `TaskList.SelectedItem`.
- Live filtering applies on the dispatcher, so a job that completes leaves Active on its own. A test
  that checks this without a running app must pump the dispatcher first.
- **A Processing task is never hidden**: `Task_PropertyChanged` switches to Active when a task becomes
  Processing while History is shown (re-running a completed job via Prepare & Send).
- The switch sits under "TASK QUEUE" as `Active (n) | History (n)`; the shown view is SemiBold. Default
  is Active. Switching keeps the selection when the selected task is still on show.

## Job application tracking

- **Two statuses, never merged.** `JobTask.Status` is the QUEUE (Queued / Processing / Completed /
  Failed). `JobTask.ApplicationStatus` is the APPLICATION (Viewed -> Ready -> Applied -> Interview ->
  Done). A job is routinely queue-Completed and application-Ready. Do not collapse them, and do not
  let one reset the other.
- **Resume Builder automates exactly one transition**: Viewed -> Ready, when a document was actually
  generated (`JobTracker.MarkResumeReady`, called from `GenerateDocumentsAsync`). Everything past
  Ready is the user's own knowledge and is entered by hand. Never infer that a job was applied for,
  answered or finished.
- **`MarkResumeReady` never moves a job backwards.** Regenerating documents for a job the user
  already marked Applied records the new `ResumePath` and leaves the status alone.
- **`ApplicationStatus.Normalize` makes tracking backward compatible**: missing, empty or unknown is
  Viewed, so a `tasks.json` written before tracking existed loads with no migration step.
- **`JobTracker` holds no UI and no rendering.** It updates a task, filters, counts and opens a link;
  `SaveTrackingData` is the existing `Storage.SaveTasks`. Tracking must stay out of generation.
- **Only http/https links are ever handed to the shell** (`JobTracker.IsOpenableUrl`). A job payload
  is third-party data: a `file://` or local path in its `url` must never become a launch. The default
  browser is used; no second browser is embedded.
- The Settings "Applications" tab edits **MainWindow's live task objects** (`MainWindow.Tasks`), not a
  second copy loaded from disk, so a status change there cannot be overwritten by the queue's next
  save. Keep it that way if the window ever gains another task view.
- **Stage timestamps are historical facts.** `ViewedAt`, `ReadyAt`, `AppliedAt`, `InterviewAt` and
  `DoneAt` are stamped when a stage is reached, are never rewritten, and are never cleared — moving a
  job back to correct a mistake keeps the record of what actually happened. (This replaced an earlier
  rule that cleared them; the user asked for history to be preserved.) Skipping a stage backfills the
  ones passed over, so a job moved straight to Interview still has an `AppliedAt` and appears in the
  activity chart as well as the totals.
- **The dashboard is the Settings "Applications" tab.** Summary cards (today / last 30 days / total),
  a WPF-native bar chart, the five-stage pipeline, conversion rates, search + status + date filters,
  and a List/Board pair over the same filtered set.
- **How the dashboard stays in sync.** Every view shares the same `JobTask` objects, and their
  `INotifyPropertyChanged` (`Status`, and `NotifyTrackingChanged` after a JobTracker edit) updates rows
  live. The aggregates — cards, chart, pipeline, board columns, filter membership — are rebuilt only by
  `SettingsWindow.RefreshTracking`. When `MainWindow` changes tracking data it calls
  `RefreshDashboardIfOpen()` once, after the save: Job Browser import, `MarkResumeReady`, and a Refresh
  Input that queued new jobs. It does nothing when Settings is closed. Do not add events or a second
  task store for this; queue `Status` changes need no dashboard refresh, since the dashboard never
  shows them.
- **Its look is defined by named styles in `SettingsWindow.xaml`** — `DashboardCardStyle`,
  `SectionHeaderStyle`, `StatusBadgeStyle`, `FilterControlStyle`, `BoardCardStyle`,
  `SegmentToggleStyle` — plus `Status<Name>Bg`/`Status<Name>Fg` brushes for the five statuses. The
  brushes are the single source of truth: the list badges pick them up through `DataTrigger`s and the
  pipeline and board columns through `StatusBrush(status, "Bg"/"Fg")`, which falls back rather than
  throwing if a key is missing. Status colour is presentation only; stored data never carries it.
- **Applications are counted from `AppliedAt`**, because that is when an application existed. The
  *total* is status-based (Applied, Interview or Done, counted once each), so a job marked Applied
  before timestamps existed still counts; it simply contributes nothing to the dated chart.
- **The chart has no dependency.** Bars are `Border`s in an `ItemsControl`, scaled per refresh against
  the busiest day on show. A 7- or 30-day window includes empty days; "All time" lists only days with
  applications, so a long history stays readable. Do not add a charting library for this.
- The date filter and the Date column use `JobTask.TrackingDate` — `AppliedAt ?? ReadyAt ?? CreatedAt`
  — so a task saved before tracking existed still sorts and filters without a null check everywhere.
  The filter control is a popup with quick ranges (Today / 3 / 7 / 15 / 30 days / Any date) beside a
  `Calendar` for one exact day; an exact day overrides the quick range. Both go through
  `JobTracker.ApplyFilters`, so there is only ever one date system.
- **Search matches every word against company + role + job id combined**, so "Caterpillar AI" finds
  Caterpillar Inc / Senior AI Software Engineer. `JobTracker.Search` is the only implementation, and
  List and Board are filtered by the same call so they can never disagree.
- **List mode shows the daily activity chart; Board mode shows the flow graph** (`BuildFlowGraph`:
  bands proportional to each stage's count, joined by tapering connectors, drawn with plain WPF shapes
  on a `Canvas`). The canvas is only sized once its tab and card are visible, so the graph is also
  rebuilt from `FlowCanvas.SizeChanged` — a `TabControl` does not lay out an unselected tab.
- Every bar and every stage carries its own `ToolTip` ("Sep 17, 2026 / 12 Applications";
  "Applied / 30 jobs / 71% of Ready"). Standard WPF tooltips, no floating windows.
- **A tooltip is only reachable if something under the pointer is hit-testable.** A one-application
  day is a two-pixel bar and a one-job stage is a five-pixel band, so each chart column and each flow
  stage has a full-height `Background="Transparent"` target carrying the tooltip. Never hang a chart
  tooltip on a bare `StackPanel` — it has no background, so it receives no mouse events.
- **The row's status control is `StatusPickerStyle`**: a fully templated `ComboBox` that looks like the
  badge when closed and lists the five statuses with icons when open, its popup sized in the template
  (158 px wide, ~32 px rows). Do not go back to laying a near-invisible ComboBox over the badge —
  `Opacity` and `Foreground` are inherited by the dropdown, so its items rendered invisible at the
  cell's width and the popup looked like a blank white rectangle. Dropdown item styles must bind to
  `{Binding}` (the status string), not to `ApplicationStatus`, which only exists on a row.
- List columns are bounded so a wide window cannot stretch them into empty space: Job and Company are
  star-width with `MaxWidth`, and Status/Resume/Date/Actions are fixed.
- **Every list column needs a `MinWidth`, including the fixed ones.** A DataGrid squeezes columns down
  to `DataGrid.MinColumnWidth` (20 px) *before* it will scroll, so fixed columns with no floor were
  crushed to 57/37/20 px in a narrow window while the extent never exceeded the viewport — the content
  looked clipped and offset, and there was no scrollbar to bring it back. With a floor on each column
  the grid scrolls honestly instead. Current floors: Job 150, Company 110, Status 140, Resume 116,
  Date 88, Actions 246 — 850 px total, which fits the ~900 px grid at a 1000 px window with little to
  spare (Actions grew from 196 when the Apply button was added). Do not add a column without
  revisiting this.
- **The DataGrid is the only horizontal scroll owner.** The dashboard's outer `ScrollViewer` is
  vertical only (its default `HorizontalScrollBarVisibility` is Disabled); do not enable horizontal
  scrolling there, or the two will fight. `TrackingGrid_SizeChanged` clamps the grid's horizontal
  offset when widening leaves it past the new end.
- **An adjacent-pair percentage is A / (A + B)**, the earlier stage's share of the two
  (`JobStatistics.PairShare`, shown with `WholePercent`, spelled out by `PairSplit` as
  "60% Ready vs 40% Applied"). It is not a B/A conversion. The pipeline label, the stage tooltips and
  the flow connectors all go through those helpers. The three named funnel rates (Applied, Interview,
  Completion) are a separate statistic and keep their own formulas.
- **`ResumePath` is what makes the Resume actions work**, and documents generated before tracking
  existed have none. `JobTracker.RelinkResumes` repairs that on each dashboard refresh by looking for
  `<ResumeRoot>\<date>\<ResumeOutputManager.JobFolderName(company, role)>\Resume*.docx` — it reuses
  that naming rather than duplicating it, prefers the most recently written file (sorting by name
  would put `Resume.docx` ahead of `Resume (2).docx`), and only writes when something changed. The
  Resume and Folder buttons are disabled while `ResumeGenerated` is false.
- Diagnostics: `TRACKING status <jobId> <old> -> <new>`, `TRACKING open URL <jobId>`,
  `TRACKING dashboard refreshed total=… applied=… interview=… done=…`. One line per refresh, never
  per UI frame.

## Built-in job browser (phase 1)

- **Visible again.** It was hidden at the user's request for a while; the `JobBrowserButton` in
  `MainWindow.xaml` is shown once more.

### Find Jobs / Auto Import (user-started batch on a Jobright results page)

These buttons are the one deliberate exception to "no batch, no crawling" below: they run only when
the user clicks them, and every job still goes through `JobrightPageExtractor` and `ImportJob`.

- **Jobright scrolls an internal container, not the window**: `div.index_jobs-page-main-content__qd__a`.
  `ScrollDownAsync` scrolls with `container.scrollTop += amount` (80% of `clientHeight`) and waits for
  more results to load. **Never use `window.scrollTo` / `window.scrollBy`** — the window does not
  scroll there, so no new jobs load.
- `FindJobsUntilTargetAsync(target)` repeats collect `a[href*="/jobs/info/"]` links -> scroll until
  the target is reached or Stop is pressed. Verified: it collected 50 jobs with a target of 50.
- The scan target is chosen in `JobTargetBox` (20 / 50 / 100 / 200 / 500, default 50) and read by
  `GetJobTarget`, which falls back to 50.
- **The target counts NEW jobs only** — jobs not already in Resume Builder. While scanning,
  `CollectJobsFromCurrentPageAsync` checks each link with `JobExists` and skips existing ones (counted
  and reported as "skipped … already in Resume Builder", never toward the target); each link is keyed by
  `JobUrls.Normalize` so a card seen again after a scroll is counted once. The scan keeps scrolling
  until it has that many unseen jobs, the user presses Stop, or it reaches the end of the list —
  `EndOfListRounds` (3) rounds in a row with no new job and no scroll movement — so it can never loop
  forever when Jobright runs out.
- **`AutoImportFoundJobsAsync` skips URLs already in the queue before navigating to them**, through
  the `JobExists` delegate (`MainWindow` compares with `JobUrls.Normalize`), so existing jobs cost no
  page load. New ones are opened one at a time and imported through the normal `ImportJob` path, which
  still refuses duplicates on its own.

### Application link capture (ATS platform, phase 1)

- **Jobright's page data holds no application address** (verified on a live job page: 53 `jobResult`
  keys, only flags such as `isCompanySiteLink`; APPLY NOW is a plain button). The address exists only
  once the USER clicks Apply, so it is captured then — never by clicking, fetching or reading the page.
- `JobBrowserWindow` observes `NavigationStarting` (same-page) and `NewWindowRequested` (new window,
  `Handled` never set) and calls `ReportApplyDestination` only when a single job page heads off
  jobright.ai. It hands (job page, destination) to the `RecordApplyUrl` delegate; `MainWindow`
  runs `ApplyCapture.Record` on `_tasks` and saves through `Storage.SaveTasks` when it returns Recorded.
- **Rules (`ApplyCapture`)**: http/https only; never jobright.ai or a subdomain; never a known
  non-application link Jobright shows (LinkedIn `/in/`, `/company/`, `/school/`, X/Twitter,
  Crunchbase, Glassdoor, Facebook, Instagram, YouTube — LinkedIn `/jobs/` IS captured); only on a
  single job page; the task is matched by **Jobright job id** (`JobIdFromUrl` of page vs `Link`);
  an unknown job records nothing and creates nothing. Stored via `JobUrls.Normalize`. The same
  address again is Unchanged (time kept); a different one replaces it and restamps.
- `JobTask.ApplyUrl` ("" = none) and `ApplyUrlCapturedAt` (null = none) are additive; older
  `tasks.json` loads with them empty. Informational only — never a duplicate key; `Link` keeps its
  meaning.
- Diagnostics: `JOBBROWSER apply seen <scheme+host+path> via <navigation|new-window> -> <result>`.
- Tests: `ResumeStyleTests` "Job browser: application link capture" (4 checks).

### Application platform detection (phase 2)

- `JobTask.ApplicationPlatform` is an `ApplicationPlatform` enum — Unknown, Greenhouse, Workday,
  Lever, LinkedIn, Ashby, SmartRecruiters, ICims, Other. **Unknown = no ApplyUrl; Other = an ApplyUrl
  on a site not recognised.** It is **always derived** from `ApplyUrl` by
  `ApplicationPlatformDetector.Detect`, never entered by hand.
- Detection is by host, exact or subdomain (look-alikes such as `notgreenhouse.io` are Other):
  greenhouse.io; myworkdayjobs.com / myworkdaysite.com / myworkday.com; lever.co; ashbyhq.com;
  smartrecruiters.com; icims.com; linkedin.com only for `/jobs/` paths. A company careers page with a
  `gh_jid` or `ashby_jid` query parameter is Greenhouse / Ashby (embedded boards). Pure; never throws.
- Runs in `ApplyCapture.Record` whenever it records an address, and at startup
  (`ApplicationPlatformDetector.Refresh` next to `RecoverStaleProcessing`), which re-derives every task
  and saves once only if something changed — so older jobs and detector updates are applied.
- **Stored as the name** (`"Greenhouse"`), read by `TolerantPlatformConverter`: missing, null,
  misspelled, future or wrongly-typed values become Unknown instead of throwing. This matters because
  `Storage.LoadTasks` turns any exception into an EMPTY list, which the next save would write over the
  user's jobs. Any future enum field on `JobTask` needs the same tolerant treatment.
- Tests: `ResumeStyleTests` "Application platform detection" (7 checks).

### Application platform filter (phase 3)

- **Same pipeline, one extra optional parameter**: `JobTracker.ApplyFilters(..., exactDate, platforms)`.
  Order: status -> search -> platforms (`FilterByPlatforms`) -> date. Null or empty = every platform;
  otherwise **OR** — a job passes when its platform is any ticked one. List and Board both use the one
  `filtered` result, so they cannot disagree. Cards, pipeline and chart still describe ALL jobs.
- **Choice order is `JobTracker.PlatformFilterOrder`**, explicit and never the enum's declaration
  order: Greenhouse, Workday, Lever, LinkedIn, Ashby, SmartRecruiters, iCIMS, Other, Unknown.
  `PlatformDisplayName` shows `ICims` as "iCIMS". A new enum value must be added there too (a test
  asserts every platform is a choice, exactly once).
- **Label** (`PlatformFilterLabel`): "All platforms" for none or all ticked; one or two names in that
  order ("Greenhouse, Workday"); "N platforms" for three or more.
- UI: `PlatformFilterButton` + `PlatformFilterPopup` left of the Status dropdown, built like the date
  filter. Checkboxes are created in `InitTracking` from `PlatformFilterOrder`; each tick refreshes at
  once, the popup stays open until an outside click, and Clear unticks all with a single refresh.
- **The selection is `SettingsWindow._platformFilter` only** — never tasks.json, settings or a task.
- **Platform badge** (display only): one shared `PlatformBadgeStyle` (a templated `ContentControl`,
  neutral colours so it never reads as a status) used in the List's Job cell (right-docked after the
  title, which trims first — no extra column, so the 800 px column floors still hold) and on the Board
  card's company line. Text goes through `PlatformDisplayConverter` -> `JobTracker.PlatformDisplayName`;
  **Unknown is hidden and carries no tooltip**; Other and every recognised platform show, with the
  `ApplyUrl` as tooltip. `RecordApplyUrlFromBrowser` calls `RefreshDashboardIfOpen()` after a
  successful record, so a new badge appears without a manual refresh.
- **Open Application** (`JobTracker.OpenApplyUrl`): opens the recorded `ApplyUrl` through the same
  `IsOpenableUrl` (http/https only) and `Launch` as Open Job, and logs `TRACKING open apply URL <jobId>`
  (id only). `ApplyUrlToOpen` **never falls back to `Link`** — the Jobright posting is Open Job's. UI:
  an Apply button between Open Job and Resume in the List's Actions column (disabled via
  `OpenableUrlConverter` unless the ApplyUrl is usable; tooltip = ApplyUrl), and "Open Application"
  under "Open Job Posting" in the List and Board context menus, which show a status message instead of
  opening when no link is recorded. Test: `OpenApplicationIsSafe` (refusals only — it never launches).
- Tests: 5 checks in the "Tracking dashboard" group of `ResumeStyleTests`.

### Design direction — agreed, NOT yet built

Do not implement these without the user starting the work; they record intent, not current behaviour.

- **Jobright "Remove From List"** makes a card disappear from Jobright's results, so future scans stop
  finding it. It is the candidate mechanism for keeping unwanted jobs out of later scans. Clicking it
  changes the user's Jobright account, so automating it needs the user's explicit go-ahead and must
  stay a user-started action — never a side effect of a scan or an import.
- **"Already Applied" stays a local application status** (`ApplicationStatus`), not a deletion — the
  job and its history remain in Resume Builder.

### Core job-browser rules

- **It is a SECOND WebView2 and shares nothing with the ChatGPT one.** Its user-data folder is
  `%LOCALAPPDATA%\ResumeBuilder\JobBrowserWebView2` — a sibling of the ChatGPT profile, never inside
  it — which forces a separate `CoreWebView2Environment`, a separate browser process tree and a
  separate cookie jar. `JobBrowser.IsSeparateProfileFrom` states that rule and a test asserts it.
- **It lives in its own window** (`JobBrowserWindow`), opened from the "🔎 Job Browser" button in the
  main header and owned by `MainWindow`, so closing the app closes it. It is deliberately not part of
  the main layout: the ChatGPT pane, `ChatHost` recycling and the queue are untouched by it.
- **It takes no part in the automation.** No `ChatHost`, no `ChatCompletionWatcher`, no
  `CaptureWatchdog`, no clipboard, no `KeyboardSimulator`, no queue. A failure while browsing is shown
  on its own status line and cannot fail a job.
- **It reads a page only when the user clicks Import Current Job** (phase 2), and only through
  `JobrightPageExtractor`. No polling, no persistent scripts, no import on navigation, no batch, no
  crawling. One `ExecuteScriptAsync` per click, and that script (`JobrightPageExtractor.ReadScript`)
  returns only `location.href` and the text of the `application/ld+json` blocks. A test asserts it
  mentions no cookie, storage, `innerHTML`/`innerText`, `document.body`, `fetch`, or `click(`.
- **Sources, verified on the live site.** `__NEXT_DATA__` (the page's own `application/json` data) is
  ALWAYS present on a loaded job page and is the primary source: `jobResult.{jobId, jobTitle,
  jobSummary, coreResponsibilities, qualifications}` and `companyResult.{companyName, companyURL}`.
  The schema.org `JobPosting` JSON-LD is served only **sometimes** (present on a first anonymous
  visit, then absent in the same browser), so it is a fallback — never the only source. The read
  script picks the named fields out of `__NEXT_DATA__` inside the page rather than returning the
  whole blob. There are deliberately **no CSS selectors** and no DOM text.
- **Every source must describe the job in the address.** `__NEXT_DATA__` is written only on a full
  page load, so an in-app switch can leave it describing the previous job; a JSON-LD `identifier` can
  lag the same way. Each source is used only if its job id equals the `/jobs/info/<id>` in the
  address; if neither does, the import is refused ("not finished switching… Click ⟳ Refresh") rather
  than attributed to the wrong job.
- Fields: company and title from the page data (its title is the one the page shows, without the
  "[Remote]" prefix JSON-LD adds); **jobUrl** from `<link rel="canonical">` when it names the same
  job, else the address — query string dropped, since on Jobright it only ever carries tracking;
  **companyUrl** from `companyResult.companyURL` (or JSON-LD `hiringOrganization.url/sameAs`) only when
  it is a real web address, otherwise none — never guessed; **description** is the full JSON-LD
  posting when published, otherwise composed from this job's own summary, responsibilities and
  required/preferred qualifications — the sections the page shows under the title, nothing else.
- **The extractor returns `JobImportData`, never a `JobTask`**, and has no WebView2 reference —
  `ExecuteScriptAsync` and the current address are injected. A reflection test asserts it can hold no
  task, task list or storage.
- **`MainWindow.ImportFromBrowser` owns the hand-over**: it runs `JobImporter.ImportOne` on a copy of
  the live task list (as `RefreshInput` does) and adds only the new task, so selection and existing
  task objects are untouched. The browser window only holds an `ImportJob` delegate.
- Outcomes are shown in the window's banner: *Imported* (title, company, "Status: Viewed"),
  *Already imported* for a duplicate (not an error), or *Could not import this page.* with a plain
  reason. Raw script errors are logged by exception type only and never shown. The button is disabled
  while an import runs and while a page is loading, and is only enabled on a single job page; one
  click can create at most one task.
- Diagnostics: `JOBBROWSER import requested <url>`, `extract success`, `import success <internal id>`,
  `duplicate <internal id>`, `import failed <reason>` — URLs scrubbed by `SafeForLog`, no page content.
- UI tests run on a **throwaway profile** and answer `https://jobright.ai/*` locally with
  `WebResourceRequested`, shaped like the live page (canonical link + `__NEXT_DATA__`, no JSON-LD), so
  neither the network nor the user's signed-in profile is ever touched. When embedding JSON in a test
  page, escape `</` as `<\/` — a literal `</script>` inside it ends the block early.

## Job input contract — one job, five fields

    { "company": "…", "title": "…", "jobUrl": "https://…", "companyUrl": "https://…", "description": "…" }

- **`JobImportData` is the canonical input** and both sources produce it: an Incoming JSON file
  deserializes straight into it, and the job browser's extractor returns it. Required: company,
  title, jobUrl (an absolute http/https address), description. Optional: companyUrl (kept only if it
  is a real web address; a bad one is dropped, it never fails the job). There is **no external jobId
  and no location** — neither is required, read or invented.
- **One job per input, always.** No batches, arrays, lists, pages or crawling. An Incoming file that
  is an array — even an array of one — or an object with a `jobs` list (the retired batch format) is
  refused with "Only one job per input file is supported." and stays in Incoming; it is never partly
  imported. A refused file is left in place to be fixed.
- **There is one importer: `JobImporter.ImportOne`** — validate, normalize the job URL, refuse a
  duplicate, otherwise create one task and save. `JobImporter.Import` (Incoming folder) calls it per
  file; the browser calls it through MainWindow. Do not add a second validation or dedup path.
- **`JobTask.JobId` is Resume Builder's internal task id**, not an input field. The queue, the capture,
  `results\<id>.json`, prepared requests and every diagnostic depend on it, so it was kept, not
  renamed. New tasks get `RB-yyyyMMdd-HHmmss-xxxxxxxx` from `JobImporter.NewInternalId` (time for
  readability, random for safety, checked against the queue); it is generated once, persisted, and
  never changes. Older tasks keep the ids they were saved with.
- **Duplicates are found by normalized job URL, never by id.** `JobUrls.Normalize` is the only place
  URLs are made comparable: trim, lower-case scheme and host, drop the default port, the fragment and
  a trailing slash (not the root's), drop known tracking parameters (`utm_*`, `gclid`, `fbclid`,
  `msclkid`, …), keep and sort every other parameter (on another site one may name the job), keep path
  case. Never compare raw URL strings anywhere else.
- **The job URL is stored in the existing `JobTask.Link`** (normalized), so Open Job, `resume-info.json`
  and the GPT payload keep working unchanged. `JobTask.CompanyUrl` is new and additive — older tasks
  load with it empty. `JobTask.Location` is kept for older tasks and left empty on import.
- New tasks start **Queued** and **application-Viewed**; nothing past Viewed is stamped.
- The Development tab writes **one file per job** in this format, with per-run URLs under
  `https://example.com/sample/<run>/` so a second run is not a duplicate. `SampleJobs.IsTestJob`
  recognises test jobs by that URL root (and by the `SAMPLE-`/`STRESS-` ids older ones were saved with).
- **Sign-in belongs to the site.** The app stores no credentials, reads no cookies or tokens, and
  disables no browser protection. Nothing about CAPTCHAs, rate limits or access controls is bypassed.
- Diagnostics: `JOBBROWSER initialize` / `ready` / `navigate <url>` / `dispose`. The URL is scrubbed by
  `JobBrowser.SafeForLog` to scheme + host + path — a query string can carry a session token or a
  search term and must never reach `diagnostics.log`. Page content is never logged.
- The environment is cached in a static field so re-opening the window reuses the same signed-in
  profile; closing disposes only that view.

## Resume output folders

    ResumeRoot\yyyy-MM-dd\<Company> - <Role>\Resume.docx
                                            \Resume.pdf
                                            \resume-info.json

- **`ResumeOutputManager` owns every folder and file-name decision.** No date, sanitization or
  collision logic belongs in `ResumeGenerator`, `DocxWriter` or `PdfWriter` — they receive finished
  paths. The renderers were not touched when this landed; keep it that way.
- **The daily folder is local-date `yyyy-MM-dd` and is reused**, never duplicated. Every job that day
  gets its own `<Company> - <Role>` subfolder beneath it.
- **Nothing is ever overwritten.** A second run for the same job on the same day writes
  `Resume (2).docx`, `Resume (2).pdf` and `resume-info (2).json` — one shared revision number, so a
  DOCX/PDF/metadata set never splits across revisions.
- **Only the folder name is sanitized** (`\ / : * ? " < > |` and control characters become spaces,
  runs collapse, trailing dots and spaces go, 120-character cap). The job data itself is never
  modified — `resume-info.json` records the company and role exactly as they arrived.
- **`resume-info.json` is written with `System.Text.Json`** and always carries `jobId`, `company`,
  `role`, `jobUrl`, `generatedAt`, `docxFile` and `pdfFile`. It is written even when a document
  failed (that file's name is `null`), and a metadata failure never changes the document outcome.
- Diagnostics: `OUTPUT folder: <path>` and `OUTPUT metadata saved: <file>`.

## Resume styling — the style system

- **Styling lives in `ResumeStyle.cs`, never in the renderers.** A renderer converts tokens into its
  own units (half-points, twips, MigraDoc `Unit`); it must not carry literal sizes, colours or fonts.
  Do not reintroduce `font.size = 11` scattered through `DocxWriter`/`PdfWriter`.
- **The AI may return an OPTIONAL top-level `style` object** drawn from a fixed token set. It must
  never send OOXML, CSS, HTML, Markdown, python-docx or any other code, and the renderer must never
  interpret free-form formatting instructions. New capability means a new *token*, validated here.
- **A profile with no `style` renders with the `promV4.12` preset**, so every older profile is
  unchanged. Presets are `promV4.12` (default), `compact` and `classic`; a preset is just a complete
  default style.
- **`StyleNormalizer` is preset -> deep merge -> clamp -> enforce**, and it never throws: an
  out-of-range value is corrected and reported, so a styling mistake cannot fail a job whose resume
  content is good. `StyleValidator` applies the same rules as errors for the documented contract and
  the tests. Keep both in step.
- **Hard rules that survive any AI request**: body/bullet/education ≥ 11 pt, skill values ≥ 10.5 pt,
  line spacing ≥ 1.0, `#RRGGBB` colours only, whitelisted fonts, single column. The summary, skill
  values and education always render regular weight; a bullet whose every segment is bold is demoted
  to regular. No text boxes, sidebars, icons, skill bars, graphics, layout tables or backgrounds.
- **Emphasis is structural, never Markdown.** `descriptionLines` accepts a plain string or
  `{ "segments": [ { "text", "bold" } ] }`; segment spacing is preserved exactly. Markdown in resume
  text is a bug, not a feature.
- **The company heading is built by the renderer** from `company`, `title`, `startDate` and `endDate`
  (`Company | Role | Start - End`), and the metadata line from `location`, `employmentType` and
  `workArrangement`. The AI never supplies a pre-combined heading, and an empty value disappears with
  its separator — `California | Full-time |` must never render.
- **DOCX uses real Word styles** (`Normal`, `Heading 1/2/3` with outline levels) so the Navigation
  Pane works: section headings are Heading 1, company headings and skill categories Heading 2, an
  optional project subtitle Heading 3. Paragraphs also carry direct formatting, and OOXML child-element
  order is schema-significant — a test validates every generated DOCX with `OpenXmlValidator`.
- **Rendering is deterministic**: the same JSON produces byte-identical `document.xml` and `styles.xml`.
- The resolved style is written to `results\<jobId>.effective-style.json` for debugging; document
  generation still never writes a profile.
- Contract for the AI: `docs\GPT_JSON_CONTRACT.md` and `docs\gpt-resume-json-example.json`. Keep them
  in step with the tokens in `ResumeStyle.cs`.
- Regression harness: `tests\ResumeStyleTests` (41 checks, `dotnet run --project tests\ResumeStyleTests`).
  It compiles the real source files and must stay at 0 failures.

## Sequential queue (A6.6.10)

- **`QueueRunner` contains no I/O** — no files, clipboard, WebView2 or UI. All sequencing decisions live
  there so they stay unit-testable; `MainWindow` performs the effects. Keep it that way.
- **One active job at a time.** A captured answer is always attributed to `QueueRunner.ActiveJobId`,
  never to "whatever was prepared last". A capture arriving with no active job is discarded.
- **Duplicate responses are refused.** An answer already accepted (hash match) is never written against
  a second job — that is how a stale Copy would corrupt another job's resume.
- **Two-strike failure policy.** The first rejected response keeps the job Processing and asks for
  another Copy; the second marks it Failed, keeps the raw diagnostic and advances, so a bad answer can
  never block the rest of the queue. Strikes reset per job and apply to manual runs too.
- **Stop re-queues the in-flight job** (Processing -> Queued); Pause lets the current job finish and
  stops advancing. Skip marks the active job Failed and advances.
- **Stale `Processing` jobs are recovered to `Queued` at startup** (`QueueRunner.RecoverStaleProcessing`),
  so a crash or a close cannot strand a job forever.
- Manual single-job processing, Retry Failed and A6.6.9 document generation must keep working unchanged;
  the queue reuses the same `RunJobAsync` path rather than duplicating it.

## Auto-Send, and why Copy stays manual (A6.6.11)

- **Never programmatically extract ChatGPT Output.** The consumer Terms prohibit automated extraction
  of Output, so the app does not click Copy and does not read assistant turns. (A6.6.13 adds
  completion detection from control state, for notification only - see below.) The answer reaches the
  app only when the *user* copies it with ChatGPT's own Copy button or shortcut and `ClipboardWatcher`
  picks it up. Do not add Auto-Copy, response scraping, or any DOM read of a reply.
- **Auto-Send actuates a control; it never reads output.** `ChatSender` + `WebViewChatProbe` check the
  Send button's state, click it, and confirm by seeing *our own* composer empty. Every probe returns a
  status token from a fixed set; a test asserts the scripts contain no `innerText`, `innerHTML`,
  `data-message-author-role`, `conversation-turn` or `markdown`, and that every `return` is a token.
- **`ChatSender` has no WebView2 reference** — orchestration is testable with a fake `IChatProbe`.
- **An automation failure is never a job failure.** Auto-Send problems leave the job Processing with the
  capture still armed, pause the queue via `PauseForManualAction`, and name the one action: press Enter.
  The two-strike Failed policy stays reserved for genuinely bad answers.
- **`PausedForManualAction` distinguishes our pause from the user's.** Only ours auto-resumes when the
  capture finally lands (`TryAutoResume`); an explicit Pause stays paused until the user resumes.
- Auto-Send applies to both queue runs and manual single-job runs, and is switchable in Settings.

## Performance (A6.6.12)

Measured baselines — treat these as regression guardrails:

- Prepare 1–9 ms, clipboard write (35 KB) 2–22 ms, capture+normalize+validate+save ~1 ms,
  DOCX 3–83 ms, PDF 650–800 ms. None of these is a latency problem; do not micro-optimise them.
- **An empty ChatGPT tab costs ~700 MB across 6 WebView2 processes.** That is the memory budget that
  matters; the managed heap stays at 4–12 MB and is never the problem.

Rules that keep it that way:

- **Every job starts a fresh ChatGPT conversation** (`NavigateFreshChatAsync`). Never go back to
  appending jobs to one page: before A6.6.12 that accumulated a 35 KB prompt plus a long answer per
  job in a single renderer and climbed toward 3–4 GB. The WebView2 user-data folder is untouched, so
  the signed-in session survives navigation.
- **The prepared payload is sent to the page once** (`BuildPayloadScript` stores `window.__rbPayload`)
  and the poll loop re-sends only the ~1.5 KB `FillScript`. Never put the payload back in the retry
  script — that was 41 KB per attempt.
- **All ChatGPT polling uses `PollPolicy`** — 100 ms growing to 600 ms, under a 5 s budget per phase.
  Failure detection is ~5 s for fill, send-ready and send-confirmation. Do not reintroduce fixed
  multi-hundred-millisecond cadences or 10–14 s ceilings.
- **`PerfLog` writes `diagnostics.log` in the app data folder**: stage timings and memory snapshots
  before/after each job and at queue start/finish. Keep it cheap (one working-set read per snapshot),
  keep it non-throwing, and keep it out of the behaviour path.
- **`GC.Collect` is not a fix.** The managed heap is not where the memory goes.

## Memory lifetime (A6.6.12)

- **The ChatGPT WebView2 is destroyed after every completed job** and rebuilt lazily at the start of
  the next one (`ChatHost` + `MainWindow.CreateChatViewAsync`/`DisposeChatViewAsync`). It is also
  released when the queue finishes or stops and when the window closes. Never recycle while a
  response is pending — only after the capture is saved and the documents are written.
- **The user-data folder is shared and never touched by recycling**, so the signed-in session
  survives. Recreating the view must always pass the same `%LOCALAPPDATA%\ResumeBuilder\WebView2`.
- **`ChatHost` holds no WPF or WebView2 references** — create and dispose are injected, so the
  lifecycle is unit-tested with counters and no browser. Keep it that way.
- **A release is only finished when the browser PROCESS has exited.** `Dispose()` returns long before
  that, so `DisposeChatViewAsync` waits on two independent signals - the environment's
  `BrowserProcessExited` event and the OS process handle - under a 10 s bounded timeout. Never treat
  `Dispose()` returning as success, and never use a sleep as the wait.
- **`ChatHost.EnsureAsync` waits for any release still in flight**, so two browser trees can never
  overlap. On timeout the code logs it clearly, drops the cached environment so the next job starts
  fresh, and reports the recycle as unconfirmed - it must never claim success.
- Disposal drops the field first, unparents the control, then disposes it. A failed teardown must
  still clear `IsAlive` so the next job can build a new browser.
- **PDF generation uses PDFsharp/MigraDoc (MIT), never a browser.** `PdfWriter.BuildDocument` returns
  the MigraDoc model and `PdfWriter.ExtractText` walks it, which is how DOCX/PDF content parity is
  asserted. Do not reintroduce WebView2 `PrintToPdfAsync`, and do not add a second PDF engine.
  Fonts come from `ResumeFontResolver` (installed Windows fonts, with fallbacks).
- Diagnostics label set to keep comparable across releases: `queue start`, `before job`, `after job`,
  `after ChatGPT WebView2 recycle`, `queue finished`/`queue stopped`, `after queue WebView2 disposal`.

## Long-run stability (A6.6.12)

- `tests\WebViewStress` drives the **production `ChatHost`** through 50 create/dispose cycles with no
  resume prompts, using a temporary user-data folder so the real ChatGPT login is never touched.
  Re-run it after any change to the browser lifetime. Measured baseline on this machine: 50 creates,
  50 confirmed exits, 0 timeouts, 0 orphans, WebView2 memory after exit flat at 262 MB across all
  cycles (spread 0 MB), managed heap 1 -> 5 MB, exit confirmed in ~200-950 ms.
- **A repeated browser pid is not a leak.** Windows reuses process ids after exit, so liveness must be
  judged by pid **and** process start time. Asserting "distinct pids" produces false failures.
- `SampleJobs` generates the Development-tab batches. Nothing is written anywhere until the user
  clicks a Development action, ids are unique per run, and the data is explicitly synthetic.

## "Answer ready" notification (A6.6.13)

- **Completion detection is for notification only.** `ChatCompletionWatcher` + `WebViewCompletionProbe`
  look at control state (is the stop-generating button present, is the composer idle) and return a
  token. They never read an answer, never click Copy, and never dispatch or synthesize input. A test
  asserts the probe script contains none of `innerText`, `innerHTML`, `textContent`,
  `data-message-author-role`, `conversation-turn`, `markdown`, `copy`, `click(`, `dispatchEvent`,
  `clipboard`, and that it only returns `generating` / `idle` / `unknown`.
- **The copy stays a human action by ChatGPT's own feature.** The app tells the user to press
  Ctrl+Shift+; (ChatGPT's "Copy last code block"; there is no "copy last response" shortcut); the
  keystroke goes from Windows to the page. Never send it with `SendInput`,
  `SendKeys`, CDP `Input.dispatchKeyEvent` or a scripted event — that would be programmatic extraction
  under another name.
- Idle must hold for 3 consecutive 1-second polls after generation was seen, so reasoning-model pauses
  do not fire early. If generation is never seen, notify only after the 30 s start budget. Give up
  after 20 minutes with a status message. Always cancellable.
- **No focus stealing.** The toast is shown non-activated with `WS_EX_NOACTIVATE`; the taskbar flash is
  used when the app is in the background; the ChatGPT pane is focused only when the app is already
  active. Clicking the toast is the user action that brings the window forward and focuses the pane.
- Verified on this machine with real keystrokes: with `AreDevToolsEnabled = false` (the app's setting),
  Ctrl+Shift+; reaches the page whether browser accelerator keys are enabled or not, provided the
  window is foreground. Do not change accelerator settings for this feature.
- The watch is dismissed on capture, Stop, Skip, queue finish, browser recycle and window close.
- **`PromptEchoGuard` refuses copies of our own prompt.** The master prompt contains code blocks, so
  "Copy last code block" can pick one up if an answer has none — and an empty schema template would
  pass validation. Captured text that is a verbatim (whitespace-collapsed) slice of the prepared request
  is refused before the pipeline, counts no strike, and the job keeps waiting.
- **Ctrl+Shift+' (`GlobalHotkey`) only moves focus.** It brings Resume Builder to the front from any app
  and focuses the ChatGPT pane, so the user's own Ctrl+Shift+; reaches ChatGPT. It must never send a
  key to ChatGPT, re-send the user's keystroke, or trigger a copy. Registration failure (another app
  owns the combination, Win32 error 1409) is reported in the status line, never thrown. Verified with a
  real keypress: with another window in front, the hotkey fired once and brought the window forward.

## Capture watchdog (A6.6.13)

- **The Copy is awaited for 30 s, never forever.** `CaptureWatchdog` starts only on a *confirmed*
  `CompletionOutcome.Ready` (generation seen, then idle) while the capture is armed. `ReadyUnconfirmed`
  (generation never observed — could be a drifted selector while ChatGPT is still writing) notifies but
  never starts it, so a page change can never fail every job.
- On timeout: disarm, mark the job `Failed` with `FailureReason = "CaptureTimeout"`, log
  `CAPTURE TIMEOUT <jobId> after 30s` plus the exact message, recycle the WebView2 as after a completed
  job, and advance. **The job is never re-sent automatically**; Retry Failed re-queues it and clears the reason.
- Cancelled (logged as `CAPTURE watchdog cancelled <jobId> (<reason>)`) on capture received, Stop, Skip,
  Pause, queue finish, second-strike failure, a new active job, and app close. Resume restarts a fresh
  30 s wait for a job whose answer was already READY. After a first-strike rejection the completion
  watch restarts, so the corrected answer's Copy is bounded too.
- The watchdog holds no I/O: `MainWindow` performs every effect, and a timer from an earlier job can only
  ever report `Cancelled` (generation counter), so it cannot fail the job active now.
- **Late responses are never attributed to the next job.** Two independent guards:
  `QueueRunner.OnCaptureTimedOut` remembers the profile-like text on the clipboard at timeout and
  `Classify` refuses it as `LateResponse`; and `ClipboardWatcher` ignores any update whose clipboard
  sequence number has not moved since arming (`ChangedSinceArm`), so a delayed WM_CLIPBOARDUPDATE can
  never read an older copy for the job armed now. Verified with the real clipboard.
- Diagnostics: `READY <jobId>`, `CAPTURE watchdog started <jobId> 30s`, `CAPTURE received <jobId>`,
  `CAPTURE TIMEOUT <jobId> after 30s`.
