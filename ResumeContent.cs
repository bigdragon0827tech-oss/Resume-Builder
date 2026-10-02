using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ResumeBuilder;

/// <summary>
/// Semantic resume sent to GPT. The original HTML stays the layout template.
/// Locked facts are included so GPT can reason about them. The app decides what may change.
/// </summary>
public sealed class ResumeContentModel {
    public int SchemaVersion { get; set; } = ResumeContent.SchemaVersion;
    public ResumeIdentity Identity { get; set; } = new();
    public List<ContentBlock> Blocks { get; set; } = new();
    public List<EditableFieldSpec> EditableFields { get; set; } = new();
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResumeLinesField? Summary { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResumeSkillsField? Skills { get; set; }
    public List<ResumeExperience> Experience { get; set; } = new();
    public List<ResumeTextField> Education { get; set; } = new();
    public List<ResumeTextField> Certifications { get; set; } = new();
    public List<ResumeLinesField> LockedSections { get; set; } = new();
}

public sealed class ResumeIdentity {
    public ResumeTextField? Name { get; set; }
    public ResumeTextField? Headline { get; set; }
    public ResumeTextField? Contact { get; set; }
}

public sealed class ResumeTextField {
    public string Id { get; set; } = "";
    public bool Editable { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Optional { get; set; }
    public string? Value { get; set; }
}

public sealed class ResumeLinesField {
    public string Id { get; set; } = "";
    public bool Editable { get; set; }
    public List<string> Value { get; set; } = new();
}

public sealed class ResumeSkillsField {
    public string Id { get; set; } = "";
    public bool Editable { get; set; }
    public List<ResumeSkillGroup> Value { get; set; } = new();
}

public sealed class ResumeSkillGroup {
    public string Label { get; set; } = "";
    public List<string> Items { get; set; } = new();
}

public sealed class ResumeExperience {
    public string Id { get; set; } = "";
    public ResumeTextField Company { get; set; } = new();
    public ResumeTextField Title { get; set; } = new();
    public ResumeTextField Dates { get; set; } = new();
    public ResumeTextField? Metadata { get; set; }
    public ResumeLinesField Projects { get; set; } = new();
    public ResumeLinesField Bullets { get; set; } = new();
}

public sealed class ContentBlock {
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "unclassified";
    public bool Editable { get; set; }
    public string Text { get; set; } = "";
}

public sealed class EditableFieldSpec {
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
}

/// <summary>One HTML document plus the fields that map onto its nodes.</summary>
public sealed class BoundResume {
    readonly Dictionary<string, FieldSlot> _slots = new(StringComparer.Ordinal);

    public BoundResume(XDocument document, ResumeContentModel model) {
        Document = document;
        Model = model;
    }

    public XDocument Document { get; }
    public ResumeContentModel Model { get; }

    public string ModelJson() => ResumeContent.ToJson(Model);

    public string Html() => HtmlResumeHtml.Normalize(Document.ToString(SaveOptions.DisableFormatting));

    internal void Add(FieldSlot slot) => _slots[slot.Id] = slot;

    public PatchApplication Apply(string? captured) {
        if (!ResumeContent.TryReadUpdates(captured, out var updates, out var error))
            return PatchApplication.Malformed(error);
        var ignored = new List<string>();
        foreach (var item in updates.EnumerateArray()) {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("id", out var idNode)
                || idNode.ValueKind != JsonValueKind.String) {
                ignored.Add("missing-id");
                PerfLog.Line("HTML PATCH ignored id= reason=missing-id expected=id actual=missing");
                continue;
            }
            var id = idNode.GetString() ?? "";
            if (!_slots.TryGetValue(id, out var slot)) {
                ignored.Add("unknown:" + id);
                PerfLog.Line("HTML PATCH ignored id=" + id + " reason=unknown expected=known-id actual=absent");
                continue;
            }
            if (!item.TryGetProperty("value", out var value)) {
                ignored.Add("type:" + id);
                PerfLog.Line("HTML PATCH ignored id=" + id + " reason=type expected=" + slot.Type + " actual=missing");
                continue;
            }
            if (!slot.Editable) {
                ignored.Add("locked:" + id);
                PerfLog.Line("HTML PATCH ignored id=" + id + " reason=locked expected=" + slot.Type + " actual=locked");
                continue;
            }
            if (!slot.TryAssign(value)) {
                ignored.Add("type:" + id);
                PerfLog.Line("HTML PATCH ignored id=" + id + " reason=type expected=" + slot.Type + " actual=" + ResumeContent.JsonKind(value));
                continue;
            }
        }
        foreach (var slot in _slots.Values)
            if (slot.Dirty) slot.Write();
        return PatchApplication.Applied(Html(), ignored);
    }
}

public sealed class PatchApplication {
    public bool Ok { get; init; }
    public string Html { get; init; } = "";
    public string Error { get; init; } = "";
    public List<string> Ignored { get; init; } = new();

    public static PatchApplication Malformed(string error) => new() { Ok = false, Error = error };
    public static PatchApplication Applied(string html, List<string> ignored) =>
        new() { Ok = true, Html = html, Ignored = ignored };
}

/// <summary>
/// How an existing candidate-profile.json is read. Nothing is written back.
/// Locked facts, career context, and tailorable presentation stay in the same file.
/// </summary>
public static class CandidateProfileRoles {
    public const string Locked = "locked";
    public const string Career = "career";
    public const string Tailorable = "tailorable";

