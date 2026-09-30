using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace ResumeBuilder;

/// <summary>Outcome of a single background read of the ChatGPT page.</summary>
public enum ChatReadStatus {
    /// <summary>A candidate response payload was extracted from the last assistant turn.</summary>
    Ok,
    /// <summary>No assistant message was found.</summary>
    Missing,
    /// <summary>Assistant text was empty.</summary>
    Empty,
    /// <summary>More than one plausible JSON payload was found — refuse rather than guess.</summary>
    Ambiguous,
    /// <summary>The page script failed or returned an unreadable payload.</summary>
    Error
}

public sealed class ChatReadResult {
    public ChatReadStatus Status { get; init; }
    public string Text { get; init; } = "";
    public string Detail { get; init; } = "";
    public int AssistantCount { get; init; }
    public bool Success => Status == ChatReadStatus.Ok && !string.IsNullOrWhiteSpace(Text);
}

/// <summary>
/// Background reader for the assistant response in the live ChatGPT WebView2.
/// Read-only: no clicks, no navigation, no clipboard, no focus. Used only after
/// <see cref="ChatCompletionWatcher"/> reports a confirmed Ready.
/// </summary>
public static class ChatResponseReader {
    /// <summary>How long successive reads must match before the payload is treated as stable.</summary>
    public const int StableMatchPolls = 2;
    public const int StablePollMs = 800;
    public const int StableBudgetMs = 8_000;

    /// <summary>
    /// Extracts the latest assistant turn. Prefers a JSON code block (the resume contract).
    /// Returns a plain object so ExecuteScriptAsync can JSON-serialize it once.
    /// Does not click, fetch, or mutate the DOM.
    /// </summary>
    public const string ReadLastAssistantScript = """
(function () {
  function assistants() {
    var a = document.querySelectorAll('[data-message-author-role="assistant"]');
    if (a && a.length) return a;
    a = document.querySelectorAll('div[data-message-author-role="assistant"]');
    if (a && a.length) return a;
    return [];
  }

  function looksLikeProfileJson(t) {
    if (!t || t.length < 40) return false;
    if (t.indexOf('{') < 0) return false;
    var hits = 0;
    if (t.indexOf('"info"') >= 0) hits++;
    if (t.indexOf('"summary"') >= 0) hits++;
    if (t.indexOf('"skills"') >= 0) hits++;
    if (t.indexOf('"experience"') >= 0) hits++;
    if (t.indexOf('"education"') >= 0) hits++;
    if (t.indexOf('"certifications"') >= 0) hits++;
    return hits >= 2;
  }

  function codeTexts(root) {
    var out = [];
    var nodes = root.querySelectorAll('pre code, code');
    for (var i = 0; i < nodes.length; i++) {
      var t = (nodes[i].textContent || '').trim();
      if (t) out.push(t);
    }
    return out;
  }

  var list = assistants();
  if (!list.length) return { status: 'missing', text: '', assistants: 0 };

  var last = list[list.length - 1];
  var codes = codeTexts(last);
  var jsonHits = [];
  for (var c = 0; c < codes.length; c++) {
    if (looksLikeProfileJson(codes[c])) jsonHits.push(codes[c]);
  }

  if (jsonHits.length > 1) {
    // Prefer the longest profile-shaped block; if two are very different, call ambiguous.
    jsonHits.sort(function (a, b) { return b.length - a.length; });
    if (jsonHits[0].length > 0 && jsonHits[1].length > jsonHits[0].length * 0.6 &&
        jsonHits[0] !== jsonHits[1]) {
      return { status: 'ambiguous', text: '', assistants: list.length };
    }
  }

  var text = '';
  if (jsonHits.length >= 1) {
    text = '```json\n' + jsonHits[0] + '\n```';
  } else {
    // Fallback: structured plain text from the last assistant turn only (not the whole page).
    text = (last.innerText || last.textContent || '').trim();
    if (!text) return { status: 'empty', text: '', assistants: list.length };
    if (!looksLikeProfileJson(text) && text.indexOf('```json') < 0) {
      // Not recognisable as the resume payload — refuse rather than capture chat prose.
      return { status: 'missing', text: '', assistants: list.length, detail: 'no-profile-json' };
    }
    if (text.indexOf('```') < 0 && looksLikeProfileJson(text)) {
      var a = text.indexOf('{');
      var b = text.lastIndexOf('}');
      if (a >= 0 && b > a) text = '```json\n' + text.substring(a, b + 1) + '\n```';
    }
  }

  if (!text) return { status: 'empty', text: '', assistants: list.length };
  return { status: 'ok', text: text, assistants: list.length };
})();
""";

