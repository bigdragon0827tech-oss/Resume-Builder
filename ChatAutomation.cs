using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace ResumeBuilder;

public sealed class ComposerResult {
    public bool Success { get; init; }
    public string Message { get; init; } = "";
}

/// <summary>
/// Writes the prepared request into the ChatGPT composer hosted in the WebView2.
///
/// This is deliberately a WRITE-ONLY automation. The response is never read out of the page:
/// the DOM of a third-party site changes without notice, and a drifted selector on the read path
/// would silently feed a truncated or wrong answer into the profile. Capturing the answer is the
/// clipboard watcher's job (see ResultCapture), which depends on no page structure at all.
///
/// Nothing here signs in, reads credentials or cookies, or presses Send. The user stays signed in
/// through the WebView2 profile they already use, and sending remains a real human keystroke.
/// </summary>
public static class ChatComposer {
    public const string ChatUrl = "https://chatgpt.com/";

    /// <summary>A6.6.12 budget for finding the composer before falling back to a manual paste.</summary>
    public const int FillBudgetMs = 5000;

    /// <summary>
    /// After a fresh navigation, NavigationCompleted can fire long before the SPA has painted the
    /// composer. This budget waits for the composer control itself — not for the navigation event.
    /// </summary>
    public const int ComposerReadyBudgetMs = 30000;

    /// <summary>
    /// Read-only probe: is the ChatGPT composer present? Returns only fixed tokens
    /// (<c>ready</c> / <c>missing</c>). Never reads message text.
    /// </summary>
    public const string ComposerReadyScript = """
(function () {
  var el = document.querySelector('#prompt-textarea')
        || document.querySelector('div[contenteditable="true"]')
        || document.querySelector('form textarea')
        || document.querySelector('textarea');
  return el ? 'ready' : 'missing';
})();
""";

    /// <summary>
    /// Waits until the composer element exists (or the budget expires). Pure sequencing over an
    /// injected probe so tests can drive it with a fake clock.
    /// </summary>
    public static async Task<bool> WaitForComposerAsync(
        Func<Task<string>> probe,
        int budgetMs = ComposerReadyBudgetMs,
        Func<int, CancellationToken, Task>? delay = null,
        CancellationToken cancellation = default) {

        delay ??= (ms, ct) => Task.Delay(ms, ct);
        foreach (var wait in PollPolicy.Delays(budgetMs).Prepend(0)) {
            if (cancellation.IsCancellationRequested) return false;
            if (wait > 0) await delay(wait, cancellation);
            try {
                if (await probe() == "ready") return true;
            } catch {
                // Keep polling; a transient script error during navigation is not fatal.
            }
        }
        return false;
    }

    /// <summary>WebView2 wrapper around <see cref="ComposerReadyScript"/>.</summary>
    public static async Task<bool> WaitForComposerAsync(
        CoreWebView2? web,
        int budgetMs = ComposerReadyBudgetMs,
        Func<int, CancellationToken, Task>? delay = null,
        CancellationToken cancellation = default) {

        if (web is null) return false;
        return await WaitForComposerAsync(
            async () => Unwrap(await web.ExecuteScriptAsync(ComposerReadyScript)) ?? "missing",
            budgetMs, delay, cancellation);
    }

    /// <summary>
    /// Stores the prepared request in the page once, so the retry loop does not re-send ~40 KB on
    /// every attempt. The text is embedded with JsonSerializer so quotes, backticks, backslashes and
    /// newlines cannot break the script — the same class of escaping bug that broke earlier versions
    /// when large prompt text was pasted into source by hand.
    /// </summary>
    public static string BuildPayloadScript(string text) {
        var literal = JsonSerializer.Serialize(text);
        return "(function () { window.__rbPayload = __TEXT__; return 'stored'; })();"
            .Replace("__TEXT__", literal);
    }

