using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ResumeBuilder;

// ---------------------------------------------------------------------------
// Job browser: reading ONE job, only when the user clicks Import Current Job.
//
// Every Jobright-specific assumption lives in this file. Verified against live job pages:
//
//   * A job page is /jobs/info/<24-hex id>. The query string only ever carried tracking
//     (utm_source); the path is what names the job.
//   * <link rel="canonical"> is that address with no query string.
//   * __NEXT_DATA__ (the page's own application/json data) is ALWAYS present on a loaded job page:
//     jobResult.{jobId, jobTitle, jobSummary, coreResponsibilities, qualifications} and
//     companyResult.{companyName, companyURL}. It is written only on a full page load, so a
//     single-page-app switch can leave it describing the previous job — hence the id check.
//   * The schema.org JobPosting (JSON-LD) is served only SOMETIMES: present for a first anonymous
//     visit, absent later in the same browser. It is a fallback, never the only source.
//
// No CSS selectors, no DOM text, and never cookies, storage or tokens.
// ---------------------------------------------------------------------------

/// <summary>An extraction problem, worded for the person looking at the page.</summary>
public sealed class JobExtractionException : Exception {
    public JobExtractionException(string reason, Exception? inner = null) : base(reason, inner) { }
}

public sealed class JobrightPageExtractor : IJobPageExtractor {
    static readonly Regex JobPath = new(@"^/jobs/info/(?<id>[0-9a-f]{24})/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The only script ever sent to the page, and only on the Import click. It returns the address,
    /// the canonical link, the JSON-LD text and a handful of named fields picked out of __NEXT_DATA__
    /// inside the page — not the whole blob — and it changes nothing.
    /// </summary>
    
    public const string ReadScript = """
    (() => {
      const normalize = (ds) => {
        if (!ds) return null;

        const j = ds.jobResult || {};
        const c = ds.companyResult || {};
        const q = j.qualifications || {};

        const list = v =>
          Array.isArray(v)
            ? v.filter(x => typeof x === 'string' && x.trim().length > 0)
            : [];

        return {
          jobId: j.jobId || null,
          title: j.jobTitle || null,
          summary: j.jobSummary || null,
          responsibilities: list(j.coreResponsibilities),
          required: list(q.mustHave),
          preferred: list(q.preferredHave),
          company: c.companyName || null,
          companyUrl: c.companyURL || null,
          // The application address the page's own Apply button opens (signed-in page data only).
          applyLink: typeof j.applyLink === 'string' ? j.applyLink : null,
          originalUrl: typeof j.originalUrl === 'string' ? j.originalUrl : null
        };
      };

      const pick = () => {

        // Current Jobright source.
        const helper =
          document.getElementById('jobright-helper-job-detail-info');

        if (helper) {
          try {
            const data = JSON.parse(helper.textContent || '{}');

            if (data && data.jobResult) {
              return normalize(data);
            }
          } catch (e) {
            // Fall through to older source.
          }
        }

        // Older Jobright / Next.js source.
        const next =
          document.getElementById('__NEXT_DATA__');

        if (next) {
          try {
            const parsed = JSON.parse(next.textContent || '{}');
            const ds = parsed?.props?.pageProps?.dataSource;

            if (ds) {
              return normalize(ds);
            }
          } catch (e) {
            // JSON-LD remains available as the parser's final fallback.
          }
        }

        return null;
      };

      const canonical =
        document.querySelector('link[rel="canonical"]');

      return JSON.stringify({
        href: location.href,

        canonical:
          canonical
            ? canonical.getAttribute('href')
            : null,

        ld: Array.from(
          document.querySelectorAll(
            'script[type="application/ld+json"]'
          )
        ).map(s => s.textContent),

        next: pick()
      });
    })()
    """;

    readonly Func<string, Task<string>> _executeScript;
    readonly Func<string?> _currentUrl;

    /// <summary>
    /// Both dependencies are injected — WebView2's ExecuteScriptAsync and its current address — so
    /// the extractor has no browser reference and cannot reach the queue or storage.
    /// </summary>
    public JobrightPageExtractor(Func<string, Task<string>> executeScript, Func<string?> currentUrl) {
        _executeScript = executeScript;
        _currentUrl = currentUrl;
    }

    /// <summary>A Jobright job detail page: https, jobright.ai, /jobs/info/&lt;24-hex id&gt;.</summary>
    public static bool IsJobPage(string? url) => JobIdFromUrl(url) is not null;

    /// <summary>Jobright's id for the job in an address. Used only to recognise the page and to catch stale data.</summary>
    public static string? JobIdFromUrl(string? url) {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps) return null;
        if (!uri.Host.Equals("jobright.ai", StringComparison.OrdinalIgnoreCase)
            && !uri.Host.Equals("www.jobright.ai", StringComparison.OrdinalIgnoreCase)) return null;

        var match = JobPath.Match(uri.AbsolutePath);
        return match.Success ? match.Groups["id"].Value.ToLowerInvariant() : null;
    }

