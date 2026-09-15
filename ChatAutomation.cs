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