    public static readonly IReadOnlyList<(string Path, string Role)> Map = new[] {
        ("info.name", Locked),
        ("info.email", Locked),
        ("info.phone", Locked),
        ("info.linkedin", Locked),
        ("info.location", Locked),
        ("info.title", Tailorable),
        ("summary", Tailorable),
        ("skills", Tailorable),
        ("experience.company", Locked),
        ("experience.title", Tailorable),
        ("experience.startDate", Locked),
        ("experience.endDate", Locked),
        ("experience.location", Locked),
        ("experience.employmentType", Locked),
        ("experience.workArrangement", Locked),
        ("experience.descriptionLines", Career),
        ("education", Locked),
        ("certifications", Locked)
    };
}

/// <summary>Reads resume HTML into a content model and applies JSON patches onto that document.</summary>
public static class ResumeContent {
    public const int SchemaVersion = 1;
    public const string ModelMarker = "===== RESUME CONTENT MODEL =====";
    public const string StringType = "string";
    public const string NullableStringType = "nullableString";
    public const string StringArrayType = "stringArray";
    public const string SkillGroupsType = "skillGroups";
    public const string ProjectArrayType = "projectArray";

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    static readonly Regex DateRange = new(
        @"\b(?:(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Sept|Oct|Nov|Dec)[a-z]*\.?\s+\d{4}|\d{1,2}/\d{4})\s*[-–—]\s*(?:Present|Current|(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Sept|Oct|Nov|Dec)[a-z]*\.?\s+\d{4}|\d{1,2}/\d{4})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly HashSet<string> HeadingQualifier = new(StringComparer.OrdinalIgnoreCase) {
        "professional", "technical", "work", "relevant", "core", "selected", "key", "additional", "other", "career"
    };
    static readonly Regex SectionTitle = new(
        @"\b(SUMMARY|SKILLS?|EXPERIENCE|EDUCATION|CERTIFICATIONS?|PROJECTS?|PUBLICATIONS?|AWARDS?|HONORS?|LANGUAGES?|INTERESTS?|VOLUNTEER|REFERENCES?|OBJECTIVE|PROFILE|CONTACT|TRAINING|COURSES?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string ToJson(ResumeContentModel model) => JsonSerializer.Serialize(model, JsonOptions);

    public static BoundResume Bind(string html) => Bind(XDocument.Parse(html, LoadOptions.PreserveWhitespace));

    public static string JsonKind(JsonElement value) => value.ValueKind switch {
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        JsonValueKind.Array => "array",
        JsonValueKind.Object => "object",
        _ => "missing"
    };

    public static BoundResume Bind(XDocument document) {
        var model = new ResumeContentModel();
        var bound = new BoundResume(document, model);
        var blocks = ReadBlocks(document);
        var index = 0;
        var header = new List<Block>();
        while (index < blocks.Count && !blocks[index].Heading) header.Add(blocks[index++]);
        MapHeader(header, model, bound);
        while (index < blocks.Count) {
            var heading = blocks[index];
            heading.Kind = "heading";
            var kind = SectionKind(heading.Text);
            index++;
            var body = new List<Block>();
            while (index < blocks.Count && !blocks[index].Heading) body.Add(blocks[index++]);
            switch (kind) {
                case "summary":
                    MapSummary(body, model, bound);
                    break;
                case "skills":
                    MapSkills(body, model, bound);
                    break;
                case "experience":
                    MapExperience(body, model, bound);
                    break;
                case "education":
                    MapLocked(body, "education.item", model.Education, bound, "education");
                    break;
                case "certifications":
                    MapLocked(body, "certifications.item", model.Certifications, bound, "certification");
                    break;
                default:
                    var id = "locked." + Word(heading.Text);
                    var field = new ResumeLinesField { Id = id, Editable = false, Value = body.ConvertAll(block => block.Text) };
                    model.LockedSections.Add(field);
                    foreach (var block in body) block.Kind = "locked";
                    Track(model, bound, FieldSlot.MakeLines(id, StringArrayType, editable: false, field.Value, Nodes(body), prefix: "", anchor: null));
                    break;
            }
        }
        model.Blocks = blocks.ConvertAll(block => new ContentBlock {
            Id = block.Id, Kind = block.Kind, Editable = false, Text = block.Text
        });
        var employers = model.Experience.Select(role => role.Company.Value ?? "").Distinct(StringComparer.OrdinalIgnoreCase).Count(name => name.Length > 0);
        var projects = model.Experience.Sum(role => role.Projects.Value.Count);
        var unclassified = blocks.Count(block => block.Kind == "unclassified");
        PerfLog.Line("RESUME MODEL blocks=" + blocks.Count
            + " skillsGroups=" + (model.Skills?.Value.Count ?? 0)
            + " employers=" + employers
            + " projects=" + projects
            + " editable=" + model.EditableFields.Count
            + " unclassified=" + unclassified);
        return bound;
    }

    public static bool TryReadUpdates(string? captured, out JsonElement updates, out string error) {
        updates = default;
        error = "";
        if (string.IsNullOrWhiteSpace(captured)) {
            error = "The answer was empty.";
            return false;
        }
        var text = captured.Trim();
        var fence = Regex.Match(text, @"```(?:json|JSON)?[^\n]*\r?\n([\s\S]*?)```");
        if (fence.Success) text = fence.Groups[1].Value.Trim();
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) {
            error = "The answer was not a JSON patch.";
            return false;
        }
        try {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            if (!doc.RootElement.TryGetProperty("updates", out var list) || list.ValueKind != JsonValueKind.Array) {
                error = "The answer was not a JSON patch.";
                return false;
            }
            updates = list.Clone();
            return true;
        } catch (JsonException) {
            error = "The answer was not a JSON patch.";
            return false;
        }
    }