    /// <summary>
    /// Null when the browser is not on a job page (the interface's contract). Anything else that
    /// goes wrong is a <see cref="JobExtractionException"/> with a reason fit to show.
    /// </summary>
    public async Task<JobImportData?> ExtractCurrentJobAsync() {
        var url = _currentUrl();
        if (!IsJobPage(url)) return null;

        string raw;
        try {
            raw = await _executeScript(ReadScript);
        } catch (Exception ex) {
            throw new JobExtractionException("The page could not be read. Wait for it to finish loading and try again.", ex);
        }
        return Parse(raw);
    }

    /// <summary>Turns the script's result into ONE job. Pure, so it is tested without a browser.</summary>
    public static JobImportData Parse(string scriptResult) {
        var payload = Unwrap(scriptResult);

        var href = Text(payload["href"]);
        var urlId = JobIdFromUrl(href)
                    ?? throw new JobExtractionException("This is not a Jobright job page. Open a single job first.");

        // Each source is trusted only if it describes the job in the address. A single-page app can
        // change the address before its data; stale data is dropped, never imported.
        var next = payload["next"] as JsonObject;
        var nextFresh = next is not null && Text(next["jobId"]).Equals(urlId, StringComparison.OrdinalIgnoreCase);

        var posting = FindJobPosting(payload["ld"] as JsonArray);
        var postingId = posting is null ? null : Identifier(posting);
        var postingFresh = posting is not null && (postingId is null || postingId.Equals(urlId, StringComparison.OrdinalIgnoreCase));

        if (!nextFresh && !postingFresh) {
            var stale = (next is not null && Text(next["jobId"]).Length > 0) || posting is not null;
            throw new JobExtractionException(stale
                ? "The page has not finished switching to this job. Click ⟳ Refresh and try again."
                : "This page has no job details Resume Builder can read yet. It may still be loading.");
        }

        var fromNext = nextFresh ? next! : null;
        var fromPosting = postingFresh ? posting! : null;

        return new JobImportData {
            // __NEXT_DATA__ first: it is always there and its title is the one the page shows.
            Company = FirstNonEmpty(Clean(Text(fromNext?["company"])), Clean(OrganisationName(fromPosting))),
            Title = FirstNonEmpty(Clean(Text(fromNext?["title"])), Clean(Text(fromPosting?["title"]))),
            JobUrl = JobUrl(Text(payload["canonical"]), href, urlId),
            CompanyUrl = CompanyUrl(fromNext, fromPosting),
            // The full original posting when it is published; otherwise the job's own sections.
            Description = FirstNonEmpty(HtmlToText(Text(fromPosting?["description"])), Compose(fromNext)),
            ApplyUrl = ApplyUrl(fromNext)
        };
    }

    /// <summary>
    /// The application address, as Jobright's Apply button chooses it: applyLink, else originalUrl —
    /// each taken only if it passes <see cref="ApplyCapture.IsApplicationUrl"/>. Only from page data
    /// that describes this job (fromNext is null when stale). Signed-out pages carry neither: null.
    /// </summary>
    static string? ApplyUrl(JsonObject? fromNext) =>
        new[] { Text(fromNext?["applyLink"]).Trim(), Text(fromNext?["originalUrl"]).Trim() }
            .FirstOrDefault(ApplyCapture.IsApplicationUrl);

    // ---------- the five fields ----------

    /// <summary>
    /// The page's canonical address when it names this same job, else the current address. Either
    /// way the query string goes: on Jobright it only ever carries tracking, never the job.
    /// </summary>
    static string JobUrl(string canonical, string href, string urlId) {
        var chosen = JobIdFromUrl(canonical) == urlId ? canonical : href;
        return new Uri(chosen.Trim()).GetLeftPart(UriPartial.Path);
    }

    /// <summary>The company's own website, only when the page states one. Never guessed.</summary>
    static string? CompanyUrl(JsonObject? next, JsonObject? posting) {
        var candidates = new[] {
            Text(next?["companyUrl"]),
            posting?["hiringOrganization"] is JsonObject org ? FirstNonEmpty(Text(org["url"]), Text(org["sameAs"])) : ""
        };
        var url = candidates.FirstOrDefault(JobUrls.IsWebUrl);
        return url?.Trim();
    }

