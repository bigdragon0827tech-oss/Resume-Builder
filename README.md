# Resume Builder A6.6.10

Based on A6.6.9. Normalization (A6.6.6), clipboard (A6.6.7), the AI round trip (A6.6.8) and document
generation (A6.6.9) are unchanged.

## Change in A6.6.10 — sequential queue processing

Run the whole queue instead of picking each job by hand:

```
Start Queue
  -> prepare job 1, fill the ChatGPT box   -> you press Enter -> you click Copy
  -> validate + save results\<jobId>.json  -> generate DOCX/PDF
  -> immediately prepare job 2 ...          until the queue is finished
```

**Controls:** ▶ Start Queue, ⏸ Pause / Resume, ⏹ Stop, ⏭ Skip Job. A new queue status line shows
`Queue 2 of 5 — Google — Senior Software Engineer`, and the active job is selected in the list.

### Rules that keep the run honest

- **One active job at a time.** A captured answer is attributed to the queue's active job id, never to
  "whatever was prepared last". A capture with no active job is discarded.
- **Stale answers are refused.** Clicking Copy again on an earlier response is detected and rejected,
  so an old answer can never be saved against a later job.
- **A bad answer never blocks the queue.** The first rejected response keeps the job in progress and
  asks for another Copy. The second marks it Failed, keeps the raw diagnostic, and moves on.
- **Completed, Failed and Ignored jobs are skipped**, including a job that was finished elsewhere
  while the run was in progress.
- **Pause** lets the current job finish and stops advancing. **Stop** puts the in-flight job back to
  Queued so it is never stranded. **Skip** marks the active job Failed and advances.
- **Startup recovery:** a job left `Processing` by a crash or a close is returned to `Queued` when the
  app starts, so nothing is stranded.

### Unchanged

Manual single-job processing (Prepare & Send), Retry Failed, individual Generate Documents, the
manual Settings → Result fallback, normalize → strict validate → save, per-job result routing with
`candidate-profile.json` as the untouched baseline, and the human Send + Copy steps. API mode is not
part of this release.

## Test

1. `dotnet clean`
2. `dotnet build`  (expect 0 errors, 0 warnings)
3. `dotnet run`
4. With two or more queued jobs, click **Start Queue**; press Enter in ChatGPT, click Copy, and the
   next job should prepare itself automatically.