    public static void SetText(XElement element, string text) {
        var nodes = element.DescendantNodes().OfType<XText>().ToList();
        if (nodes.Count == 0) {
            element.Add(new XText(text));
            return;
        }
        nodes[0].Value = text;
        for (var i = 1; i < nodes.Count; i++) nodes[i].Value = "";
    }

    static void MapHeader(List<Block> header, ResumeContentModel model, BoundResume bound) {
        Block? name = null;
        var contact = new List<Block>();
        var headlines = new List<Block>();
        foreach (var block in header) {
            if (IsContact(block.Text)) {
                contact.Add(block);
                block.Kind = "header";
                continue;
            }
            if (name is null) {
                name = block;
                block.Kind = "header";
                continue;
            }
            if (IsHeadline(block.Text)) headlines.Add(block);
        }
        if (name is not null) {
            model.Identity.Name = new ResumeTextField { Id = "header.name", Editable = false, Value = name.Text };
            Track(model, bound, FieldSlot.Scalar(model.Identity.Name, name.Element));
        }
        if (headlines.Count == 1) {
            var line = headlines[0];
            line.Kind = "header";
            model.Identity.Headline = new ResumeTextField { Id = "header.headline", Editable = true, Value = line.Text };
            Track(model, bound, FieldSlot.Scalar(model.Identity.Headline, line.Element));
        }
        if (contact.Count > 0) {
            model.Identity.Contact = new ResumeTextField {
                Id = "header.contact", Editable = false, Value = string.Join(" | ", contact.Select(block => block.Text))
            };
            Track(model, bound, FieldSlot.MakeLines("header.contact", StringArrayType, editable: false,
                contact.ConvertAll(block => block.Text), Nodes(contact), prefix: "", anchor: null));
        }
    }

    static void MapSummary(List<Block> body, ResumeContentModel model, BoundResume bound) {
        if (body.Count == 0) return;
        foreach (var block in body) block.Kind = "summary";
        var lines = body.ConvertAll(block => block.Text);
        model.Summary = new ResumeLinesField { Id = "summary", Editable = true, Value = lines };
        Track(model, bound, FieldSlot.MakeLines("summary", StringArrayType, editable: true, lines, Nodes(body), prefix: "", anchor: body[0].Element));
    }

    static void MapSkills(List<Block> body, ResumeContentModel model, BoundResume bound) {
        var layout = TrySkills(body);
        if (layout is null) return;
        foreach (var block in body) block.Kind = "skill";
        model.Skills = new ResumeSkillsField { Id = "skills", Editable = true, Value = layout.Groups };
        Track(model, bound, FieldSlot.Skill(layout));
    }

