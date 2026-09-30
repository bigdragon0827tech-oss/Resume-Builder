# Resume Builder v3.0

Version 3.0 (assembly version 3.0.0). Version 1.0 was the first numbered release. This release carries
forward everything built in the A6.x development line.

## How an answer is captured

```
Start Queue
  -> Resume Builder fills ChatGPT and clicks Send
  -> ChatGPT generates
  -> Resume Builder waits until generation looks finished
  -> it reads the last assistant JSON from the page in the background
  -> normalize -> strict validate -> save -> DOCX/PDF -> next job
```

That read does not click Copy, change focus, or use the clipboard. If it cannot find one finished
resume JSON, copy the answer yourself with ChatGPT's Copy button or Ctrl+Shift+;. Ctrl+Shift+' only
brings Resume Builder to the front so that fallback can reach the page.

Once generation has clearly finished and the background read fails, Resume Builder waits 10 seconds
for that copy. If nothing usable arrives, the job is marked **Failed — no answer captured in time**
(CaptureTimeout) and the queue moves on. The job is not sent again automatically; use **Retry Failed**
later. A copy that arrives too late is not used for the next job.

When an answer looks ready you still get a notification, a short sound, and a taskbar flash if
Resume Builder is in the background. None of these takes focus. Each can be turned off under
Settings → General → When the answer is ready.

## Test

1. `dotnet clean`
2. `dotnet build`  (expect 0 errors, 0 warnings)
3. `dotnet run`
4. Start a queue and let the answer be captured without copying it yourself.
