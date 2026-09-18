# Resume Builder v1.0

Version 1.0 (assembly version 1.0.0) is Resume Builder's first numbered release. It carries forward
everything built in the A6.x development line. The notes below describe milestones from that line and
are kept as history.

## History: A6.6.13 — "answer ready" + one keypress

A6.6.13 was based on A6.6.12 and added the "answer ready" notification.

```
Start Queue
  -> ResumeBuilder fills ChatGPT and clicks Send
  -> ChatGPT generates
  -> ResumeBuilder notices generation has finished and tells you:
       * a notification on the right side of the screen
       * a short sound
       * a flashing taskbar button if Resume Builder is in the background
  -> press Ctrl+Shift+' (from any app), then Ctrl+Shift+;   <- your action per job
     (or click the notification instead of Ctrl+Shift+')
  -> capture -> normalize -> validate -> save -> DOCX/PDF -> next job
```

### Why this stays within ChatGPT's terms

ChatGPT's consumer Terms prohibit automatically or programmatically extracting Output. Resume Builder
therefore only watches whether ChatGPT is still generating — whether its stop button is showing and
its message box is idle. It never reads the answer, never clicks Copy, and never sends keystrokes.
The copy is done by ChatGPT's own feature, in response to your own keypress.

### How it behaves

- The answer is treated as finished only after ChatGPT has stayed idle for three seconds, so a pause
  mid-answer does not trigger a false notification.
- **It never steals focus.** The notification appears without taking focus from whatever you are
  doing. Clicking it brings Resume Builder to the front and puts the cursor in the ChatGPT pane, so
  Ctrl+Shift+; goes straight to ChatGPT, which copies the answer's json code block. You can also click
  the Copy button on that code block.
- The notification disappears as soon as an answer is captured, or when you Stop, Skip, or the queue
  finishes. If no finished answer is seen within 20 minutes, the status line tells you to check.
- Verified on this machine with real keystrokes: Ctrl+Shift+; reaches the ChatGPT page with the app's
  browser settings.

### Settings → General → When the answer is ready

- Show a notification on the right side of the screen (on)
- Play a sound (on)
- Flash the taskbar button when Resume Builder is in the background (on)

## Unchanged

Auto-Send, the clipboard capture pipeline, normalize → strict validate → save, per-job routing,
DOCX/PDF generation, queue/retry/pause/stop/skip, the A6.6.12 WebView2 memory lifecycle, and the
50-cycle stress test.

## Test

1. `dotnet clean`
2. `dotnet build`  (expect 0 errors, 0 warnings)
3. `dotnet run`
4. Start a queue, switch to another app while ChatGPT generates, and wait for the notification.

## Ctrl+Shift+' — come back to Resume Builder from anywhere

Wherever you are working, press **Ctrl+Shift+'** and Resume Builder comes to the front with the cursor
already in the ChatGPT pane. Then press **Ctrl+Shift+;** to copy the answer. No mouse needed.

It only brings the window forward — it never presses anything in ChatGPT for you. If another program
already uses Ctrl+Shift+', Resume Builder says so at startup and you can click the notification
instead. It can be switched off in Settings → General (takes effect after restart).

## If a Copy is never picked up

Once ChatGPT has clearly finished an answer, Resume Builder waits 30 seconds for your Copy. If nothing
usable arrives, that job is marked **Failed — no answer captured in time** (CaptureTimeout), ChatGPT is
reset and the queue moves on to the next job, so a missed Copy can no longer stall the run. The job is
not sent again automatically; use **Retry Failed** to run it later. An answer copied too late is never
used for the next job.
