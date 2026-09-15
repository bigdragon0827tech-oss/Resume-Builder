# Resume Builder A6.6.8

Based on A6.6.7. Everything A6.6.6 (normalization) and A6.6.7 (clipboard) did is unchanged.

## Change in A6.6.8 — fewer manual steps in the AI round trip

| Before (A6.6.7) | Now (A6.6.8) |
| --- | --- |
| Click Prepare | Click **Prepare & Send to ChatGPT** — the prompt is typed into the ChatGPT box for you |
| Switch app, Ctrl+V, Enter | **Press Enter** |
| Select the answer, Ctrl+C | Click ChatGPT's own **Copy** button |
| Open Settings → Result | — captured automatically |
| Ctrl+V | — |
| Click Validate + Save | — normalized, validated and saved automatically |

Five manual actions become two, and both remaining ones are deliberate.

### How it works

**Sending — `ChatAutomation.cs` (`ChatComposer`).** The prepared request is typed into the ChatGPT
composer through the WebView2 you are already signed into. The text is embedded in the injection
script with `JsonSerializer`, so quotes, backticks and newlines cannot break it. **Send is not
clicked for you** — you review and press Enter. If the composer cannot be found (page still loading,
signed out, ChatGPT changed its markup), you are told so and the full prompt is already on your
clipboard.

**Capturing — `Clipboard.cs` (`ClipboardWatcher`) + `ResultCapture.cs`.** While a request is waiting
for its answer, the app listens for clipboard changes (`WM_CLIPBOARDUPDATE`; no polling). Text that
is recognisably a candidate profile goes straight through the unchanged pipeline — **normalize →
strict validate → save**. Everything else is ignored and never stored. A recognisable but broken
answer (a truncated response, for example) is reported rather than silently dropped.

**The answer is never read out of the ChatGPT page.** Page structure changes without notice; a
drifted selector on the read path would feed a wrong or truncated answer into your resume. The
clipboard route depends on no page structure at all, and works the same with Claude or any other tool.

### Where results are saved

- `candidate-profile.json` — the **baseline** profile, the input to every job. Only the
  "Prepare Profile Creation" (BASELINE) flow writes it.
- `results\<jobId>.json` — the tailored profile for that job.
- `results\<jobId>.raw.txt` — the raw answer, kept only when a capture fails validation.

This is the A6.6.8 fix for a real problem: previously a tailored answer overwrote
`candidate-profile.json`, so the next job was tailored from the previous job's output.

### Also in A6.6.8

- The task queue now shows real progress: Queued → Processing → Completed / Failed, and **Retry
  Failed** re-queues failed jobs.
- Settings → General has two toggles (both on by default): type the request into ChatGPT
  automatically, and capture the answer from the clipboard.

### Privacy and safety

Clipboard capture is armed **only** between sending a request and receiving its answer, reads only
through `ClipboardService`, ignores the text the app itself copied, and never stores anything that is
not a profile. No credentials, cookies or API keys are used or stored; nothing bypasses ChatGPT
authentication; sending remains a human keystroke.

## Unchanged

- Normalize → strict validate → save, with every A6.6.6 variation still handled (profile wrapper,
  json fences, prose, trailing commas, skills as an object, bullets/highlights → descriptionLines,
  dates and start_date/end_date → startDate/endDate, institution → school, field_of_study → major,
  info aliases, Markdown mailto cleanup, certification strings, removal of employment_type /
  work_arrangement / work_mode).
- Strict validation is not weakened; nothing is invented; experience and education keep their count
  and order.
- The Master Prompt is still read from the external file configured in Settings.
- The job payload is still built with `System.Text.Json`.
- The canonical profile schema is unchanged.
- The manual path (Settings → Result → Paste → Validate + Save) works exactly as before and is the
  fallback for every automation failure.

## Test

1. `dotnet clean`
2. `dotnet build`  (expect 0 errors, 0 warnings)
3. `dotnet run`
4. Select the Google sample job and click **Prepare & Send to ChatGPT** — the prompt should appear in
   the ChatGPT box.
5. Press Enter, wait for the answer, click ChatGPT's Copy button — the job should flip to Completed
   and the result should appear under `results\`.