    /// <summary>
    /// The retry script: a few hundred bytes that reads the already-stored payload. This is what the
    /// poll loop sends repeatedly while the composer is still rendering.
    /// </summary>
    public const string FillScript = """
(function () {
  if (typeof window.__rbPayload !== 'string') return 'no-payload';
  var text = window.__rbPayload;
  var el = document.querySelector('#prompt-textarea')
        || document.querySelector('div[contenteditable="true"]')
        || document.querySelector('form textarea')
        || document.querySelector('textarea');
  if (!el) return 'no-composer';
  try {
    el.focus();
    if (el.tagName === 'TEXTAREA' || el.tagName === 'INPUT') {
      var setter = Object.getOwnPropertyDescriptor(window.HTMLTextAreaElement.prototype, 'value')
                || Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value');
      setter.set.call(el, text);
      el.dispatchEvent(new Event('input', { bubbles: true }));
      el.dispatchEvent(new Event('change', { bubbles: true }));
    } else {
      // contenteditable (ProseMirror): go through the real editing pipeline so the editor
      // receives proper beforeinput/input events instead of a detached DOM mutation.
      var range = document.createRange();
      range.selectNodeContents(el);
      var sel = window.getSelection();
      sel.removeAllRanges();
      sel.addRange(range);
      if (!document.execCommand('insertText', false, text)) {
        el.textContent = text;
        el.dispatchEvent(new Event('input', { bubbles: true }));
      }
    }
    el.scrollIntoView({ block: 'center' });
    return 'ok';
  } catch (e) {
    return 'error:' + (e && e.message ? e.message : e);
  }
})();
""";

    /// <summary>
    /// Fills the composer, retrying while the page is still loading. Never throws: a failure here is
    /// a convenience that did not happen, not a failed job preparation.
    /// </summary>
    public static async Task<ComposerResult> FillAsync(
        CoreWebView2? web, string text,
        int budgetMs = FillBudgetMs,
        Func<int, CancellationToken, Task>? delay = null,
        CancellationToken cancellation = default) {

        if (web is null)
            return Fail("The ChatGPT browser is not available, so the prompt was not typed in for you.");
        if (string.IsNullOrEmpty(text))
            return Fail("There was nothing to send to the composer.");

        delay ??= (ms, ct) => Task.Delay(ms, ct);
        string? last = null;

        // One large round trip; every retry below is a few hundred bytes.
        await StoreAsync(web, text);

        foreach (var wait in PollPolicy.Delays(budgetMs).Prepend(0)) {
            if (cancellation.IsCancellationRequested) break;
            if (wait > 0) await delay(wait, cancellation);

            try {
                last = Unwrap(await web.ExecuteScriptAsync(FillScript));
                if (last == "ok")
                    return new ComposerResult {
                        Success = true,
                        Message = "The prompt is in the ChatGPT box — review it and press Enter to send."
                    };
                // A navigation between storing and filling clears page globals; put it back.
                if (last == "no-payload") await StoreAsync(web, text);
            } catch (Exception ex) {
                last = "error:" + ex.Message;
            }
        }

        return Fail(last == "no-composer"
            ? "The ChatGPT message box was not found (the page may still be loading or signed out)."
            : "The prompt could not be typed into ChatGPT automatically.");
    }

    static async Task StoreAsync(CoreWebView2 web, string text) {
        try { await web.ExecuteScriptAsync(BuildPayloadScript(text)); } catch { /* the retry loop reports it */ }
    }

    /// <summary>ExecuteScriptAsync returns a JSON-encoded value; "ok" arrives as "\"ok\"".</summary>
    static string? Unwrap(string? raw) {
        if (string.IsNullOrWhiteSpace(raw) || raw == "null") return null;
        try {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.ValueKind == JsonValueKind.String ? doc.RootElement.GetString() : raw;
        } catch {
            return raw;
        }
    }

    static ComposerResult Fail(string message) => new() {
        Success = false,
        Message = message + " The full prompt is on your clipboard — press Ctrl+V in ChatGPT."
    };
}

// ---------------------------------------------------------------------------
// A6.6.11 — Auto-Send.
//
// Sending is an ACTUATION of a control, not an extraction of output: every probe below reports
// button state (present / disabled / clicked) or whether our own composer emptied, and returns a
// short status token. Nothing here reads an assistant turn, and nothing here copies the answer.
// The response still reaches the application only when the user clicks ChatGPT's own Copy button
// and ClipboardWatcher picks it up — that path is deliberately untouched.
// ---------------------------------------------------------------------------

public enum SendOutcome { Sent, NotReady, ControlMissing, Unconfirmed, Cancelled }

public sealed class SendResult {
    public SendOutcome Outcome { get; init; }
    public bool Success => Outcome == SendOutcome.Sent;
    /// <summary>On failure, the single manual action the user has to take.</summary>
    public string Message { get; init; } = "";
}

