using System.Text;
using System.Text.Json;

namespace ResumeBuilder;

/// <summary>
/// Builds the extraction-only ChatGPT prompt and reads the strict JSON reply.
/// Company is taken only from that reply. A missing or uncertain company stays blank.
/// </summary>
public static class EmailExtractor {
    public sealed class Draft {
        public string Id { get; init; } = "";
        public string Sender { get; init; } = "";
        public string Company { get; init; } = "";
        public string JobTitle { get; init; } = "";
        public string JobDescription { get; init; } = "";
        public string EmailUrl { get; init; } = "";
        public string OriginalText { get; init; } = "";
    }

    public static string NewId() =>
        "ET-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-") + Guid.NewGuid().ToString("N")[..8];

    public static string BuildPrompt(string pasted, string? providedUrl) {
        var url = JobTracker.IsOpenableUrl(providedUrl) ? providedUrl!.Trim() : "";
        return """
Reply with one JSON object and nothing else. No markdown prose around it. Use this shape exactly:
{
  "sender": "",
  "company": "",
  "jobTitle": "",
  "jobDescription": "",
  "emailUrl": ""
}
Rules:
- sender is the recruiter or person who sent the message. Use "" if there is no person name.
- company is the actual hiring or client company only when the text clearly states it. If the company is missing or uncertain, use "".
- Do not use a recruiting agency, the sender's email domain, or a company name in the signature as the hiring company unless the text explicitly identifies that name as the client or hiring company.
- jobTitle is the actual role title. Use "" if none is stated.
- jobDescription is the job description only. Remove inbox metadata and the recruiter signature or footer.
- emailUrl is an explicit http or https link in the message, or the provided URL below when that is an http or https link. Otherwise "".
- Do not invent a company, title, or URL.

Provided email URL:
""" + url + "\n\nMessage:\n" + (pasted ?? "");
    }

    /// <summary>How long email extraction waits for the assistant text to stop changing.</summary>
    public const int CaptureBudgetMs = 30_000;

    /// <summary>
    /// The complete last assistant turn, including fences and prose. Nothing is cut down here;
    /// stability is decided by comparing this text across polls.
    /// </summary>
    public const string ReadScript = """
(function () {
  var list = document.querySelectorAll('[data-message-author-role="assistant"]');
  if (!list.length) return { status: 'missing', text: '', assistants: 0 };
  var last = list[list.length - 1];
  var text = (last.innerText || last.textContent || '').trim();
  if (!text) return { status: 'empty', text: '', assistants: list.length };
  return { status: 'ok', text: text, assistants: list.length };
})();
""";

    /// <summary>One log line: collapsed whitespace, capped, no control characters.</summary>
    public static string SanitizedPreview(string? text) {
        if (string.IsNullOrWhiteSpace(text)) return "(empty)";
        const int max = 120;
        var buffer = new char[max];
        var n = 0;
        var spaced = false;
        var truncated = false;
        foreach (var ch in text) {
            if (n >= max) { truncated = true; break; }
            if (char.IsControl(ch) || char.IsWhiteSpace(ch)) {
                if (n == 0 || spaced) continue;
                buffer[n++] = ' ';
                spaced = true;
                continue;
            }
            spaced = false;
            buffer[n++] = ch;
        }
        var flat = new string(buffer, 0, n).Trim();
        return truncated ? flat + "…" : flat;
    }

    /// <summary>
    /// The first extraction object in the complete reply. Fences and surrounding prose are
    /// skipped by the JSON parser. Blank or omitted fields stay blank. Null only when no
    /// JSON object can be parsed. <paramref name="error"/> is a short parser message.
    /// </summary>
    public static Draft? ParseReply(string? text, string original, string? providedUrl, out string error) {
        error = "";
        if (string.IsNullOrWhiteSpace(text)) {
            error = "empty";
            return null;
        }
        Draft? blank = null;
        string? lastError = null;
        foreach (var region in Regions(text)) {
            foreach (var (draft, parseError) in ParsedObjects(region, original, providedUrl)) {
                if (parseError is not null) {
                    lastError = parseError;
                    continue;
                }
                if (draft is null) continue;
                if (HasValue(draft)) return draft;
                blank ??= draft;
            }
        }
        if (blank is not null) return blank;
        var recovered = TryRecover(text, original, providedUrl);
        if (recovered is not null) {
            PerfLog.Line("EMAIL extract-json-recovered");
            return recovered;
        }
        error = ShortParserMessage(lastError);
        return null;
    }

    public static bool HasOpenBrace(string? text) => text is not null && text.Contains('{');
    public static bool HasCloseBrace(string? text) => text is not null && text.Contains('}');

    static bool HasValue(Draft draft) =>
        draft.Sender.Length > 0 || draft.Company.Length > 0 || draft.JobTitle.Length > 0
        || draft.JobDescription.Trim().Length > 0 || draft.EmailUrl.Length > 0;

    /// <summary>Fenced blocks first, then the complete reply. The parser, not a brace scan, reads each one.</summary>
    static IEnumerable<string> Regions(string text) {
        var s = text.Trim().TrimStart('\uFEFF');
        var i = 0;
        while (i < s.Length) {
            var start = s.IndexOf("```", i, StringComparison.Ordinal);
            if (start < 0) break;
            var content = start + 3;
            var lineEnd = s.IndexOf('\n', content);
            if (lineEnd < 0) break;
            var fenceEnd = s.IndexOf("```", lineEnd + 1, StringComparison.Ordinal);
            if (fenceEnd < 0) break;
            var inner = s[(lineEnd + 1)..fenceEnd].Trim();
            if (inner.Length > 0) yield return inner;
            i = fenceEnd + 3;
        }
        yield return s;
    }

    static IEnumerable<(Draft? Draft, string? Error)> ParsedObjects(string region, string original, string? providedUrl) {
        var utf8 = Encoding.UTF8.GetBytes(region);
        var options = new JsonReaderOptions {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        };
        string? lastError = null;
        var produced = false;
        for (var i = 0; i < utf8.Length; i++) {
            if (utf8[i] != (byte)'{') continue;
            var reader = new Utf8JsonReader(utf8.AsSpan(i), options);
            JsonDocument doc;
            try {
                doc = JsonDocument.ParseValue(ref reader);
            } catch (JsonException ex) {
                lastError = ex.Message;
                continue;
            }
            using (doc) {
                var consumed = (int)reader.BytesConsumed;
                if (doc.RootElement.ValueKind != JsonValueKind.Object || !HasExtractionKey(doc.RootElement)) {
                    i += Math.Max(0, consumed - 1);
                    continue;
                }
                produced = true;
                var draft = DraftFrom(doc.RootElement, original, providedUrl);
                yield return (draft, null);
                i += Math.Max(0, consumed - 1);
            }
        }
        if (!produced && lastError is not null)
            yield return (null, lastError);
    }

    static Draft DraftFrom(JsonElement root, string original, string? providedUrl) {
        TryReadString(root, "sender", out var sender);
        TryReadString(root, "company", out var company);
        TryReadString(root, "jobTitle", out var jobTitle);
        TryReadString(root, "jobDescription", out var jobDescription);
        TryReadString(root, "emailUrl", out var emailUrl);
        var url = emailUrl.Trim();
        if (!JobTracker.IsOpenableUrl(url)) url = "";
        if (url.Length == 0 && JobTracker.IsOpenableUrl(providedUrl))
            url = providedUrl!.Trim();
        return new Draft {
            Id = NewId(),
            Sender = sender.Trim(),
            Company = company.Trim(),
            JobTitle = jobTitle.Trim(),
            JobDescription = jobDescription,
            EmailUrl = url,
            OriginalText = original ?? ""
        };
    }

    static bool HasExtractionKey(JsonElement root) {
        foreach (var prop in root.EnumerateObject()) {
            if (prop.Name.Equals("sender", StringComparison.OrdinalIgnoreCase)
                || prop.Name.Equals("company", StringComparison.OrdinalIgnoreCase)
                || prop.Name.Equals("jobTitle", StringComparison.OrdinalIgnoreCase)
                || prop.Name.Equals("jobDescription", StringComparison.OrdinalIgnoreCase)
                || prop.Name.Equals("emailUrl", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public static string ShortParserMessage(string? message) {
        if (string.IsNullOrWhiteSpace(message)) return "no-json";
        var line = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        var path = line.IndexOf(" Path:", StringComparison.Ordinal);
        if (path > 0) line = line[..path];
        if (line.Length > 100) line = line[..100];
        return line;
    }

    static readonly string[] FieldNames = { "sender", "company", "jobTitle", "jobDescription", "emailUrl" };

    /// <summary>
    /// Used only after strict parsing fails. Reads the five top-level keys. A broken
    /// jobDescription does not discard the other fields, and nothing is filled in
    /// when a key is absent.
    /// </summary>
    static Draft? TryRecover(string text, string original, string? providedUrl) {
        foreach (var region in Regions(text)) {
            var draft = RecoverRegion(region, original, providedUrl);
            if (draft is not null && HasValue(draft)) return draft;
        }
        return null;
    }

    /// <summary>
    /// Short fields are read only from the text before jobDescription, so a quote or
    /// brace inside that long value cannot be read as another field.
    /// </summary>
    static Draft? RecoverRegion(string region, string original, string? providedUrl) {
        if (!RegionHasField(region)) return null;
        var descAt = FindKey(region, "jobDescription", 0);
        var head = descAt >= 0 ? region[..descAt] : region;
        var sender = ReadFirst(head, "sender", "company", "jobTitle", "emailUrl");
        var company = ReadFirst(head, "company", "jobTitle", "emailUrl");
        var jobTitle = ReadFirst(head, "jobTitle", "emailUrl");
        var jobDescription = "";
        var emailUrl = "";
        if (descAt >= 0) {
            jobDescription = ReadValueAfterKey(region, descAt + "jobDescription".Length + 2, "emailUrl");
            var emailAt = FindKey(region, "emailUrl", descAt + "jobDescription".Length);
            if (emailAt >= 0)
                emailUrl = ReadValueAfterKey(region, emailAt + "emailUrl".Length + 2);
        } else {
            emailUrl = ReadFirst(region, "emailUrl");
        }
        var url = emailUrl.Trim();
        if (!JobTracker.IsOpenableUrl(url)) url = "";
        if (url.Length == 0 && JobTracker.IsOpenableUrl(providedUrl))
            url = providedUrl!.Trim();
        return new Draft {
            Id = NewId(),
            Sender = sender.Trim(),
            Company = company.Trim(),
            JobTitle = jobTitle.Trim(),
            JobDescription = jobDescription,
            EmailUrl = url,
            OriginalText = original ?? ""
        };
    }

    static bool RegionHasField(string text) {
        foreach (var name in FieldNames)
            if (FindKey(text, name, 0) >= 0) return true;
        return false;
    }

    static string ReadFirst(string text, string name, params string[] later) {
        var at = FindKey(text, name, 0);
        if (at < 0) return "";
        return ReadValueAfterKey(text, at + name.Length + 2, later);
    }

    /// <summary>Index of a top-level <c>"name"</c> key, or -1.</summary>
    static int FindKey(string text, string name, int from) {
        var needle = "\"" + name + "\"";
        var i = from;
        while (i < text.Length) {
            var at = text.IndexOf(needle, i, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return -1;
            var after = SkipWs(text, at + needle.Length);
            if (after < text.Length && text[after] == ':' && IsKeyPosition(text, at))
                return at;
            i = at + needle.Length;
        }
        return -1;
    }

    static bool IsKeyPosition(string text, int quote) {
        var i = quote - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i])) i--;
        return i < 0 || text[i] is '{' or ',' or '"';
    }

    static string ReadValueAfterKey(string text, int afterName, params string[] later) {
        var colon = SkipWs(text, afterName);
        if (colon >= text.Length || text[colon] != ':') return "";
        var value = SkipWs(text, colon + 1);
        if (value >= text.Length) return "";
        if (text.AsSpan(value).StartsWith("null", StringComparison.Ordinal)) return "";
        if (text[value] != '"') return "";
        if (TryReadJsonString(text, value, out var strict, out var end) && ValueEndsCleanly(text, end, later))
            return strict;
        return ReadUntilNextKey(text, value + 1, later);
    }

    static bool ValueEndsCleanly(string text, int end, string[] later) {
        var i = SkipWs(text, end);
        if (i >= text.Length || text[i] is ',' or '}') return true;
        return later.Length > 0 && FindKey(text, later[0], end) == SkipWs(text, end);
    }

    static string ReadUntilNextKey(string text, int contentStart, string[] later) {
        var next = -1;
        foreach (var name in later) {
            var at = FindKey(text, name, contentStart);
            if (at >= 0 && (next < 0 || at < next)) next = at;
        }
        var raw = next >= 0 ? text[contentStart..next] : text[contentStart..];
        raw = raw.TrimEnd();
        if (raw.EndsWith('"')) raw = raw[..^1].TrimEnd();
        if (raw.EndsWith(',')) raw = raw[..^1].TrimEnd();
        if (raw.EndsWith('}')) raw = raw[..^1].TrimEnd();
        if (raw.EndsWith(',')) raw = raw[..^1].TrimEnd();
        return UnescapeLoose(raw);
    }

    static bool TryReadJsonString(string text, int quote, out string value, out int end) {
        value = "";
        end = quote;
        if (quote >= text.Length || text[quote] != '"') return false;
        var buffer = new StringBuilder();
        for (var i = quote + 1; i < text.Length; i++) {
            var c = text[i];
            if (c == '\\') {
                if (i + 1 >= text.Length) return false;
                var n = text[++i];
                if (n == 'u') {
                    if (i + 4 >= text.Length || !int.TryParse(text.AsSpan(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out var code))
                        return false;
                    buffer.Append((char)code);
                    i += 4;
                    continue;
                }
                buffer.Append(n switch {
                    '"' => '"', '\\' => '\\', '/' => '/',
                    'b' => '\b', 'f' => '\f', 'n' => '\n', 'r' => '\r', 't' => '\t',
                    _ => n
                });
                continue;
            }
            if (c == '"') { value = buffer.ToString(); end = i + 1; return true; }
            if (c is '\n' or '\r') return false;
            buffer.Append(c);
        }
        return false;
    }

    static string UnescapeLoose(string raw) {
        if (raw.IndexOf('\\') < 0) return raw;
        var buffer = new StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++) {
            if (raw[i] != '\\' || i + 1 >= raw.Length) { buffer.Append(raw[i]); continue; }
            var n = raw[++i];
            if (n == 'u' && i + 4 < raw.Length && int.TryParse(raw.AsSpan(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out var code)) {
                buffer.Append((char)code);
                i += 4;
                continue;
            }
            buffer.Append(n switch {
                '"' => '"', '\\' => '\\', '/' => '/',
                'b' => '\b', 'f' => '\f', 'n' => '\n', 'r' => '\r', 't' => '\t',
                _ => n
            });
        }
        return buffer.ToString();
    }

    static int SkipWs(string text, int i) {
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        return i;
    }

    /// <summary>
    /// Reads one JSON string. Names match ignoring case. A missing key is blank.
    /// If the model repeats a key, a later empty copy does not replace an earlier value.
    /// </summary>
    static bool TryReadString(JsonElement root, string name, out string value) {
        value = "";
        var seen = false;
        foreach (var prop in root.EnumerateObject()) {
            if (!prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (prop.Value.ValueKind == JsonValueKind.Null) {
                seen = true;
                continue;
            }
            if (prop.Value.ValueKind != JsonValueKind.String) continue;
            seen = true;
            var text = prop.Value.GetString() ?? "";
            if (value.Trim().Length == 0 || text.Trim().Length > 0)
                value = text;
        }
        return seen;
    }
}