    static void MapExperience(List<Block> body, ResumeContentModel model, BoundResume bound) {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        RoleDraft? current = null;
        string? company = null;
        XElement? companyNode = null;
        string? pendingCompany = null;
        XElement? pendingNode = null;

        void Close() {
            if (current is null) return;
            var stem = Word(current.Company);
            seen.TryGetValue(stem, out var count);
            seen[stem] = count + 1;
            var id = "experience." + stem + "-" + (count + 1).ToString("00");
            var entry = new ResumeExperience {
                Id = id,
                Company = new ResumeTextField { Id = id + ".company", Editable = false, Value = current.Company },
                Title = new ResumeTextField { Id = id + ".title", Editable = true, Value = current.Title },
                Dates = new ResumeTextField { Id = id + ".dates", Editable = false, Value = current.Dates },
                Projects = new ResumeLinesField { Id = id + ".projects", Editable = true, Value = current.Projects.ConvertAll(block => block.Text) },
                Bullets = new ResumeLinesField { Id = id + ".bullets", Editable = true, Value = current.Bullets.ConvertAll(block => BulletText(block.Text)) }
            };
            if (current.Metadata is not null)
                entry.Metadata = new ResumeTextField { Id = id + ".metadata", Editable = false, Value = current.Metadata.Text };
            model.Experience.Add(entry);
            if (current.CompanyNode is not null)
                Track(model, bound, FieldSlot.Scalar(entry.Company, current.CompanyNode));
            Track(model, bound, FieldSlot.Title(entry.Title, current.Line, current.Shared, current.Company, current.Dates));
            Track(model, bound, FieldSlot.Scalar(entry.Dates, current.Line));
            if (entry.Metadata is not null && current.Metadata is not null)
                Track(model, bound, FieldSlot.Scalar(entry.Metadata, current.Metadata.Element));
            Track(model, bound, FieldSlot.MakeLines(entry.Projects.Id, ProjectArrayType, editable: true, entry.Projects.Value,
                current.Projects.ConvertAll(block => block.Element), prefix: "", anchor: current.Line));
            Track(model, bound, FieldSlot.MakeLines(entry.Bullets.Id, StringArrayType, editable: true, entry.Bullets.Value,
                current.Bullets.ConvertAll(block => block.Element), BulletPrefix(current.Bullets.FirstOrDefault()), current.Line));
            current = null;
        }

        for (var i = 0; i < body.Count; i++) {
            var block = body[i];
            if (block.Bullet) {
                if (current is null) continue;
                block.Kind = "bullet";
                current.Bullets.Add(block);
                continue;
            }
            if (IsMetadata(block.Text)) {
                if (current is null) continue;
                block.Kind = "experience";
                current.Metadata ??= block;
                continue;
            }
            if (IsCompanyOnly(block.Text) && i + 1 < body.Count && HasDate(body[i + 1].Text) && !body[i + 1].Bullet) {
                Close();
                pendingCompany = block.Text.Trim();
                pendingNode = block.Element;
                block.Kind = "experience";
                continue;
            }
            if (HasDate(block.Text) && (block.Text.Contains('|') || pendingCompany is not null)) {
                if (pendingCompany is not null) {
                    company = pendingCompany;
                    companyNode = pendingNode;
                    pendingCompany = null;
                    pendingNode = null;
                    block.Kind = "experience";
                    current = RoleDraft.Open(company, companyNode, NonDate(block.Text), DateOf(block.Text), block.Element, shared: false);
                    continue;
                }
                var parts = SplitRole(block.Text);
                if (current is not null && company is not null && LooksLikeTitle(parts.Company) && !LooksLikeCompany(parts.Company)) {
                    Close();
                    block.Kind = "experience";
                    current = RoleDraft.Open(company, companyNode, NonDate(block.Text), DateOf(block.Text), block.Element, shared: false);
                    continue;
                }
                Close();
                company = parts.Company;
                companyNode = block.Element;
                block.Kind = "experience";
                current = RoleDraft.Open(parts.Company, block.Element, parts.Title, parts.Dates, block.Element, shared: true);
                continue;
            }
            if (current is not null && current.Bullets.Count == 0 && IsProjectLine(block.Text)) {
                block.Kind = "project";
                current.Projects.Add(block);
                continue;
            }
        }
        Close();
    }

    static void MapLocked(List<Block> body, string prefix, List<ResumeTextField> target, BoundResume bound, string kind) {
        var number = 1;
        foreach (var block in body) {
            block.Kind = kind;
            var field = new ResumeTextField {
                Id = prefix + "." + number.ToString("00"),
                Editable = false,
                Value = block.Text
            };
            number++;
            target.Add(field);
            Track(null, bound, FieldSlot.Scalar(field, block.Element));
        }
    }

    static void Track(ResumeContentModel? model, BoundResume bound, FieldSlot slot) {
        bound.Add(slot);
        if (model is not null && slot.Editable)
            model.EditableFields.Add(new EditableFieldSpec { Id = slot.Id, Type = slot.Type });
    }

    static SkillLayout? TrySkills(List<Block> body) {
        if (body.Count == 0) return null;
        var table = TryTable(body);
        if (table is not null) return table;
        if (body.All(block => IsCombined(Core(block.Text)))) return Combined(body);
        if (body.All(block => block.Bullet)) return PlainBullets(body);
        var labeled = TryLabelParagraph(body);
        if (labeled is not null) return labeled;
        return TryLabelBullets(body);
    }

    static SkillLayout? TryTable(List<Block> body) {
        if (body.Any(block => block.Row is null)) return null;
        var rows = new List<XElement>();
        foreach (var block in body) {
            if (rows.Count == 0 || !ReferenceEquals(rows[^1], block.Row)) rows.Add(block.Row!);
        }
        var layout = new SkillLayout { Pattern = "table" };
        foreach (var row in rows) {
            var cells = body.Where(block => ReferenceEquals(block.Row, row)).ToList();
            if (cells.Count < 2 || !IsShortLabel(cells[0].Text)) return null;
            var items = new List<string>();
            foreach (var cell in cells.Skip(1)) items.AddRange(SplitItems(cell.Text));
            if (items.Count == 0) return null;
            layout.Groups.Add(new ResumeSkillGroup { Label = LabelText(cells[0].Text), Items = items });
            layout.Spans.Add(new SkillSpan { Row = row });
        }
        return layout.Groups.Count == 0 ? null : layout;
    }

    static SkillLayout Combined(List<Block> body) {
        var layout = new SkillLayout { Pattern = "combined" };
        foreach (var block in body) {
            var text = Core(block.Text);
            var colon = text.IndexOf(':');
            layout.Groups.Add(new ResumeSkillGroup {
                Label = text[..colon].Trim(),
                Items = SplitItems(text[(colon + 1)..])
            });
            layout.Spans.Add(new SkillSpan { Label = block.Element });
        }
        return layout;
    }

