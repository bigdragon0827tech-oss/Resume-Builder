# Resume Builder A6.6.7

Based on A6.6.6. Everything A6.6.6 did is unchanged; this release fixes clipboard reliability.

## Change in A6.6.7 — reliable clipboard

### What was actually wrong

Measured against the real Windows clipboard on this machine: `Clipboard.SetDataObject(data, copy: true)`
puts the text on the clipboard successfully, and then the `OleFlushClipboard` call that `copy: true`
triggers throws `CLIPBRD_E_CANT_OPEN (0x800401D0)`. The old code treated any exception as failure, so
**a copy that had actually worked was reported as "Clipboard busy"**. The old handler also caught only
`COMException`, letting plain `ExternalException` through as a hard failure, and used `Clipboard.SetText`,
which leaves the data delayed-rendered and owned by the process instead of published.

### The fix

New file `Clipboard.cs` holds the one and only clipboard implementation (`ClipboardService`).
`ClipboardService` in `Services.cs` was deleted; there is no second retry loop anywhere.

1. **STA / dispatcher correctness.** Clipboard calls are OLE calls. Work is marshalled onto the WPF UI
   dispatcher when one exists (with a timeout so a busy UI thread cannot hang a caller), and onto a
   dedicated STA thread when there is none, so background callers are safe too.
2. **Correct exception handling.** `ExternalException` is caught, which covers `COMException` and
   `CLIPBRD_E_CANT_OPEN`, plus `InvalidOperationException` and `ThreadStateException`.
3. **Publish, then flush separately.** `SetDataObject(data, copy: false)` places the data; the flush
   that makes it outlive the process is attempted afterwards and is never fatal.
4. **The clipboard decides, not the API.** Every attempt is verified by reading the clipboard back.
   A copy that landed is reported as copied even if an API call threw; a silent no-op is retried.
5. **Bounded retries.** Up to 3 attempts with 60/120/180 ms backoff under a 3 s budget (WPF itself
   already retries 10x100 ms internally), then a final fallback through the WinForms clipboard,
   which applies its own native retry loop.
6. **Preparation never depends on the clipboard.** The job is prepared and saved first; the clipboard
   is touched afterwards, so a clipboard problem can no longer make preparation look failed.
7. **A real fallback, not manual selection.** Besides `prepared-request.json`, the prepared input is
   now also written to `prepared-request.txt` in the app data folder. If a copy genuinely cannot be
   completed, the message names that file and the Job Details text is pre-selected so a single
   Ctrl+C finishes the job.

### Clipboard call sites

All clipboard access goes through `ClipboardService`:

| Location | Operation |
| --- | --- |
| `MainWindow.ProcessSelected_Click` | `ClipboardService.SetText` after the job is prepared and saved |
| `SettingsWindow.CopyPrepared_Click` | `CopyToClipboard` -> `ClipboardService.SetText` |
| `SettingsWindow.PrepareProfileCreation_Click` | `CopyToClipboard` -> `ClipboardService.SetText` |
| `SettingsWindow.PasteResult_Click` | `ClipboardService.TryGetText` |

`Clipboard.cs` is the only file that references `System.Windows.Clipboard`, `DataObject` or
`System.Windows.Forms.Clipboard`.

## Unchanged from A6.6.6

- Normalize -> strict validate -> save for the AI profile result, with all A6.6.6 variations still
  handled: `profile` wrapper, ```json fences, surrounding prose, trailing commas, skills as an object
  keyed by category, `bullets`/`highlights`/`responsibilities`/`description` -> `descriptionLines`,
  `dates` and `start_date`/`end_date` -> `startDate`/`endDate`, `institution` -> `school`,
  `field_of_study` -> `major`, info-field aliases, Markdown mailto cleanup, certification strings,
  and removal of `employment_type` / `work_arrangement` / `work_mode`.
- Strict validation after normalization is unchanged; nothing is invented; experience and education
  keep their original count and order.
- The Master Prompt is still read from the external file configured in Settings.
- The job payload is still built with `System.Text.Json`, never string concatenation.
- The canonical candidate profile schema is unchanged.

## Test

1. `dotnet clean`
2. `dotnet build`  (expect 0 errors, 0 warnings)
3. `dotnet run`
4. Prepare a queued job. Expect "Prepared ... successfully. Copied to clipboard." and paste straight
   into ChatGPT.
5. Settings -> Job Details -> Copy Prepared Input copies the whole prepared request.
6. Settings -> Result -> Validate + Save Profile still normalizes and saves.
