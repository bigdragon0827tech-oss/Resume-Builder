# Resume Builder A6.6.13

Based on A6.6.12. Everything else is unchanged — this release adds the "answer ready" notification.

## Change in A6.6.13 — "answer ready" + one keypress

```
Start Queue
  -> ResumeBuilder fills ChatGPT and clicks Send
  -> ChatGPT generates
  -> ResumeBuilder notices generation has finished and tells you:
       * a notification on the right side of the screen
       * a short sound
       * a flashing taskbar button if Resume Builder is in the background
  -> click the notification, press Ctrl+Shift+;      <- your one action per job
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