    static SkillLayout PlainBullets(List<Block> body) {
        return new SkillLayout {
            Pattern = "plain",
            Prefix = BulletPrefix(body[0]),
            Groups = new List<ResumeSkillGroup> { new() { Label = "", Items = body.ConvertAll(block => BulletText(block.Text)) } },
            Spans = new List<SkillSpan> { new() { Items = Nodes(body) } }
        };
    }

    static SkillLayout? TryLabelParagraph(List<Block> body) {
        var layout = new SkillLayout { Pattern = "split" };
        var index = 0;
        while (index < body.Count) {
            if (body[index].Bullet || !IsShortLabel(body[index].Text)) return null;
            var label = body[index++];
            var values = new List<Block>();
            while (index < body.Count && IsValueParagraph(body[index])) values.Add(body[index++]);
            if (values.Count == 0) return null;
            var items = new List<string>();
            foreach (var value in values) items.AddRange(SplitItems(value.Text));
            if (items.Count == 0) return null;
            layout.Groups.Add(new ResumeSkillGroup { Label = LabelText(label.Text), Items = items });
            layout.Spans.Add(new SkillSpan {
                Label = label.Element,
                Value = values[0].Element,
                Items = values.Skip(1).Select(block => block.Element).ToList()
            });
        }
        return layout.Groups.Count == 0 ? null : layout;
    }

    static SkillLayout? TryLabelBullets(List<Block> body) {
        var layout = new SkillLayout { Pattern = "split", Prefix = "" };
        var index = 0;
        while (index < body.Count) {
            if (body[index].Bullet || !IsShortLabel(body[index].Text)) return null;
            var label = body[index++];
            var items = new List<Block>();
            while (index < body.Count && body[index].Bullet) items.Add(body[index++]);
            if (items.Count == 0) return null;
            if (layout.Prefix.Length == 0) layout.Prefix = BulletPrefix(items[0]);
            layout.Groups.Add(new ResumeSkillGroup { Label = LabelText(label.Text), Items = items.ConvertAll(block => BulletText(block.Text)) });
            layout.Spans.Add(new SkillSpan { Label = label.Element, Items = Nodes(items) });
        }
        return layout.Groups.Count == 0 ? null : layout;
    }

    static List<string> SplitItems(string text) =>
        text.Split(new[] { ',', '|', '•', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim().TrimStart('•', '-', '*').Trim())
            .Where(item => item.Length > 0).ToList();

    static (string Company, string Title, string Dates) SplitRole(string text) {
        var pieces = Regex.Split(text, @"\s*\|\s*").Select(part => part.Trim()).Where(part => part.Length > 0).ToList();
        var dateAt = pieces.FindIndex(part => DateRange.IsMatch(part));
        var dates = dateAt >= 0 ? pieces[dateAt] : "";
        var company = pieces.Count > 0 ? pieces[0] : text.Trim();
        var title = string.Join(" | ", pieces.Where((part, index) => index != 0 && index != dateAt));
        return (company, title, dates);
    }

    static string NonDate(string text) {
        var pieces = Regex.Split(text, @"\s*\|\s*").Select(part => part.Trim()).Where(part => part.Length > 0 && !DateRange.IsMatch(part));
        var line = string.Join(" | ", pieces);
        return line.Length == 0 ? text.Trim() : line;
    }

    static string DateOf(string text) {
        var match = DateRange.Match(text);
        return match.Success ? match.Value.Trim() : "";
    }

    static bool HasDate(string text) => DateRange.IsMatch(text);

    static bool IsCompanyOnly(string text) =>
        LooksLikeCompany(text) && !HasDate(text) && !text.Contains('|') && !text.Contains(':')
        && !text.Contains('.') && text.Trim().Length <= 60;

    static bool IsProjectLine(string text) {
        var line = text.Trim();
        if (line.Length is < 2 or > 70 || HasDate(line) || IsMetadata(line)) return false;
        if (line.Contains(':') || line.Contains('.') || line.Contains('|')) return false;
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return words is >= 1 and <= 8;
    }

    static bool IsMetadata(string text) {
        if (text.Contains('|') && !DateRange.IsMatch(text)) return true;
        return Regex.IsMatch(text, @"\b(full-time|full time|part-time|part time|contract|remote|hybrid|on-?site|intern)\b", RegexOptions.IgnoreCase)
            && text.Length < 80 && !text.Contains('.');
    }

    static bool IsContact(string text) =>
        text.Contains('@') || text.Contains("linkedin", StringComparison.OrdinalIgnoreCase)
        || text.Contains("http", StringComparison.OrdinalIgnoreCase)
        || Regex.IsMatch(text, @"\d{3}[^\d]{0,4}\d{3}");

    static bool IsHeadline(string text) {
        var line = text.Trim();
        if (line.Length is < 3 or > 90 || line.Contains('.') || line.Contains('@') || line.Contains('|') || HasDate(line)) return false;
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return words is >= 2 and <= 14;
    }

    static bool LooksLikeTitle(string text) =>
        Regex.IsMatch(text, @"\b(engineer|developer|manager|analyst|architect|scientist|consultant|director|designer|programmer|intern|specialist|founder|lead)\b", RegexOptions.IgnoreCase);

    static bool LooksLikeCompany(string text) {
        if (LooksLikeTitle(text)) return false;
        if (Regex.IsMatch(text, @"\b(inc|llc|corp|ltd|company|group|university|labs|systems)\b", RegexOptions.IgnoreCase)) return true;
        return text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 2;
    }

    static bool IsShortLabel(string text) {
        var label = LabelText(text);
        if (label.Length is < 2 or > 48 || label.Contains(',') || label.Contains('.') || label.Contains('|')) return false;
        if (label.StartsWith('•') || label.StartsWith("- ", StringComparison.Ordinal)) return false;
        var words = label.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return words is >= 1 and <= 8;
    }

    static string LabelText(string text) => text.Trim().TrimEnd(':').Trim();

    static bool IsValueParagraph(Block block) {
        if (block.Bullet || block.Heading) return false;
        var text = block.Text.Trim();
        if (text.Length == 0 || IsSectionHeading(text) || IsShortLabel(text)) return false;
        return text.Contains(',') || text.Contains(';') || text.Contains('•') || text.Length > 40;
    }

    static bool IsCombined(string text) {
        var colon = text.IndexOf(':');
        if (colon <= 0 || colon > 48 || colon >= text.TrimEnd().Length - 1) return false;
        return IsShortLabel(text[..colon]) && SplitItems(text[(colon + 1)..]).Count > 0;
    }

    static string SectionKind(string heading) {
        var text = heading.ToUpperInvariant();
        if (text.Contains("EXPERIENCE")) return "experience";
        if (text.Contains("EDUCATION")) return "education";
        if (text.Contains("SKILL")) return "skills";
        if (text.Contains("CERTIFICATION")) return "certifications";
        if (text.Contains("SUMMARY") || text.Contains("OBJECTIVE")) return "summary";
        return "locked";
    }

    static bool InMultiCellRow(XElement element) {
        var row = element.Ancestors().FirstOrDefault(ancestor => ancestor.Name.LocalName == "tr");
        return row is not null && row.Elements().Count(cell => cell.Name.LocalName is "td" or "th") >= 2;
    }

    static bool IsSectionHeading(string text) {
        var line = text.Trim();
        if (line.Length is < 4 or > 40 || line.StartsWith('•') || line.StartsWith("- ", StringComparison.Ordinal)) return false;
        if (line.Contains('|') || line.Contains(',') || line.Contains('.') || line.Contains(':') || LooksLikeTitle(line)) return false;
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is < 1 or > 4) return false;
        for (var i = 0; i < words.Length - 1; i++)
            if (!HeadingQualifier.Contains(words[i])) return false;
        return SectionTitle.IsMatch(words[^1]);
    }