    /// <summary>
    /// Reads one finished HTML resume from the last assistant turn. Used only after a confirmed Ready,
    /// and only when the request was sent as HTML. It does not click, fetch, or change the page.
    /// </summary>
    public const string ReadLastAssistantHtmlScript = """
(function () {
  function assistants() {
    var a = document.querySelectorAll('[data-message-author-role="assistant"]');
    if (a && a.length) return a;
    return [];
  }
  function looksLikeHtml(t) {
    if (!t) return false;
    var s = t.toLowerCase();
    return s.indexOf('<html') >= 0 && s.indexOf('</html>') >= 0;
  }
  function codeTexts(root) {
    var out = [];
    var nodes = root.querySelectorAll('pre code, code');
    for (var i = 0; i < nodes.length; i++) {
      var t = (nodes[i].textContent || '').trim();
      if (t) out.push(t);
    }
    return out;
  }
  var list = assistants();
  if (!list.length) return { status: 'missing', text: '', assistants: 0, detail: 'no-html' };
  var last = list[list.length - 1];
  var codes = codeTexts(last);
  var hits = [];
  for (var c = 0; c < codes.length; c++) {
    if (looksLikeHtml(codes[c])) hits.push(codes[c]);
  }
  if (hits.length > 1) {
    hits.sort(function (a, b) { return b.length - a.length; });
    if (hits[0] !== hits[1] && hits[1].length > hits[0].length * 0.6)
      return { status: 'ambiguous', text: '', assistants: list.length, detail: 'multiple-html' };
  }
  if (hits.length >= 1)
    return { status: 'ok', text: hits[0], assistants: list.length };
  var text = (last.textContent || '').trim();
  if (!looksLikeHtml(text))
    return { status: 'missing', text: '', assistants: list.length, detail: 'no-html' };
  var opens = text.toLowerCase().split('<html').length - 1;
  if (opens > 1)
    return { status: 'ambiguous', text: '', assistants: list.length, detail: 'multiple-html' };
  return { status: 'ok', text: text, assistants: list.length };
})();
""";

    /// <summary>Parse the JSON object returned by <see cref="ReadLastAssistantScript"/>.</summary>
    public static ChatReadResult ParseScriptPayload(string? executeScriptRaw) {
        if (string.IsNullOrWhiteSpace(executeScriptRaw) || executeScriptRaw == "null")
            return new ChatReadResult { Status = ChatReadStatus.Error, Detail = "null" };

        try {
            using var outer = JsonDocument.Parse(executeScriptRaw);
            var el = outer.RootElement;
            // ExecuteScriptAsync may wrap an object as a JSON value directly.
            if (el.ValueKind == JsonValueKind.String) {
                var inner = el.GetString();
                if (string.IsNullOrWhiteSpace(inner))
                    return new ChatReadResult { Status = ChatReadStatus.Error, Detail = "empty-string" };
                using var doc = JsonDocument.Parse(inner);
                return FromObject(doc.RootElement);
            }
            if (el.ValueKind == JsonValueKind.Object)
                return FromObject(el);
            return new ChatReadResult { Status = ChatReadStatus.Error, Detail = "unexpected-kind" };
        } catch (Exception ex) {
            return new ChatReadResult { Status = ChatReadStatus.Error, Detail = ex.GetType().Name };
        }
    }

    static ChatReadResult FromObject(JsonElement el) {
        var statusText = el.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
        var text = el.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        var assistants = el.TryGetProperty("assistants", out var a) && a.TryGetInt32(out var n) ? n : 0;
        var detail = el.TryGetProperty("detail", out var d) ? d.GetString() ?? "" : "";
        var status = statusText.ToLowerInvariant() switch {
            "ok" => ChatReadStatus.Ok,
            "missing" => ChatReadStatus.Missing,
            "empty" => ChatReadStatus.Empty,
            "ambiguous" => ChatReadStatus.Ambiguous,
            _ => ChatReadStatus.Error
        };
        return new ChatReadResult {
            Status = status,
            Text = text,
            Detail = detail,
            AssistantCount = assistants
        };
    }