    /// <summary>
    /// The description as Jobright structures it when the original is not published: summary,
    /// responsibilities and qualifications of THIS job — the same sections the page shows under the
    /// title. Nothing from elsewhere on the page, and nothing reworded.
    /// </summary>
    static string Compose(JsonObject? next) {
        if (next is null) return "";

        var parts = new List<string>();
        var summary = Clean(Text(next["summary"]));
        if (summary.Length > 0) parts.Add(summary);

        void Section(string heading, string key) {
            var items = (next[key] as JsonArray ?? new JsonArray())
                .Select(i => Clean(Text(i))).Where(i => i.Length > 0).ToList();
            if (items.Count > 0) parts.Add(heading + "\n" + string.Join("\n", items.Select(i => "• " + i)));
        }

        Section("Responsibilities", "responsibilities");
        Section("Required qualifications", "required");
        Section("Preferred qualifications", "preferred");
        return string.Join("\n\n", parts);
    }

    // ---------- reading the payload ----------

    /// <summary>ExecuteScriptAsync JSON-encodes the script's return value, so the string arrives quoted.</summary>
    static JsonObject Unwrap(string scriptResult) {
        try {
            var text = (scriptResult ?? "").Trim();
            if (text.StartsWith('"')) text = JsonSerializer.Deserialize<string>(text) ?? "";
            return JsonNode.Parse(text) as JsonObject
                   ?? throw new JobExtractionException("The page returned nothing readable.");
        } catch (JsonException ex) {
            throw new JobExtractionException("The page returned nothing readable.", ex);
        }
    }

    /// <summary>The first JobPosting in any JSON-LD block, including arrays and @graph containers.</summary>
    static JsonObject? FindJobPosting(JsonArray? blocks) {
        if (blocks is null) return null;
        foreach (var block in blocks) {
            if (block is not JsonValue value || !value.TryGetValue<string>(out var source)) continue;
            JsonNode? node;
            try { node = JsonNode.Parse(source); } catch (JsonException) { continue; }
            if (Search(node) is JsonObject found) return found;
        }
        return null;
    }

    static JsonObject? Search(JsonNode? node) {
        switch (node) {
            case JsonArray array:
                foreach (var item in array) if (Search(item) is JsonObject hit) return hit;
                return null;
            case JsonObject o:
                if (IsJobPosting(o["@type"])) return o;
                return Search(o["@graph"]);
            default:
                return null;
        }
    }

    static bool IsJobPosting(JsonNode? type) => type switch {
        JsonValue v when v.TryGetValue<string>(out var s) => s == "JobPosting",
        JsonArray a => a.Any(t => t is JsonValue tv && tv.TryGetValue<string>(out var ts) && ts == "JobPosting"),
        _ => false
    };

    static string? Identifier(JsonObject posting) {
        var id = posting["identifier"] switch {
            JsonObject o => Text(o["value"]),
            JsonNode n => Text(n),
            null => ""
        };
        return id.Length == 0 ? null : id;
    }

    static string OrganisationName(JsonObject? posting) =>
        posting?["hiringOrganization"] is JsonObject org ? Text(org["name"]) : Text(posting?["hiringOrganization"]);

    static string Text(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(v => v.Length > 0) ?? "";

    static string Clean(string value) =>
        Regex.Replace(WebUtility.HtmlDecode(value ?? ""), @"\s+", " ").Trim();

    /// <summary>
    /// The posting's HTML description as readable plain text: paragraphs and list items keep their
    /// line breaks, every tag goes, entities are decoded. Nothing is added or reworded.
    /// </summary>
    public static string HtmlToText(string html) {
        if (string.IsNullOrWhiteSpace(html)) return "";

        var s = Regex.Replace(html, @"<(script|style)[^>]*>.*?</\1\s*>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        s = Regex.Replace(s, @"<\s*br\s*/?\s*>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<\s*li\b[^>]*>", "\n• ", RegexOptions.IgnoreCase);
        // </li> is left out on purpose: the next <li> already starts a new line, and ending both
        // would put a blank line between every bullet.
        s = Regex.Replace(s, @"</\s*(p|div|ul|ol|h[1-6]|tr|section)\s*>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<[^>]+>", "");
        s = WebUtility.HtmlDecode(s).Replace(' ', ' ');

        var lines = new StringBuilder();
        var blank = 0;
        foreach (var raw in s.Split('\n')) {
            var line = Regex.Replace(raw, @"[ \t\r]+", " ").Trim();
            if (line.Length == 0) { if (lines.Length > 0 && ++blank == 1) lines.Append('\n'); continue; }
            blank = 0;
            lines.Append(line).Append('\n');
        }
        return lines.ToString().Trim();
    }
}