    static string Core(string text) => Regex.Replace(text.Trim(), @"^[•\-\*]\s*", "");

    static string BulletText(string text) => Core(text);

    static string BulletPrefix(Block? block) {
        if (block is null || block.Element.Name.LocalName == "li") return "";
        var match = Regex.Match(block.Text, @"^[•\-\*]\s*");
        return match.Success ? match.Value : "";
    }

    static string Word(string text) {
        var word = Regex.Match(text.ToLowerInvariant(), @"[a-z0-9]+");
        return word.Success ? word.Value : "item";
    }

    static List<XElement> Nodes(List<Block> blocks) => blocks.ConvertAll(block => block.Element);

    internal static void Fit(List<XElement> nodes, int count, XElement? anchor) {
        if (count < 0) count = 0;
        if (nodes.Count == 0 && count > 0 && anchor is not null) {
            var created = new XElement(anchor);
            anchor.AddAfterSelf(created);
            nodes.Add(created);
        }
        while (nodes.Count < count && nodes.Count > 0) {
            var created = new XElement(nodes[^1]);
            nodes[^1].AddAfterSelf(created);
            nodes.Add(created);
        }
        while (nodes.Count > count) {
            nodes[^1].Remove();
            nodes.RemoveAt(nodes.Count - 1);
        }
    }

    static List<Block> ReadBlocks(XDocument document) {
        var list = new List<Block>();
        var body = document.Root?.Element("body") ?? document.Root;
        if (body is null) return list;
        foreach (var element in body.Descendants()) {
            var name = element.Name.LocalName;
            if (name is not ("p" or "h1" or "h2" or "h3" or "li" or "td" or "th")) continue;
            if (name is "p" or "h1" or "h2" or "h3" or "li") {
                if (element.Ancestors().Any(ancestor => ancestor.Name.LocalName is "p" or "h1" or "h2" or "h3" or "li")) continue;
            } else if (element.Descendants().Any(inner => inner.Name.LocalName is "p" or "li" or "h1" or "h2" or "h3")) {
                continue;
            }
            var text = string.Concat(element.DescendantNodes().OfType<XText>().Select(node => node.Value)).Trim();
            if (text.Length == 0) continue;
            list.Add(new Block {
                Id = "block-" + (list.Count + 1).ToString("00"),
                Element = element,
                Text = text,
                Bullet = name == "li" || text.StartsWith('•') || text.StartsWith("- ", StringComparison.Ordinal),
                Heading = (name is "p" or "h1" or "h2" or "h3") && !InMultiCellRow(element) && IsSectionHeading(text),
                Row = element.Ancestors().FirstOrDefault(ancestor => ancestor.Name.LocalName == "tr")
            });
        }
        return list;
    }

