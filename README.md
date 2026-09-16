# Resume Builder A6.6.12

Based on A6.6.11. Behaviour is unchanged — this release is performance and memory only.

## Performance (first part of A6.6.12)

- **A fresh ChatGPT conversation for every job.** Previously every job appended another 35 KB prompt
  and a long answer to one page.
- **The big payload is sent to the page once** (~40 KB stored as `window.__rbPayload`); the composer
  retry loop sends a 1.5 KB script instead of re-sending 41 KB per attempt.
- **Adaptive polling** at 100 ms backing off to 600 ms, with a 5-second budget per phase.

| Measured | A6.6.11 | A6.6.12 |
| --- | --- | --- |
| Composer not found (worst case) | 7,848 ms | 5,250 ms |
| Send button missing (worst case) | 14,079 ms | 5,294 ms |
| Send unconfirmed (worst case) | 10,281 ms | 5,235 ms |
| Script sent per retry | 41,820 chars | 1,483 chars |

## Memory (second part of A6.6.12)

Measurement first: the managed heap is 4–12 MB and was never the problem. **A loaded ChatGPT tab
costs ~700 MB across six WebView2 processes** — that is the entire memory story.

### 1. The ChatGPT browser is destroyed after every completed job

Once the answer is captured, validated, saved and turned into documents, nothing needs the browser.
It is disposed completely and rebuilt when the next job starts. It is also released when the queue
finishes or stops, and when the window closes. **It is never recycled while a response is pending.**

Your sign-in is unaffected: cookies live in the shared `%LOCALAPPDATA%\ResumeBuilder\WebView2`
folder, which recycling never touches.

### 2. PDF generation no longer uses a browser

PDFs now come from **PDFsharp/MigraDoc (MIT)** instead of a hidden WebView2 printing HTML. That
removes two browser processes and ~80 MB of transient renderer memory per document, and makes PDF
generation roughly five times faster. No Word, no COM, no Office, no browser. DOCX and PDF are still
rendered from the same `ResumeDocument`, and a test asserts they carry identical content in the same
order.

### 3. Diagnostics for comparing runs

`diagnostics.log` records `queue start`, `before job`, `after job`, `after ChatGPT WebView2 recycle`,
`queue finished` and `after queue WebView2 disposal`, each with managed / process / WebView2 memory.

Verified on this machine: with ChatGPT loaded the app adds 6 WebView2 processes (~716 MB); after
closing, **0 orphaned processes** remain.

## Unchanged

Auto-Send, manual Copy, the ClipboardWatcher pipeline, normalize → strict validate → save, per-job
routing with `candidate-profile.json` as the untouched baseline, DOCX output, and every queue,
retry, pause, stop and skip behaviour.

## Test

1. `dotnet clean`
2. `dotnet build`  (expect 0 errors, 0 warnings)
3. `dotnet run`
4. Run a 3-job queue and read `diagnostics.log`: the `after ChatGPT WebView2 recycle` lines should
   drop back to a low baseline after every job instead of climbing.
