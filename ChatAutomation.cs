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

    /// <summary>
    /// Builds the injection script. The prepared text is embedded with JsonSerializer so quotes,
    /// backticks, backslashes and newlines cannot break the script — the same class of escaping bug
    /// that broke earlier versions when large prompt text was pasted into source by hand.
    /// </summary>
    public static string BuildFillScript(string text) {
        var literal = JsonSerializer.Serialize(text);
        return """
(function () {
  var text = __TEXT__;
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
""".Replace("__TEXT__", literal);
    }

    /// <summary>
    /// Fills the composer, retrying while the page is still loading. Never throws: a failure here is
    /// a convenience that did not happen, not a failed job preparation.
    /// </summary>
    public static async Task<ComposerResult> FillAsync(CoreWebView2? web, string text, int attempts = 12, int delayMs = 700) {
        if (web is null)
            return Fail("The ChatGPT browser is not available, so the prompt was not typed in for you.");
        if (string.IsNullOrEmpty(text))
            return Fail("There was nothing to send to the composer.");

        var script = BuildFillScript(text);
        string? last = null;

        for (var attempt = 1; attempt <= attempts; attempt++) {
            try {
                var raw = await web.ExecuteScriptAsync(script);
                last = Unwrap(raw);
                if (last == "ok")
                    return new ComposerResult {
                        Success = true,
                        Message = "The prompt is in the ChatGPT box — review it and press Enter to send."
                    };
            } catch (Exception ex) {
                last = "error:" + ex.Message;
            }
            if (attempt < attempts) await Task.Delay(delayMs);
        }

        return Fail(last == "no-composer"
            ? "The ChatGPT message box was not found (the page may still be loading or signed out)."
            : "The prompt could not be typed into ChatGPT automatically.");
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
    public const int ReadyAttempts = 35;      // ~14 s at 400 ms
    public const int ReadyDelayMs = 400;
    public const int ConfirmAttempts = 20;    // ~10 s at 500 ms
    public const int ConfirmDelayMs = 500;

    public const string PressEnter = " Press Enter in the ChatGPT box to send it.";

    public static async Task<SendResult> SendAsync(
        IChatProbe probe,
        CancellationToken cancellation = default,
        Func<int, CancellationToken, Task>? delay = null) {

        delay ??= (ms, ct) => Task.Delay(ms, ct);
        var lastState = "missing";

        try {
            for (var attempt = 1; attempt <= ReadyAttempts; attempt++) {
                if (cancellation.IsCancellationRequested) return Cancelled();
                lastState = await probe.CanSendAsync();
                if (lastState == "ready") break;
                if (attempt == ReadyAttempts)
                    return lastState == "missing"
                        ? Fail(SendOutcome.ControlMissing, "The ChatGPT Send button was not found.")
                        : Fail(SendOutcome.NotReady, "ChatGPT did not become ready to send.");
                await delay(ReadyDelayMs, cancellation);
            }

            if (cancellation.IsCancellationRequested) return Cancelled();
            if (await probe.ClickSendAsync() != "clicked")
                return Fail(SendOutcome.ControlMissing, "The ChatGPT Send button disappeared before it could be clicked.");

            for (var attempt = 1; attempt <= ConfirmAttempts; attempt++) {
                if (cancellation.IsCancellationRequested) return Cancelled();
                if (await probe.SendConfirmedAsync() == "sent")
                    return new SendResult { Outcome = SendOutcome.Sent, Message = "Sent to ChatGPT — click Copy on the answer when it is finished." };
                await delay(ConfirmDelayMs, cancellation);
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