    sealed class Block {
        public string Id = "";
        public XElement Element = null!;
        public string Text = "";
        public string Kind = "unclassified";
        public bool Bullet;
        public bool Heading;
        public XElement? Row;
    }

    sealed class RoleDraft {
        public string Company = "";
        public XElement? CompanyNode;
        public string Title = "";
        public string Dates = "";
        public XElement Line = null!;
        public bool Shared;
        public Block? Metadata;
        public List<Block> Projects = new();
        public List<Block> Bullets = new();

        public static RoleDraft Open(string company, XElement? companyNode, string title, string dates, XElement line, bool shared) =>
            new() {
                Company = company,
                CompanyNode = companyNode,
                Title = title,
                Dates = dates,
                Line = line,
                Shared = shared
            };
    }
}

sealed class FieldSlot {
    public string Id = "";
    public string Type = "";
    public bool Editable;
    public bool Dirty;
    public bool RoleLine;
    public bool SharedRole;
    public string Company = "";
    public string Dates = "";
    public string Prefix = "";
    public List<XElement> Nodes = new();
    public XElement? Anchor;
    public string? Text;
    public List<string>? Lines;
    public List<ResumeSkillGroup>? Groups;
    public SkillLayout? Skills;

    public static FieldSlot Scalar(ResumeTextField field, XElement node) => new() {
        Id = field.Id,
        Type = ResumeContent.StringType,
        Editable = field.Editable,
        Text = field.Value,
        Nodes = new List<XElement> { node }
    };

    public static FieldSlot Title(ResumeTextField field, XElement node, bool shared, string company, string dates) => new() {
        Id = field.Id,
        Type = ResumeContent.StringType,
        Editable = true,
        RoleLine = true,
        SharedRole = shared,
        Company = company,
        Dates = dates,
        Text = field.Value,
        Nodes = new List<XElement> { node }
    };

    public static FieldSlot MakeLines(string id, string type, bool editable, List<string> lines, List<XElement> nodes, string prefix, XElement? anchor) => new() {
        Id = id,
        Type = type,
        Editable = editable,
        Lines = lines,
        Nodes = nodes,
        Prefix = prefix,
        Anchor = anchor
    };

    public static FieldSlot Skill(SkillLayout layout) => new() {
        Id = "skills",
        Type = ResumeContent.SkillGroupsType,
        Editable = true,
        Skills = layout,
        Groups = layout.Groups
    };

    public bool TryAssign(JsonElement value) {
        switch (Type) {
            case ResumeContent.StringType:
                if (value.ValueKind != JsonValueKind.String) return false;
                Text = value.GetString() ?? "";
                break;
            case ResumeContent.NullableStringType:
                if (value.ValueKind == JsonValueKind.Null) Text = "";
                else if (value.ValueKind == JsonValueKind.String) Text = value.GetString() ?? "";
                else return false;
                break;
            case ResumeContent.StringArrayType:
            case ResumeContent.ProjectArrayType:
                if (!TryStrings(value, out var lines)) return false;
                Lines = lines;
                break;
            case ResumeContent.SkillGroupsType:
                if (!TryGroups(value, out var groups)) return false;
                Groups = groups;
                break;
            default:
                return false;
        }
        Dirty = true;
        return true;
    }

    public void Write() {
        if (!Dirty || !Editable) return;
        switch (Type) {
            case ResumeContent.SkillGroupsType:
                WriteSkills();
                break;
            case ResumeContent.StringArrayType:
            case ResumeContent.ProjectArrayType:
                WriteLines();
                break;
            default:
                WriteScalar();
                break;
        }
    }

    void WriteScalar() {
        if (Nodes.Count == 0) return;
        if (RoleLine) {
            var line = SharedRole ? Join(Company, Text, Dates) : Join(Text, Dates);
            ResumeContent.SetText(Nodes[0], line);
            return;
        }
        if (Type == ResumeContent.NullableStringType && string.IsNullOrWhiteSpace(Text)) {
            Nodes[0].Remove();
            return;
        }
        ResumeContent.SetText(Nodes[0], Text ?? "");
    }

    void WriteLines() {
        var lines = Lines ?? new List<string>();
        ResumeContent.Fit(Nodes, lines.Count, Anchor);
        for (var i = 0; i < lines.Count && i < Nodes.Count; i++)
            ResumeContent.SetText(Nodes[i], Prefix + lines[i]);
    }

