# ResumeBuilder — development rules for Claude Code

These rules are permanent. Follow them in every session unless the user explicitly overrides them.

## The project

- **This directory is the authoritative development project.** Current baseline: **A6.6.7**
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

- Git is installed but **this project is not currently a repository**. Do not run `git init`, commit,
  tag, or push unless the user asks.
- If it is placed under Git later: inspect `git status` / `git diff` before substantial changes, use
  history and checkpoints instead of development ZIPs, and never reset, revert, discard or overwrite
  the user's own changes without explicit authorization.

## File map

| File | Contents |
| --- | --- |
| `MainWindow.xaml(.cs)` | Task queue, Prepare Selected Job, embedded ChatGPT WebView2 |
| `SettingsWindow.xaml(.cs)` | General settings, Job Details inspector, Result capture, Development tab |
| `Services.cs` | `Storage`, `JobImporter`, `RequestPreparation`, `BaselineProfileImporter`, `CandidateProfileStore` |
| `Normalization.cs` | `ProfileNormalizer`, `NormalizationReport` — AI output -> canonical schema |
| `Clipboard.cs` | `ClipboardService` — the single STA-aware, verifying, retrying clipboard implementation |
| `Models.cs` | `JobBatch`, `JobInput`, `JobTask`, `AppSettings`, `PreparedRequest` |