/// <summary>Control-state probe. Implemented over WebView2 in the app and faked in tests.</summary>
public interface IChatProbe {
    Task<string> CanSendAsync();        // "ready" | "disabled" | "missing"
    Task<string> ClickSendAsync();      // "clicked" | "missing"
    Task<string> SendConfirmedAsync();  // "sent" | "pending"
}

/// <summary>
/// Clicks Send, with no knowledge of WebView2 so the sequencing can be tested directly.
/// A failure here never fails the job: the prompt is in the box and on the clipboard, and the
/// caller reports the one keystroke needed instead.
/// </summary>
public static class ChatSender {
    /// <summary>
    /// A6.6.12: each phase gets roughly five seconds of adaptive polling (100 ms, growing to 600 ms)
    /// instead of a fixed 400/500 ms cadence. The happy path reacts in ~100 ms and a broken page is
    /// reported in about five seconds rather than ten to fourteen.
    /// </summary>
    public const int ReadyBudgetMs = 5000;
    public const int ConfirmBudgetMs = 5000;

    public const string PressEnter = " Press Enter in the ChatGPT box to send it.";

    public static async Task<SendResult> SendAsync(
        IChatProbe probe,
        CancellationToken cancellation = default,
        Func<int, CancellationToken, Task>? delay = null) {

        delay ??= (ms, ct) => Task.Delay(ms, ct);
        var lastState = "missing";

        try {
            var ready = false;
            foreach (var wait in PollPolicy.Delays(ReadyBudgetMs).Prepend(0)) {
                if (cancellation.IsCancellationRequested) return Cancelled();
                if (wait > 0) await delay(wait, cancellation);
                lastState = await probe.CanSendAsync();
                if (lastState == "ready") { ready = true; break; }
            }

            if (!ready)
                return lastState == "missing"
                    ? Fail(SendOutcome.ControlMissing, "The ChatGPT Send button was not found.")
                    : Fail(SendOutcome.NotReady, "ChatGPT did not become ready to send.");

            if (cancellation.IsCancellationRequested) return Cancelled();
            if (await probe.ClickSendAsync() != "clicked")
                return Fail(SendOutcome.ControlMissing, "The ChatGPT Send button disappeared before it could be clicked.");

            foreach (var wait in PollPolicy.Delays(ConfirmBudgetMs).Prepend(0)) {
                if (cancellation.IsCancellationRequested) return Cancelled();
                if (wait > 0) await delay(wait, cancellation);
                if (await probe.SendConfirmedAsync() == "sent")
                    return new SendResult { Outcome = SendOutcome.Sent, Message = "Sent to ChatGPT — click Copy on the answer when it is finished." };
            }

            return Fail(SendOutcome.Unconfirmed, "The Send click could not be confirmed.");
        } catch (OperationCanceledException) {
            return Cancelled();
        } catch (Exception ex) {
            return Fail(SendOutcome.ControlMissing, "Automatic sending failed (" + ex.GetType().Name + ").");
        }
    }

    static SendResult Fail(SendOutcome outcome, string message) =>
        new() { Outcome = outcome, Message = message + PressEnter };

    static SendResult Cancelled() =>
        new() { Outcome = SendOutcome.Cancelled, Message = "Sending was cancelled." };
}

/// <summary>
/// The WebView2 side of Auto-Send. Each script returns a status token only.
/// The composer check reads the length of OUR OWN input element; no assistant turn is touched.
/// </summary>
public sealed class WebViewChatProbe : IChatProbe {
    readonly CoreWebView2 _web;
    public WebViewChatProbe(CoreWebView2 web) => _web = web;

    public const string SendButtonLookup =
        "var b = document.querySelector('button[data-testid=\"send-button\"]') " +
        "|| document.querySelector('button[aria-label*=\"Send\"]');";

    public const string CanSendScript = """
(function () {
  __LOOKUP__
  if (!b) return 'missing';
  if (b.disabled || b.getAttribute('aria-disabled') === 'true') return 'disabled';
  if (b.getAttribute('data-testid') === 'stop-button') return 'disabled';
  return 'ready';
})();
""";

    public const string ClickSendScript = """
(function () {
  __LOOKUP__
  if (!b) return 'missing';
  b.click();
  return 'clicked';
})();
""";

