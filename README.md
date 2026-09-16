# Resume Builder A6.6.12

Based on A6.6.11. The AI pipeline, Auto-Send, Auto-Copy behaviour (still manual), queue, routing and
document generation are all unchanged — this release is performance only.

## Change in A6.6.12 — performance

### 1. A fresh ChatGPT conversation for every job

Previously the app navigated to ChatGPT only if it was not already there, so **every job appended
another 35 KB prompt and a long answer to one conversation**. An empty ChatGPT tab already costs
~700 MB of WebView2 memory; a growing conversation is what climbed toward 3–4 GB. Each job now starts
a new chat. Your signed-in session is untouched — cookies live in the WebView2 profile folder, not in
the page. Applies to queue runs and manual single-job runs alike.

As a side benefit, no job can see the previous job's conversation any more.

### 2. The big payload is sent to the page once

The prepared request (~40 KB as a script) used to be re-sent on **every** composer retry. It is now
stored in the page once, and the retry loop sends a **1.5 KB** script that reads it.

### 3. Faster, adaptive polling

Fill, send-readiness and send-confirmation now poll at 100 ms, backing off to 600 ms, under a 5-second
budget each — instead of fixed 700/400/500 ms cadences with 8–14 second ceilings.

| Measured | A6.6.11 | A6.6.12 |
| --- | --- | --- |
| Composer not found (worst case) | 7,848 ms | **5,250 ms** |
| Send button missing (worst case) | 14,079 ms | **5,294 ms** |
| Send unconfirmed (worst case) | 10,281 ms | **5,235 ms** |
| Script sent per retry | 41,820 chars | **1,483 chars** |
| Composer fill, happy path | 723 ms | 679 ms |

The happy path is unchanged by design — it is dominated by inserting 35 KB into the editor, not by
transfer or polling.

### 4. Diagnostics

`diagnostics.log` in `%LOCALAPPDATA%\ResumeBuilder` now records stage timings (preparation, clipboard,
navigation, fill, auto-send, capture, documents) and memory snapshots (managed / process / WebView2
processes) before and after each job and at queue start and finish. It is append-only, capped at 1 MB,
and never affects a run.

Reference measurements on this machine: prepare 1–9 ms, clipboard 2–22 ms, capture 1 ms, DOCX 3–83 ms,
PDF 650–800 ms, managed heap 4–12 MB.

## Unchanged

Everything else: normalize → strict validate → save, per-job routing, DOCX/PDF generation, the queue
with its attribution and duplicate guards, Auto-Send, manual Copy, and every manual fallback.

## Test

1. `dotnet clean`
2. `dotnet build`  (expect 0 errors, 0 warnings)
3. `dotnet run`
4. Run a 3-job queue and watch `diagnostics.log`: the memory lines should stay roughly flat instead of
   climbing, and each job should open a new ChatGPT conversation.
