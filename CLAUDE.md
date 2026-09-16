# ResumeBuilder — development rules for Claude Code

These rules are permanent. Follow them in every session unless the user explicitly overrides them.

## The project

- **This directory is the authoritative development project.** Current baseline: **A6.6.12**
  (.NET 8 / `net8.0-windows`, WPF + WinForms interop, WebView2).
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
- When the version does change, update every reference together: `MainWindow.xaml` (window `Title`
  and the header `TextBlock`), `MainWindow.xaml.cs` (MessageBox caption), `README.md`, and any
  version text in `SettingsWindow.xaml`. Leave no mixed version strings.

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

Test harnesses are throwaway console projects that `<Compile Include="...">` the real source files
(never copies of them), built outside this directory so they never ship. Delete them afterwards.

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
| `DocumentGeneration.cs` | `ResumeDocument`, `DocxWriter`, `ResumeGenerator` — DOCX from a validated profile |
| `PdfGeneration.cs` | `PdfWriter` (PDFsharp/MigraDoc), `ResumeFontResolver` — PDF without a browser |
| `ChatHost.cs` | `ChatHost` — creates and destroys the ChatGPT WebView2 between jobs |
| `QueueRunner.cs` | `QueueRunner`, `QueueState`, `FailureOutcome` — sequencing state machine, no I/O |
| `Diagnostics.cs` | `PerfLog` (timing/memory log), `PollPolicy` (shared adaptive poll cadence) |
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
- **Filenames are `<Company> - <Role>.docx` / `.pdf` in the configured Resume Root, and overwrite.**
  Never append job ids, timestamps or "(2)".
- Documents are written to a temp file and moved into place, so a crash cannot leave a truncated file.
- The `Docx` / `Pdf` checkboxes are authoritative; both off means "disabled", which is reported as a
  skip, not a failure.

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
  of Output, so the app does not click Copy, does not read assistant turns, and has no completion
  detection. The answer reaches the app only when the *user* clicks ChatGPT's own Copy button and
  `ClipboardWatcher` picks it up. Do not add Auto-Copy, response scraping, or any DOM read of a reply.
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