    /// <summary>Confirmation reads our own composer and the stop control — never a response.</summary>
    public const string SendConfirmedScript = """
(function () {
  var el = document.querySelector('#prompt-textarea')
        || document.querySelector('div[contenteditable="true"]')
        || document.querySelector('form textarea')
        || document.querySelector('textarea');
  var empty = !el || ((el.value !== undefined && el.value !== null)
      ? el.value.trim().length === 0
      : (el.textContent || '').trim().length === 0);
  var stopping = !!(document.querySelector('button[data-testid="stop-button"]')
      || document.querySelector('button[aria-label*="Stop"]'));
  return (empty || stopping) ? 'sent' : 'pending';
})();
""";

    public static string Script(string template) => template.Replace("__LOOKUP__", SendButtonLookup);

    public Task<string> CanSendAsync() => RunAsync(Script(CanSendScript), "missing");
    public Task<string> ClickSendAsync() => RunAsync(Script(ClickSendScript), "missing");
    public Task<string> SendConfirmedAsync() => RunAsync(SendConfirmedScript, "pending");

    async Task<string> RunAsync(string script, string fallback) {
        try {
            var raw = await _web.ExecuteScriptAsync(script);
            if (string.IsNullOrWhiteSpace(raw) || raw == "null") return fallback;
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.ValueKind == JsonValueKind.String ? doc.RootElement.GetString() ?? fallback : fallback;
        } catch {
            return fallback;
        }
    }
}

// ---------------------------------------------------------------------------
// A6.6.13 — "answer ready" notification.
//
// This watches CONTROL STATE ONLY: is ChatGPT's stop-generating button present, and is the composer
// idle. It never reads an assistant message, never clicks Copy, and never synthesizes a keystroke.
// Its only job is to tell the user the answer looks finished, so that THEY can press ChatGPT's own
// copy shortcut. The copy itself stays a human action performed by ChatGPT's own feature.
// ---------------------------------------------------------------------------

public enum CompletionOutcome {
    /// <summary>Generation was seen and has finished. Only this starts the A6.6.13 capture watchdog.</summary>
    Ready,
    TimedOut,
    Cancelled,
    /// <summary>
    /// Generation was never observed within the start budget: it finished before the first poll, never
    /// began, or the page's controls changed. The user is still notified, but nothing is failed on it.
    /// Kept for compatibility; the watcher now reports this through its onUnconfirmedReady callback and
    /// keeps watching, so an ambiguous state ends as a capture or as NoResponseStart.
    /// </summary>
    ReadyUnconfirmed,

    /// <summary>A confirmed Send produced no generation at all within the response-start budget.</summary>
    NoResponseStart,

    /// <summary>Generation started, then made no progress for the inactivity budget without finishing.</summary>
    Stalled
}

/// <summary>Generation-state probe. Implemented over WebView2 in the app and faked in tests.</summary>
public interface ICompletionProbe {
    Task<string> GenerationStateAsync();   // "generating" | "idle" | "unknown"
}

/// <summary>
/// Decides when an answer looks finished, with no WebView2 reference so it can be tested directly.
/// A reasoning model can pause mid-answer, so "idle" must hold for several consecutive polls before
/// the answer is reported ready. A false early cue only costs a premature keypress — the validator
/// still rejects a truncated answer — but the debounce keeps that rare.
/// </summary>
public static class ChatCompletionWatcher {
    /// <summary>ChatGPT's own "Copy last code block" shortcut; the answer is requested as one json code block.</summary>
    public const string ShortcutText = "Ctrl+Shift+;";

    /// <summary>How often the state is polled while an answer is generating.</summary>
    public const int PollMs = 1000;
    /// <summary>Consecutive idle polls required before the answer is treated as finished.</summary>
    public const int StablePolls = 3;
    /// <summary>How long to wait to see generation start before assuming it already finished.</summary>
    public const int StartBudgetMs = 30_000;
    /// <summary>Upper bound on waiting for one answer; long resumes can take several minutes.</summary>
    public const int MaxWaitMs = 20 * 60 * 1000;

    /// <summary>
    /// How long a confirmed Send has to produce SOMETHING (generation seen, or a capture) before the
    /// attempt is treated as never started. Deliberately far longer than the capture watchdog.
    /// </summary>
    public const int ResponseStartMs = 180_000;