    public static string ContentHash(string text) {
        var normalized = text.Replace("\r\n", "\n").Trim();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    /// <summary>
    /// Reads until two successive successful payloads match, or the budget expires.
    /// Pure sequencing over an injected probe so tests can drive it without WebView2.
    /// </summary>
    public static async Task<ChatReadResult> ReadStableAsync(
        Func<CancellationToken, Task<ChatReadResult>> probe,
        int budgetMs = StableBudgetMs,
        int pollMs = StablePollMs,
        int matchPolls = StableMatchPolls,
        Func<int, CancellationToken, Task>? delay = null,
        CancellationToken cancellation = default,
        bool requireStable = false) {

        delay ??= (ms, ct) => Task.Delay(ms, ct);
        ChatReadResult? lastOk = null;
        ChatReadResult? lastSeen = null;
        var matchStreak = 0;
        var waited = 0;

        while (waited <= budgetMs) {
            cancellation.ThrowIfCancellationRequested();
            var read = await probe(cancellation);
            lastSeen = read;
            if (read.Success) {
                if (lastOk is not null && ContentHash(lastOk.Text) == ContentHash(read.Text)) {
                    matchStreak++;
                    if (matchStreak >= matchPolls - 1)
                        return read;
                } else {
                    matchStreak = 0;
                    lastOk = read;
                }
            } else if (read.Status is ChatReadStatus.Ambiguous or ChatReadStatus.Error) {
                return read;
            } else {
                matchStreak = 0;
                lastOk = null;
            }

            if (waited >= budgetMs) break;
            await delay(pollMs, cancellation);
            waited += pollMs;
        }

        // Email extraction must not parse a reply that is still streaming.
        // Resume capture keeps the last complete read when the budget ends.
        if (requireStable)
            return new ChatReadResult {
                Status = ChatReadStatus.Missing,
                Detail = "unstable",
                Text = lastSeen?.Text ?? "",
                AssistantCount = lastSeen?.AssistantCount ?? 0
            };
        return lastOk ?? new ChatReadResult { Status = ChatReadStatus.Missing, Detail = "unstable" };
    }

    public static async Task<ChatReadResult> ReadStableAsync(
        CoreWebView2? web,
        CancellationToken cancellation = default,
        Func<int, CancellationToken, Task>? delay = null) {

        if (web is null)
            return new ChatReadResult { Status = ChatReadStatus.Error, Detail = "no-webview" };

        return await ReadStableAsync(
            async ct => {
                try {
                    var raw = await web.ExecuteScriptAsync(ReadLastAssistantScript);
                    return ParseScriptPayload(raw);
                } catch (Exception ex) {
                    return new ChatReadResult { Status = ChatReadStatus.Error, Detail = ex.GetType().Name };
                }
            },
            cancellation: cancellation,
            delay: delay);
    }

    /// <summary>Same stability wait as the JSON reader, using the HTML script. Call only after Ready.</summary>
    public static async Task<ChatReadResult> ReadStableHtmlAsync(
        CoreWebView2? web,
        CancellationToken cancellation = default) {

        if (web is null)
            return new ChatReadResult { Status = ChatReadStatus.Error, Detail = "no-webview" };

        return await ReadStableAsync(
            async ct => {
                try {
                    var raw = await web.ExecuteScriptAsync(ReadLastAssistantHtmlScript);
                    return ParseScriptPayload(raw);
                } catch (Exception ex) {
                    return new ChatReadResult { Status = ChatReadStatus.Error, Detail = ex.GetType().Name };
                }
            },
            cancellation: cancellation);
    }

    /// <summary>
    /// Scripts used by the write-only probes must stay free of response scraping.
    /// The background reader is the only place that may read assistant content.
    /// </summary>
    public static bool ScriptIsReadOnly(string script) {
        foreach (var forbidden in new[] {
            "click(", "dispatchEvent", "fetch(", "XMLHttpRequest",
            "localStorage", "sessionStorage", "document.cookie", ".submit(" }) {
            if (script.Contains(forbidden, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
}