    void WriteSkills() {
        if (Skills is null) return;
        var next = Groups ?? new List<ResumeSkillGroup>();
        if (Skills.Pattern == "combined") {
            var nodes = Skills.Spans.Select(span => span.Label).Where(node => node is not null).Select(node => node!).ToList();
            ResumeContent.Fit(nodes, next.Count, nodes.FirstOrDefault());
            for (var i = 0; i < next.Count && i < nodes.Count; i++)
                ResumeContent.SetText(nodes[i], Format(next[i]));
            return;
        }
        if (Skills.Pattern == "table") {
            var rows = Skills.Spans.Select(span => span.Row).Where(row => row is not null).Select(row => row!).ToList();
            ResumeContent.Fit(rows, next.Count, rows.FirstOrDefault());
            for (var i = 0; i < next.Count && i < rows.Count; i++) {
                SetCell(rows[i], 0, next[i].Label);
                SetCell(rows[i], 1, string.Join(", ", next[i].Items));
            }
            return;
        }
        if (Skills.Pattern == "plain") {
            var items = Skills.Spans.Count == 0 ? new List<XElement>() : Skills.Spans[0].Items;
            var flat = new List<string>();
            foreach (var group in next) {
                if (!string.IsNullOrWhiteSpace(group.Label) && next.Count > 1) flat.Add(Format(group));
                else flat.AddRange(group.Items);
            }
            ResumeContent.Fit(items, flat.Count, items.FirstOrDefault());
            for (var i = 0; i < flat.Count && i < items.Count; i++)
                ResumeContent.SetText(items[i], Skills.Prefix + flat[i]);
            return;
        }
        if (Skills.Spans.Count == 0) return;
        while (Skills.Spans.Count < next.Count) Skills.Spans.Add(CloneSpan(Skills.Spans[^1]));
        while (Skills.Spans.Count > next.Count) {
            RemoveSpan(Skills.Spans[^1]);
            Skills.Spans.RemoveAt(Skills.Spans.Count - 1);
        }
        for (var i = 0; i < next.Count; i++) {
            var span = Skills.Spans[i];
            var group = next[i];
            if (span.Label is not null) ResumeContent.SetText(span.Label, group.Label);
            if (span.Value is not null) {
                ResumeContent.SetText(span.Value, string.Join(", ", group.Items));
                foreach (var extra in span.Items) extra.Remove();
                span.Items.Clear();
            } else {
                ResumeContent.Fit(span.Items, group.Items.Count, span.Items.FirstOrDefault() ?? span.Label);
                for (var j = 0; j < group.Items.Count && j < span.Items.Count; j++)
                    ResumeContent.SetText(span.Items[j], Skills.Prefix + group.Items[j]);
            }
        }
    }

    static bool TryStrings(JsonElement value, out List<string> lines) {
        lines = new List<string>();
        if (value.ValueKind != JsonValueKind.Array) return false;
        foreach (var item in value.EnumerateArray()) {
            if (item.ValueKind != JsonValueKind.String) return false;
            lines.Add(item.GetString() ?? "");
        }
        return true;
    }

    static bool TryGroups(JsonElement value, out List<ResumeSkillGroup> groups) {
        groups = new List<ResumeSkillGroup>();
        if (value.ValueKind != JsonValueKind.Array) return false;
        foreach (var item in value.EnumerateArray()) {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("label", out var label) || label.ValueKind != JsonValueKind.String
                || !item.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return false;
            var list = new List<string>();
            foreach (var skill in items.EnumerateArray()) {
                if (skill.ValueKind != JsonValueKind.String) return false;
                list.Add(skill.GetString() ?? "");
            }
            groups.Add(new ResumeSkillGroup { Label = label.GetString() ?? "", Items = list });
        }
        return true;
    }

    static string Format(ResumeSkillGroup group) {
        var items = string.Join(", ", group.Items);
        return string.IsNullOrWhiteSpace(group.Label) ? items : group.Label.Trim() + ": " + items;
    }

    static string Join(params string?[] parts) =>
        string.Join(" | ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));

    static void SetCell(XElement row, int index, string text) {
        var cells = row.Elements().Where(element => element.Name.LocalName is "td" or "th").ToList();
        if (index >= cells.Count) return;
        var target = cells[index].Descendants().FirstOrDefault(element => element.Name.LocalName is "p" or "li") ?? cells[index];
        ResumeContent.SetText(target, text);
    }

    static SkillSpan CloneSpan(SkillSpan template) {
        var clone = new SkillSpan();
        var after = Last(template);
        if (after is null) return clone;
        XElement Copy(XElement source) {
            var created = new XElement(source);
            after.AddAfterSelf(created);
            after = created;
            return created;
        }
        if (template.Row is not null) {
            clone.Row = Copy(template.Row);
            return clone;
        }
        if (template.Label is not null) clone.Label = Copy(template.Label);
        if (template.Value is not null) clone.Value = Copy(template.Value);
        if (template.Items.Count > 0) clone.Items.Add(Copy(template.Items[0]));
        return clone;
    }

    static void RemoveSpan(SkillSpan span) {
        if (span.Row is not null) {
            span.Row.Remove();
            return;
        }
        span.Label?.Remove();
        span.Value?.Remove();
        foreach (var item in span.Items) item.Remove();
    }

    static XElement? Last(SkillSpan span) {
        if (span.Items.Count > 0) return span.Items[^1];
        return span.Value ?? span.Label ?? span.Row;
    }
}

sealed class SkillLayout {
    public string Pattern = "";
    public string Prefix = "";
    public List<ResumeSkillGroup> Groups = new();
    public List<SkillSpan> Spans = new();
}

sealed class SkillSpan {
    public XElement? Label;
    public XElement? Value;
    public List<XElement> Items = new();
    public XElement? Row;
}
