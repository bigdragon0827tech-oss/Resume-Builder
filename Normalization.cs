using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ResumeBuilder;

/// <summary>
/// Result of converting a raw AI response into the canonical ResumeBuilder profile schema.
/// </summary>
public sealed class NormalizationReport {
    public JsonObject Profile { get; set; } = new();
    public List<string> Changes { get; } = new();
    public List<string> Dropped { get; } = new();
    public bool Changed => Changes.Count > 0 || Dropped.Count > 0;

    public string Describe() {
        if (!Changed) return "No normalization was required; the response already matched the canonical schema.";
        var sb = new StringBuilder();
        if (Changes.Count > 0) {
            sb.AppendLine("Normalized:");
            foreach (var c in Changes.Take(40)) sb.AppendLine("  - " + c);
            if (Changes.Count > 40) sb.AppendLine($"  - ...and {Changes.Count - 40} more");
        }
        if (Dropped.Count > 0) {
            sb.AppendLine("Removed non-schema fields:");
            foreach (var d in Dropped.Take(40)) sb.AppendLine("  - " + d);
            if (Dropped.Count > 40) sb.AppendLine($"  - ...and {Dropped.Count - 40} more");
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// Converts tolerated AI output variations into the canonical profile schema:
/// info, summary, skills[], experience[], certifications[], education[].
/// Nothing here fabricates resume facts; it only renames, reshapes and drops fields.
/// </summary>
public static class ProfileNormalizer {
    static readonly JsonDocumentOptions DocOptions = new() {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>Write options with an explicit resolver, so serializing the normalized tree can never fail.</summary>
    static readonly JsonSerializerOptions WriteOptions = new() {
        WriteIndented = true,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };

    public static JsonNode Parse(string json) =>
        JsonNode.Parse(json, null, DocOptions) ?? throw new InvalidDataException("The response did not contain JSON.");

    public static string ToCanonicalJson(JsonNode node) => node.ToJsonString(WriteOptions);

    /// <summary>Adds a plain string. The cast keeps JsonArray from boxing it as a customized value.</summary>
    static void AddLine(JsonArray array, string value) => array.Add((JsonNode?)value);

    public static NormalizationReport Normalize(JsonNode root) {
        var report = new NormalizationReport();

        var obj = root as JsonObject
                  ?? throw new InvalidDataException("The response must be a JSON object.");

        // Unwrap common envelopes such as { "profile": { ... } }.
        foreach (var wrapper in new[] { "profile", "updatedProfile", "updated_profile", "candidateProfile", "candidate_profile", "resume" }) {
            var found = FindProperty(obj, wrapper);
            if (found.Value is JsonObject inner && LooksLikeProfile(inner)) {
                report.Changes.Add($"unwrapped top-level \"{found.Name}\" wrapper");
                obj = inner;
                break;
            }
        }

        var map = Map(obj);
        var used = new HashSet<string>(StringComparer.Ordinal);

        var profile = new JsonObject {
            ["info"] = NormalizeInfo(Use(map, used, "info", "contact", "contactInfo", "personalInfo", "basics", "header"), report),
            ["summary"] = NormalizeSummary(Use(map, used, "summary", "professionalSummary", "objective", "about"), report),
            ["skills"] = NormalizeSkills(Use(map, used, "skills", "skillGroups", "technicalSkills", "skillCategories"), report),
            ["experience"] = NormalizeExperience(Use(map, used, "experience", "workExperience", "employment", "employmentHistory", "workHistory", "positions"), report),
            ["certifications"] = NormalizeCertifications(Use(map, used, "certifications", "certificates", "certification", "licenses"), report),
            ["education"] = NormalizeEducation(Use(map, used, "education", "educationHistory", "academics", "schools"), report)
        };

        // Style system: the optional style block. It is kept only after normalization, so what lands in the
        // profile is always a complete, in-range style — never raw AI values.
        var style = Use(map, used, "style", "styling", "format", "formatting");
        if (style is not null) NormalizeStyle(style, profile, report);

        ReportLeftovers(map, used, "", report);
        report.Profile = profile;
        return report;
    }

    // ---------- style ----------

    /// <summary>
    /// The style block is run through the preset merge, the range clamps and the hard
    /// typography rules before it is stored, so the saved profile can only ever contain a style the
    /// renderers accept. Every correction is reported rather than applied silently.
    /// </summary>
    static void NormalizeStyle(JsonNode style, JsonObject profile, NormalizationReport report) {
        var normalized = StyleNormalizer.Normalize(style);
        foreach (var warning in normalized.Warnings) report.Changes.Add("style: " + warning);
        profile["style"] = normalized.Style.ToJson();
    }

    static bool LooksLikeProfile(JsonObject o) {
        var m = Map(o);
        return m.ContainsKey("info") || m.ContainsKey("experience") || m.ContainsKey("summary") || m.ContainsKey("skills");
    }

    // ---------- info ----------

    static JsonObject NormalizeInfo(JsonNode? node, NormalizationReport report) {
        var info = new JsonObject {
            ["name"] = "", ["title"] = "", ["location"] = "",
            ["email"] = "", ["phone"] = "", ["linkedin"] = ""
        };
        if (node is not JsonObject o) {
            if (node is not null) report.Changes.Add("info was not an object; replaced with an empty info block");
            return info;
        }

        var map = Map(o);
        var used = new HashSet<string>(StringComparer.Ordinal);

        info["name"] = Text(Use(map, used, "name", "fullName", "candidateName", "displayName"));
        info["title"] = Text(Use(map, used, "title", "headline", "jobTitle", "currentTitle", "role"));
        info["location"] = Text(Use(map, used, "location", "city", "address", "region"));

        var email = CleanContact(Text(Use(map, used, "email", "emailAddress", "mail")));
        if (email.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) email = email.Substring(7).Trim();
        info["email"] = email;

        info["phone"] = Text(Use(map, used, "phone", "phoneNumber", "mobile", "telephone", "tel"));
        info["linkedin"] = CleanContact(Text(Use(map, used, "linkedin", "linkedinUrl", "linkedinProfile", "linkedinLink")));

        ReportLeftovers(map, used, "info.", report);
        return info;
    }

    /// <summary>Turns "[a@b.com](mailto:a@b.com)" into "a@b.com" and leaves a plain value untouched.</summary>
    static string CleanContact(string value) {
        var v = value.Trim();
        var m = Regex.Match(v, @"^\[(?<text>[^\]]*)\]\((?<url>[^)]*)\)$");
        if (!m.Success) return v;
        var text = m.Groups["text"].Value.Trim();
        var url = m.Groups["url"].Value.Trim();
        var result = text.Length > 0 ? text : url;
        if (result.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) result = result.Substring(7);
        return result.Trim();
    }

    // ---------- summary ----------

    static JsonNode NormalizeSummary(JsonNode? node, NormalizationReport report) {
        if (node is JsonArray arr) {
            report.Changes.Add("summary was an array; joined into a single string");
            return string.Join(" ", arr.Select(Text).Where(s => s.Length > 0));
        }
        if (node is JsonObject o) {
            report.Changes.Add("summary was an object; flattened into a single string");
            return string.Join(" ", o.Select(p => Text(p.Value)).Where(s => s.Length > 0));
        }
        return Text(node);
    }

    // ---------- skills ----------

    static JsonArray NormalizeSkills(JsonNode? node, NormalizationReport report) {
        var result = new JsonArray();
        if (node is null) return result;

        // Variation A: object keyed by category -> array of { category, skills }.
        if (node is JsonObject grouped) {
            report.Changes.Add("skills was an object keyed by category; converted to the canonical array of { category, skills }");
            foreach (var pair in grouped) {
                result.Add(new JsonObject {
                    ["category"] = pair.Key.Trim(),
                    ["skills"] = StringArray(pair.Value)
                });
            }
            return result;
        }

        if (node is not JsonArray array) {
            report.Changes.Add("skills was not an array or object; replaced with an empty array");
            return result;
        }

        var loose = new JsonArray();
        int index = 0;
        foreach (var item in array) {
            if (item is JsonObject o) {
                var map = Map(o);
                var used = new HashSet<string>(StringComparer.Ordinal);
                var category = Text(Use(map, used, "category", "group", "name", "heading", "label", "title"));
                var values = Use(map, used, "skills", "items", "values", "list", "entries", "keywords");
                ReportLeftovers(map, used, $"skills[{index}].", report);
                result.Add(new JsonObject { ["category"] = category, ["skills"] = StringArray(values) });
            } else {
                var s = Text(item);
                if (s.Length > 0) AddLine(loose, s);
            }
            index++;
        }

        if (loose.Count > 0) {
            report.Changes.Add("skills contained bare strings; grouped them under a single \"Skills\" category");
            result.Add(new JsonObject { ["category"] = "Skills", ["skills"] = loose });
        }
        return result;
    }

    // ---------- experience ----------

    static JsonArray NormalizeExperience(JsonNode? node, NormalizationReport report) {
        var result = new JsonArray();
        var items = AsItemArray(node, "experience", report);
        int index = 0;
        foreach (var raw in items) {
            if (raw is not JsonObject o) { index++; continue; }
            var map = Map(o);
            var used = new HashSet<string>(StringComparer.Ordinal);

            var title = Text(Use(map, used, "title", "position", "role", "jobTitle"));
            var company = Text(Use(map, used, "company", "employer", "organization", "companyName", "orgName"));
            var location = Text(Use(map, used, "location", "city", "place"));

            var start = Text(Use(map, used, "startDate", "start", "from", "beginDate"));
            var end = Text(Use(map, used, "endDate", "end", "to", "finishDate"));
            var range = Text(Use(map, used, "dates", "dateRange", "period", "duration", "timeframe", "tenure"));
            if ((start.Length == 0 || end.Length == 0) && range.Length > 0) {
                var split = SplitDateRange(range);
                if (start.Length == 0) start = split.Start;
                if (end.Length == 0) end = split.End;
                report.Changes.Add($"experience[{index}]: split a combined date range into startDate/endDate");
            }

            // Style system: metadata the renderer prints on its own line, and an optional project subtitle.
            var employmentType = Text(Use(map, used, "employmentType", "employment_type", "employment", "jobType", "contractType"));
            var workArrangement = Text(Use(map, used, "workArrangement", "work_arrangement", "workMode", "work_mode", "arrangement", "remote"));
            var subtitle = Text(Use(map, used, "subtitle", "project", "product", "projectName", "productName", "team"));

            var lines = Use(map, used, "descriptionLines", "bullets", "highlights", "responsibilities",
                            "achievements", "accomplishments", "details", "description", "descriptions", "summary");

            ReportLeftovers(map, used, $"experience[{index}].", report);
            result.Add(new JsonObject {
                ["title"] = title,
                ["company"] = company,
                ["startDate"] = start,
                ["endDate"] = end,
                ["location"] = location,
                ["employmentType"] = employmentType,
                ["workArrangement"] = workArrangement,
                ["subtitle"] = subtitle,
                ["descriptionLines"] = DescriptionLines(lines, index, report)
            });
            index++;
        }
        return result;
    }

    /// <summary>
    /// Description lines. A plain string stays a plain string, so every older profile is
    /// untouched. A { "segments": [ { "text", "bold" } ] } line keeps its structure, which is how
    /// inline emphasis arrives without any Markdown in resume text. A segmented line with no emphasis
    /// collapses back to a plain string so the saved profile stays as simple as the content allows.
    /// </summary>
    static JsonArray DescriptionLines(JsonNode? node, int experienceIndex, NormalizationReport report) {
        if (node is not JsonArray array) return StringArray(node);

        var result = new JsonArray();
        foreach (var item in array) {
            if (item is JsonObject o) {
                var map = Map(o);
                var used = new HashSet<string>(StringComparer.Ordinal);
                var segments = Use(map, used, "segments", "runs", "parts");

                if (segments is JsonArray rawSegments) {
                    var normalized = Segments(rawSegments);
                    if (normalized.Count == 0) continue;

                    if (normalized.All(s => !s.Bold)) {
                        AddLine(result, string.Concat(normalized.Select(s => s.Text)).Trim());
                        report.Changes.Add($"experience[{experienceIndex}]: a description line with no emphasis was stored as plain text");
                        continue;
                    }

                    var line = new JsonArray();
                    foreach (var segment in normalized)
                        line.Add(new JsonObject { ["text"] = segment.Text, ["bold"] = segment.Bold });
                    result.Add(new JsonObject { ["segments"] = line });
                    continue;
                }

                // Not segmented: the pre-existing object shapes ({ "text": ... } and friends).
                var text = Text(Use(map, used, "text", "line", "value", "description", "bullet", "content"));
                if (text.Length == 0) text = Text(item);
                if (text.Length > 0) AddLine(result, text);
                continue;
            }

            var plain = Text(item);
            if (plain.Length > 0) AddLine(result, plain);
        }
        return result;
    }

    /// <summary>Segment objects, in order, with empty text removed. Nothing here invents content.</summary>
    static List<(string Text, bool Bold)> Segments(JsonArray raw) {
        var segments = new List<(string Text, bool Bold)>();
        foreach (var item in raw) {
            string text;
            var bold = false;

            if (item is JsonObject o) {
                var map = Map(o);
                var used = new HashSet<string>(StringComparer.Ordinal);
                // Segment text keeps its exact spacing: the surrounding spaces are what join the
                // segments back into one sentence.
                text = RawText(Use(map, used, "text", "value", "content", "line"));
                var flag = Use(map, used, "bold", "strong", "emphasis", "isBold");
                if (flag is JsonValue v) bold = v.TryGetValue<bool>(out var b) ? b
                    : v.TryGetValue<string>(out var s) && s.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            } else {
                text = RawText(item);
            }

            if (text.Trim().Length == 0) continue;
            segments.Add((text, bold));
        }
        return segments;
    }

    // ---------- certifications ----------

    static JsonArray NormalizeCertifications(JsonNode? node, NormalizationReport report) {
        var result = new JsonArray();
        var items = AsItemArray(node, "certifications", report);
        int index = 0;
        foreach (var raw in items) {
            if (raw is JsonObject o) {
                var map = Map(o);
                var used = new HashSet<string>(StringComparer.Ordinal);
                var name = Text(Use(map, used, "name", "title", "certification", "certificate", "credential"));
                var issuer = Text(Use(map, used, "issuer", "organization", "authority", "issuingOrganization", "provider", "vendor"));
                var date = Text(Use(map, used, "date", "issued", "issueDate", "year", "completed", "obtained"));
                if (date.Length == 0) {
                    var range = Text(Use(map, used, "dates", "dateRange", "period"));
                    if (range.Length > 0) { date = range; report.Changes.Add($"certifications[{index}]: mapped a date range into date"); }
                }
                ReportLeftovers(map, used, $"certifications[{index}].", report);
                result.Add(new JsonObject { ["name"] = name, ["issuer"] = issuer, ["date"] = date });
            } else {
                var s = Text(raw);
                if (s.Length == 0) { index++; continue; }
                report.Changes.Add($"certifications[{index}]: a bare string was converted into name/issuer/date");
                result.Add(new JsonObject { ["name"] = s, ["issuer"] = "", ["date"] = "" });
            }
            index++;
        }
        return result;
    }

    // ---------- education ----------

    static JsonArray NormalizeEducation(JsonNode? node, NormalizationReport report) {
        var result = new JsonArray();
        var items = AsItemArray(node, "education", report);
        int index = 0;
        foreach (var raw in items) {
            if (raw is not JsonObject o) { index++; continue; }
            var map = Map(o);
            var used = new HashSet<string>(StringComparer.Ordinal);

            var degree = Text(Use(map, used, "degree", "qualification", "program", "studyType"));
            var major = Text(Use(map, used, "major", "fieldOfStudy", "field", "concentration", "area", "discipline"));
            var school = Text(Use(map, used, "school", "institution", "university", "college", "schoolName"));

            var start = Text(Use(map, used, "startDate", "start", "from", "beginDate"));
            var end = Text(Use(map, used, "endDate", "end", "to", "graduationDate", "graduated", "completionDate"));
            var range = Text(Use(map, used, "dates", "dateRange", "period", "years", "duration"));
            if ((start.Length == 0 || end.Length == 0) && range.Length > 0) {
                var split = SplitDateRange(range);
                if (start.Length == 0) start = split.Start;
                if (end.Length == 0) end = split.End;
                report.Changes.Add($"education[{index}]: split a combined date range into startDate/endDate");
            }

            ReportLeftovers(map, used, $"education[{index}].", report);
            result.Add(new JsonObject {
                ["degree"] = degree,
                ["major"] = major,
                ["school"] = school,
                ["startDate"] = start,
                ["endDate"] = end
            });
            index++;
        }
        return result;
    }

    // ---------- shared helpers ----------

    static IEnumerable<JsonNode?> AsItemArray(JsonNode? node, string section, NormalizationReport report) {
        switch (node) {
            case null:
                return Array.Empty<JsonNode?>();
            case JsonArray a:
                return a.ToList();
            case JsonObject o:
                report.Changes.Add($"{section} was an object; converted its values into the canonical array");
                return o.Select(p => p.Value).ToList();
            default:
                report.Changes.Add($"{section} was not an array; replaced with an empty array");
                return Array.Empty<JsonNode?>();
        }
    }

    /// <summary>Case- and separator-insensitive property map, so startDate, start_date and "Start Date" collapse together.</summary>
    static Dictionary<string, KeyValuePair<string, JsonNode?>> Map(JsonObject o) {
        var map = new Dictionary<string, KeyValuePair<string, JsonNode?>>(StringComparer.Ordinal);
        foreach (var pair in o) {
            var key = Canon(pair.Key);
            if (key.Length == 0) continue;
            if (!map.ContainsKey(key)) map[key] = new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value);
        }
        return map;
    }

    static (string Name, JsonNode? Value) FindProperty(JsonObject o, string canonicalName) {
        var map = Map(o);
        return map.TryGetValue(Canon(canonicalName), out var hit) ? (hit.Key, hit.Value) : ("", null);
    }

    static string Canon(string name) {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    static JsonNode? Use(Dictionary<string, KeyValuePair<string, JsonNode?>> map, HashSet<string> used, params string[] names) {
        JsonNode? found = null;
        foreach (var name in names) {
            var key = Canon(name);
            if (!map.TryGetValue(key, out var hit)) continue;
            used.Add(key);
            if (found is null && hit.Value is not null) found = hit.Value.DeepClone();
        }
        return found;
    }

    static void ReportLeftovers(Dictionary<string, KeyValuePair<string, JsonNode?>> map, HashSet<string> used, string prefix, NormalizationReport report) {
        foreach (var pair in map) {
            if (used.Contains(pair.Key)) continue;
            report.Dropped.Add(prefix + pair.Value.Key);
        }
    }

    /// <summary>A string value exactly as written, including leading and trailing spaces.</summary>
    static string RawText(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : Text(node);

    static string Text(JsonNode? node) {
        switch (node) {
            case null: return "";
            case JsonValue v:
                return v.TryGetValue<string>(out var s) ? s.Trim() : v.ToJsonString().Trim('"').Trim();
            case JsonArray a:
                return string.Join(" ", a.Select(Text).Where(x => x.Length > 0));
            case JsonObject o:
                return string.Join(" ", o.Select(p => Text(p.Value)).Where(x => x.Length > 0));
            default: return "";
        }
    }

    static JsonArray StringArray(JsonNode? node) {
        var result = new JsonArray();
        switch (node) {
            case null:
                return result;
            case JsonArray a:
                foreach (var item in a) {
                    if (item is JsonObject o) {
                        var map = Map(o);
                        var used = new HashSet<string>(StringComparer.Ordinal);
                        var s = Text(Use(map, used, "text", "line", "value", "description", "bullet", "content", "name", "skill"));
                        if (s.Length == 0) s = Text(item);
                        if (s.Length > 0) AddLine(result, s);
                    } else {
                        var s = Text(item);
                        if (s.Length > 0) AddLine(result, s);
                    }
                }
                return result;
            case JsonObject grouped:
                foreach (var pair in grouped)
                    foreach (var line in StringArray(pair.Value))
                        AddLine(result, Text(line));
                return result;
            default:
                foreach (var line in SplitBlock(Text(node))) AddLine(result, line);
                return result;
        }
    }

    /// <summary>Splits a single description or skill blob into lines without inventing content.</summary>
    static IEnumerable<string> SplitBlock(string value) {
        if (string.IsNullOrWhiteSpace(value)) yield break;
        string[] parts;
        if (value.Contains('\n')) parts = value.Split('\n');
        else if (value.Contains('•')) parts = value.Split('•');
        else if (value.Contains(';')) parts = value.Split(';');
        else if (value.Contains(',') && value.Length < 400) parts = value.Split(',');
        else parts = new[] { value };

        foreach (var part in parts) {
            var line = part.Trim().TrimStart('•', '-', '*', '–', '—').Trim();
            if (line.Length > 0) yield return line;
        }
    }

    static readonly Regex DashRange = new(@"^(?<a>.+?)\s*[–—−~]\s*(?<b>.+)$", RegexOptions.Compiled);
    static readonly Regex WordRange = new(@"^(?<a>.+?)\s+(?:to|through|until)\s+(?<b>.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex SpacedHyphenRange = new(@"^(?<a>.+?)\s+-{1,2}\s+(?<b>.+)$", RegexOptions.Compiled);

    /// <summary>"Sep 2025 - Apr 2026" becomes ("Sep 2025", "Apr 2026"). A value that cannot be split safely stays in startDate.</summary>
    public static (string Start, string End) SplitDateRange(string raw) {
        var value = (raw ?? "").Trim();
        if (value.Length == 0) return ("", "");

        foreach (var rx in new[] { DashRange, WordRange, SpacedHyphenRange }) {
            var m = rx.Match(value);
            if (m.Success) return (m.Groups["a"].Value.Trim(), m.Groups["b"].Value.Trim());
        }

        // Last resort: a single unspaced hyphen such as "Sep 2025-Apr 2026".
        var parts = value.Split('-');
        if (parts.Length == 2) {
            var a = parts[0].Trim();
            var b = parts[1].Trim();
            if (LooksLikeDate(a) && LooksLikeDate(b)) return (a, b);
        }
        return (value, "");
    }

    static bool LooksLikeDate(string value) =>
        value.Length > 0 && (value.Any(char.IsDigit)
            || value.Equals("present", StringComparison.OrdinalIgnoreCase)
            || value.Equals("current", StringComparison.OrdinalIgnoreCase));
}