    /// <summary>
    /// Once generation has been seen, how long with no further "generating" poll and no confirmed
    /// finish before the answer is treated as stalled. An inactivity budget, not a fixed ceiling:
    /// while ChatGPT is demonstrably still generating, the wait simply continues.
    /// </summary>
    public const int InactivityMs = 120_000;

    /// <summary>
    /// Watches one answer. <paramref name="onUnconfirmedReady"/> fires ONCE, at the start budget, when
    /// the page looks idle but generation was never observed — an ambiguous state (a very fast answer,
    /// nothing started, or drifted controls). The user is notified then, and the watch CONTINUES: if a
    /// capture lands the caller cancels this watch, and if nothing is seen by <see cref="ResponseStartMs"/>
    /// the attempt is reported as <see cref="CompletionOutcome.NoResponseStart"/>. That way an
    /// ambiguous state can neither strand the queue nor cause an immediate duplicate send.
    /// </summary>
    public static async Task<CompletionOutcome> WaitForAnswerAsync(
        ICompletionProbe probe,
        CancellationToken cancellation = default,
        Func<int, CancellationToken, Task>? delay = null,
        Action? onUnconfirmedReady = null) {

        delay ??= (ms, ct) => Task.Delay(ms, ct);
        var waited = 0;
        var sawGenerating = false;
        var idleStreak = 0;
        var lastProgressMs = 0;          // last poll that showed generation
        var notifiedUnconfirmed = false;

        try {
            while (true) {
                if (cancellation.IsCancellationRequested) return CompletionOutcome.Cancelled;

                string state;
                try { state = await probe.GenerationStateAsync(); }
                catch { state = "unknown"; }

                if (state == "generating") {
                    sawGenerating = true;
                    lastProgressMs = waited;
                    idleStreak = 0;
                } else if (state == "idle") {
                    idleStreak++;
                    // Normal case: generation was seen, and it has now been idle long enough.
                    if (sawGenerating && idleStreak >= StablePolls) {
                        // GPT finishes work!
                        return CompletionOutcome.Ready;
                    }
                    // Generation was never observed within the start budget: it either finished
                    // before the first poll or never began. Tell the user once, then keep watching.
                    if (!sawGenerating && waited >= StartBudgetMs && idleStreak >= StablePolls && !notifiedUnconfirmed) {
                        notifiedUnconfirmed = true;
                        onUnconfirmedReady?.Invoke();
                    }
                } else {
                    idleStreak = 0;   // an unreadable page proves nothing
                }

                // Nothing ever started: this attempt produced no answer at all.
                if (!sawGenerating && waited >= ResponseStartMs) return CompletionOutcome.NoResponseStart;

                // It started, then stopped making progress without finishing.
                if (sawGenerating && waited - lastProgressMs >= InactivityMs) return CompletionOutcome.Stalled;

                if (waited >= MaxWaitMs) return CompletionOutcome.TimedOut;
                await delay(PollMs, cancellation);
                waited += PollMs;
            }
        } catch (OperationCanceledException) {
            return CompletionOutcome.Cancelled;
        }
    }
}

/// <summary>WebView2 side of the completion probe: returns a status token, reads no output.</summary>
public sealed class WebViewCompletionProbe : ICompletionProbe {
    readonly CoreWebView2 _web;
    public WebViewCompletionProbe(CoreWebView2 web) => _web = web;

    /// <summary>Checks for the stop control and for our own composer. Nothing else is touched.</summary>
    public const string GenerationStateScript = """
(function () {
  var stopping = document.querySelector('button[data-testid="stop-button"]')
              || document.querySelector('button[aria-label*="Stop"]');
  if (stopping) return 'generating';
  var composer = document.querySelector('#prompt-textarea')
              || document.querySelector('div[contenteditable="true"]')
              || document.querySelector('textarea');
  return composer ? 'idle' : 'unknown';
})();
""";

    public async Task<string> GenerationStateAsync() {
        try {
            var raw = await _web.ExecuteScriptAsync(GenerationStateScript);
            if (string.IsNullOrWhiteSpace(raw) || raw == "null") return "unknown";
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.ValueKind == JsonValueKind.String ? doc.RootElement.GetString() ?? "unknown" : "unknown";
        } catch {
            return "unknown";
        }
    }
}
