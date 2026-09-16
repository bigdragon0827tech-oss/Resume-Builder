# Resume Builder A6.6.11

Based on A6.6.10. Normalization (A6.6.6), clipboard (A6.6.7), the AI round trip (A6.6.8), document
generation (A6.6.9) and the sequential queue (A6.6.10) are unchanged.

## Change in A6.6.11 — Auto-Send

```
Start Queue
  -> ResumeBuilder fills the ChatGPT box and clicks Send
  -> ChatGPT generates
  -> YOU click ChatGPT's Copy button          <- deliberately still manual
  -> capture -> normalize -> validate -> save -> DOCX/PDF
  -> next job fills and sends itself           until the queue is finished
```

The only step left in a normal run is clicking Copy.

### Copy stays manual on purpose

ChatGPT's consumer Terms prohibit automatically or programmatically extracting Output. So this
release automates the *Send* control and nothing else: it never clicks Copy, never reads an assistant
message, and has no generation-completion detection. The answer reaches the app only through your own
Copy click and the existing clipboard pipeline.

Auto-Send works by control actuation only — is the Send button present, is it enabled, click it, and
confirm by seeing our own prompt box empty again. A test asserts the injected scripts cannot read
response content.

### If Auto-Send cannot complete

The job stays **Processing** (nothing was rejected), the capture stays armed, the queue pauses, and
the status line names one action: *press Enter in the ChatGPT box*. When you do, the run continues by
itself — the capture auto-resumes the queue. A pause you requested with the Pause button never
auto-resumes.

### Settings

**Click ChatGPT's Send button automatically** — on by default, applies to both queue runs and manual
single-job runs. Turn it off to return to A6.6.10 behavior exactly.

## Unchanged

One active job at a time, attribution to the active job id, duplicate/stale-answer refusal, the
two-strike answer policy, Pause/Stop/Skip, Retry Failed, startup recovery of stale Processing jobs,
per-job result routing with `candidate-profile.json` as the untouched baseline, DOCX/PDF generation,
and the manual Settings → Result fallback.

## Test

1. `dotnet clean`
2. `dotnet build`  (expect 0 errors, 0 warnings)
3. `dotnet run`
4. Click **Start Queue** — the prompt should be typed in and sent without touching the keyboard.
5. When the answer finishes, click ChatGPT's **Copy**; the job completes and the next one sends itself.
