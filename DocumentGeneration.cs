using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace ResumeBuilder;

// ---------------------------------------------------------------------------
// A6.6.9 — resume documents generated from a validated tailored profile.
// Styling moved out of the renderers and into ResumeStyle.
//
// Input is always results\<jobId>.json (or the baseline profile), which has already been
// normalized and strictly validated. Generation only ever READS that file: a document failure
// can never damage or invalidate the saved JSON, and candidate-profile.json is never written here.
//
// One content model (ResumeDocument) feeds both renderers, so the DOCX and the PDF always carry
// the same content even though their layout engines differ. The style is normalized once, here, and
// both renderers translate the same tokens into their own units.
// ---------------------------------------------------------------------------

/// <summary>One run of bullet text. Emphasis is structural — resume text never contains Markdown.</summary>
public sealed class TextSegment {
    public string Text = "";
    public bool Bold;
}

/// <summary>A description line: one plain segment, or several with sparse emphasis.</summary>
public sealed class BulletLine {
    public List<TextSegment> Segments = new();

    public string Text => string.Concat(Segments.Select(s => s.Text));
    public bool HasEmphasis => Segments.Any(s => s.Bold);

    public static BulletLine Plain(string text) => new() { Segments = { new TextSegment { Text = text } } };
}

public sealed class ExperienceBlock {
    public string Title = "", Company = "", Location = "", StartDate = "", EndDate = "";
    public string EmploymentType = "", WorkArrangement = "", Subtitle = "";
    public List<BulletLine> Lines = new();

    public string Dates => ResumeDocument.Range(StartDate, EndDate);

    /// <summary>"Caterpillar Inc. | Senior AI Software Engineer | Sep 2025 - Present", built here and
    /// never supplied by the AI, so the locked metadata cannot be rewritten in prose.</summary>
    public string Heading => ResumeDocument.Pipe(Company, Title, Dates);

    /// <summary>"Menlo Park, CA | Full-time | Hybrid" — empty values disappear with their separator.</summary>
    public string Metadata => ResumeDocument.Pipe(Location, EmploymentType, WorkArrangement);
}

public sealed class EducationBlock {
    public string Degree = "", Major = "", School = "", Dates = "";

    /// <summary>The degree line, without repeating the major when the degree already names it.</summary>
    public string Heading {
        get {
            var degree = Major.Length > 0 && !Degree.Contains(Major, StringComparison.OrdinalIgnoreCase)
                ? ResumeDocument.Join(Degree, Major) : Degree;
            return ResumeDocument.Join(degree, School);
        }
    }
}

public sealed class SkillBlock {
    public string Category = "", Skills = "";
}

/// <summary>The resume as ordered content plus one normalized style, built once from the canonical profile.</summary>
public sealed class ResumeDocument {
    // Headings are stored in title case and upper-cased by the style's "uppercase" token, which is on
    // in every preset. Switching it off is therefore a real choice rather than a no-op.
    public const string SummaryHeading = "Professional Summary";
    public const string SkillsHeading = "Technical Skills";
    public const string ExperienceHeading = "Professional Experience";
    public const string CertificationsHeading = "Certifications";
    public const string EducationHeading = "Education";

    public string Name = "", Title = "", Contact = "", Summary = "";
    public List<SkillBlock> Skills = new();
    public List<ExperienceBlock> Experience = new();
    public List<string> Certifications = new();
    public List<EducationBlock> Education = new();

    /// <summary>Always a complete style: the preset when the profile carries none.</summary>
    public ResumeStyle Style = StyleNormalizer.Default();

    /// <summary>What the style normalizer had to correct, if anything.</summary>
    public List<string> StyleWarnings { get; } = new();

    public static ResumeDocument FromProfileFile(string path) {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return FromProfile(doc.RootElement);
    }

    /// <summary>Reads the canonical schema only. Nothing is invented and nothing is reordered.</summary>
    public static ResumeDocument FromProfile(JsonElement profile) {
        var r = new ResumeDocument();

        if (Obj(profile, "info") is JsonElement info) {
            r.Name = Str(info, "name");
            r.Title = Str(info, "title");
            r.Contact = string.Join("  |  ", new[] {
                Str(info, "location"), Str(info, "email"), Str(info, "phone"), Str(info, "linkedin")
            }.Where(x => x.Length > 0));
        }

        if (profile.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.String)
            r.Summary = summary.GetString()!.Trim();

        foreach (var item in Array(profile, "skills")) {
            var values = Array(item, "skills").Where(v => v.ValueKind == JsonValueKind.String)
                                              .Select(v => v.GetString()!.Trim())
                                              .Where(v => v.Length > 0);
            var joined = string.Join(", ", values);
            if (joined.Length == 0 && Str(item, "category").Length == 0) continue;
            r.Skills.Add(new SkillBlock { Category = Str(item, "category"), Skills = joined });
        }

        foreach (var item in Array(profile, "experience")) {
            r.Experience.Add(new ExperienceBlock {
                Title = Str(item, "title"),
                Company = Str(item, "company"),
                Location = Str(item, "location"),
                StartDate = Str(item, "startDate"),
                EndDate = Str(item, "endDate"),
                EmploymentType = Str(item, "employmentType"),
                WorkArrangement = Str(item, "workArrangement"),
                Subtitle = Str(item, "subtitle"),
                Lines = DescriptionLines(item)
            });
        }

        foreach (var item in Array(profile, "certifications")) {
            var name = Str(item, "name");
            if (name.Length == 0) continue;
            var issuer = Str(item, "issuer");
            var date = Str(item, "date");
            var line = name;
            if (issuer.Length > 0) line += " — " + issuer;
            if (date.Length > 0) line += " (" + date + ")";
            r.Certifications.Add(line);
        }

        foreach (var item in Array(profile, "education")) {
            r.Education.Add(new EducationBlock {
                Degree = Str(item, "degree"),
                Major = Str(item, "major"),
                School = Str(item, "school"),
                Dates = Range(Str(item, "startDate"), Str(item, "endDate"))
            });
        }

        // The optional style block. A profile without one renders exactly as before this version.
        var styleNode = profile.ValueKind == JsonValueKind.Object && profile.TryGetProperty("style", out var style)
            ? JsonNode.Parse(style.GetRawText()) : null;
        var normalized = StyleNormalizer.Normalize(styleNode);
        r.Style = normalized.Style;
        r.StyleWarnings.AddRange(normalized.Warnings);

        return r;
    }

    /// <summary>
    /// Description lines, with the one emphasis rule the style system cannot express: a bullet whose
    /// every segment is bold is rendered regular. Sparse emphasis is the point; a fully bold bullet is
    /// just a loud bullet.
    /// </summary>
    static List<BulletLine> DescriptionLines(JsonElement item) {
        var lines = new List<BulletLine>();

        foreach (var value in Array(item, "descriptionLines")) {
            if (value.ValueKind == JsonValueKind.String) {
                var text = value.GetString()!.Trim();
                if (text.Length > 0) lines.Add(BulletLine.Plain(text));
                continue;
            }

            if (value.ValueKind != JsonValueKind.Object) continue;
            if (!value.TryGetProperty("segments", out var segments) || segments.ValueKind != JsonValueKind.Array) continue;

            var line = new BulletLine();
            foreach (var segment in segments.EnumerateArray()) {
                if (segment.ValueKind != JsonValueKind.Object) continue;
                if (!segment.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String) continue;
                var raw = text.GetString()!;
                if (raw.Trim().Length == 0) continue;
                var bold = segment.TryGetProperty("bold", out var flag) && flag.ValueKind == JsonValueKind.True;
                line.Segments.Add(new TextSegment { Text = raw, Bold = bold });
            }

            if (line.Segments.Count == 0) continue;

            if (line.Segments.All(s => s.Bold))
                foreach (var segment in line.Segments) segment.Bold = false;

            // Trim only the outer edges, so the spaces that join segments survive.
            line.Segments[0].Text = line.Segments[0].Text.TrimStart();
            line.Segments[^1].Text = line.Segments[^1].Text.TrimEnd();

            lines.Add(line);
        }

        return lines;
    }

    /// <summary>Every piece of visible text, in order. Both renderers must reproduce exactly this.</summary>
    public IEnumerable<string> ContentLines() {
        if (Name.Length > 0) yield return Cased(Name, Style.Name);
        if (Title.Length > 0) yield return Cased(Title, Style.Headline);
        if (Contact.Length > 0) yield return Cased(Contact, Style.Contact);

        if (Summary.Length > 0) { yield return Heading(SummaryHeading); yield return Summary; }

        if (Skills.Count > 0) {
            yield return Heading(SkillsHeading);
            foreach (var s in Skills) {
                if (s.Category.Length > 0) yield return Cased(s.Category, Style.SkillCategory);
                if (s.Skills.Length > 0) yield return s.Skills;
            }
        }

        if (Experience.Count > 0) {
            yield return Heading(ExperienceHeading);
            foreach (var e in Experience) {
                if (e.Heading.Length > 0) yield return Cased(e.Heading, Style.CompanyHeading);
                if (e.Subtitle.Length > 0) yield return e.Subtitle;
                if (e.Metadata.Length > 0) yield return e.Metadata;
                foreach (var line in e.Lines) yield return Bullet + line.Text;
            }
        }

        if (Certifications.Count > 0) {
            yield return Heading(CertificationsHeading);
            foreach (var c in Certifications) yield return Bullet + c;
        }

        if (Education.Count > 0) {
            yield return Heading(EducationHeading);
            foreach (var ed in Education) {
                if (ed.Heading.Length > 0) yield return ed.Heading;
                if (ed.Dates.Length > 0) yield return ed.Dates;
            }
        }
    }

    public const string Bullet = "• ";

    public string Heading(string text) => Cased(text, Style.SectionHeading);

    /// <summary>The uppercase token, applied once so both renderers and the parity check agree.</summary>
    public static string Cased(string text, TextStyle style) => style.Uppercase ? text.ToUpperInvariant() : text;

    internal static string Join(string a, string b) =>
        a.Length > 0 && b.Length > 0 ? a + " — " + b : a.Length > 0 ? a : b;

    /// <summary>Joins the parts that exist with " | ", so an empty value never leaves a dangling bar.</summary>
    internal static string Pipe(params string[] parts) =>
        string.Join(" | ", parts.Select(p => (p ?? "").Trim()).Where(p => p.Length > 0));

    internal static string Range(string start, string end) =>
        start.Length > 0 && end.Length > 0 ? start + " - " + end : start.Length > 0 ? start : end;

    static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!.Trim() : "";

    static JsonElement? Obj(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();
}

public sealed class GenerationResult {
    public bool DocxRequested { get; init; }
    public bool PdfRequested { get; init; }
    public string? DocxPath { get; set; }
    public string? PdfPath { get; set; }
    public string? DocxError { get; set; }
    public string? PdfError { get; set; }
    public string? FatalError { get; set; }

    /// <summary>Style system: where the effective style was written, when a debug path was supplied.</summary>
    public string? EffectiveStylePath { get; set; }

    /// <summary>Style system: style values the normalizer had to correct before rendering.</summary>
    public List<string> StyleWarnings { get; } = new();

    /// <summary>The dated job folder the documents were written into.</summary>
    public string? OutputFolder { get; set; }

    /// <summary>The resume-info.json written beside them.</summary>
    public string? MetadataPath { get; set; }

    public bool Disabled => !DocxRequested && !PdfRequested;
    public bool DocxGenerated => DocxPath is not null;
    public bool PdfGenerated => PdfPath is not null;
    public bool AnyFailure => FatalError is not null || DocxError is not null || PdfError is not null;

    /// <summary>Status line for the queue. Document problems are reported separately from the job itself.</summary>
    public string Describe() {
        if (Disabled) return "Documents disabled in Settings — the tailored JSON is saved.";
        if (FatalError is not null) return "Documents not generated — " + FatalError + " The tailored JSON is saved.";

        var parts = new List<string>();
        if (DocxGenerated) parts.Add("DOCX generated");
        else if (DocxError is not null) parts.Add("DOCX failed: " + DocxError);
        if (PdfGenerated) parts.Add("PDF generated");
        else if (PdfError is not null) parts.Add("PDF failed: " + PdfError);

        var text = string.Join(" • ", parts);
        var style = StyleWarnings.Count == 0 ? ""
            : $" {StyleWarnings.Count} style value{(StyleWarnings.Count == 1 ? " was" : "s were")} adjusted to stay within the allowed range.";
        var folder = OutputFolder is null ? "" : " Saved in " + Path.GetFileName(Path.GetDirectoryName(OutputFolder)) + "\\" + Path.GetFileName(OutputFolder) + ".";

        return (AnyFailure ? text + " — the tailored JSON is saved and can be regenerated." : "Documents generated: " + text + ".") + folder + style;
    }
}

public static class ResumeGenerator {
    /// <summary>
    /// Generates the enabled documents from an already-validated profile file. Never throws and never
    /// writes the profile. Output goes to ResumeRoot\yyyy-MM-dd\&lt;Company&gt; - &lt;Role&gt;\Resume.docx;
    /// <see cref="ResumeOutputManager"/> owns every folder and file-name decision, including the
    /// revision suffix that keeps a second run from overwriting the first.
    /// <paramref name="effectiveStylePath"/> is optional: when given, the fully resolved style is
    /// written there, which is what makes a styling problem debuggable after the fact.
    /// </summary>
    public static GenerationResult Generate(
        string company, string role, string profilePath, AppSettings settings, string? effectiveStylePath = null,
        string? jobId = null, string? jobUrl = null) {

        var result = new GenerationResult { DocxRequested = settings.Docx, PdfRequested = settings.Pdf };
        if (result.Disabled) return result;

        if (string.IsNullOrWhiteSpace(settings.ResumeRootFolder)) {
            result.FatalError = "the Resume Root Folder is not configured in Settings.";
            return result;
        }
        if (!File.Exists(profilePath)) {
            result.FatalError = "the validated profile file was not found.";
            return result;
        }

        ResumeDocument document;
        try {
            Directory.CreateDirectory(settings.ResumeRootFolder);
            document = ResumeDocument.FromProfileFile(profilePath);
        } catch (Exception ex) {
            result.FatalError = Explain(ex);
            return result;
        }

        result.StyleWarnings.AddRange(document.StyleWarnings);

        if (effectiveStylePath is not null) {
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(effectiveStylePath) ?? settings.ResumeRootFolder);
                File.WriteAllText(effectiveStylePath, document.Style.ToJsonString());
                result.EffectiveStylePath = effectiveStylePath;
            } catch {
                // Debug output must never affect the documents.
            }
        }

        ResumeOutputPaths paths;
        try {
            paths = ResumeOutputManager.Resolve(settings.ResumeRootFolder, company, role);
            Directory.CreateDirectory(paths.JobFolder);
        } catch (Exception ex) {
            result.FatalError = Explain(ex);
            return result;
        }

        result.OutputFolder = paths.JobFolder;
        PerfLog.Line("OUTPUT folder: " + paths.JobFolder);

        if (settings.Docx) {
            try {
                WriteAtomically(paths.DocxPath, temp => DocxWriter.Write(document, temp));
                result.DocxPath = paths.DocxPath;
            } catch (Exception ex) {
                result.DocxError = Explain(ex);
            }
        }

        if (settings.Pdf) {
            try {
                WriteAtomically(paths.PdfPath, temp => PdfWriter.Write(document, temp));
                result.PdfPath = paths.PdfPath;
            } catch (Exception ex) {
                result.PdfError = Explain(ex);
            }
        }

        // The metadata is written even when a document failed, so the folder still records what was
        // attempted and for which job. A metadata failure never changes the document outcome.
        try {
            ResumeOutputManager.SaveMetadata(paths.MetadataPath, new ResumeOutputMetadata {
                JobId = jobId ?? "",
                Company = company ?? "",
                Role = role ?? "",
                JobUrl = jobUrl ?? "",
                GeneratedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                DocxFile = result.DocxPath is null ? null : Path.GetFileName(result.DocxPath),
                PdfFile = result.PdfPath is null ? null : Path.GetFileName(result.PdfPath)
            });
            result.MetadataPath = paths.MetadataPath;
            PerfLog.Line("OUTPUT metadata saved: " + Path.GetFileName(paths.MetadataPath));
        } catch (Exception ex) {
            PerfLog.Line("OUTPUT metadata failed: " + ex.Message);
        }

        return result;
    }

    /// <summary>Write to a temp file and move into place, so a crash never leaves a truncated document.</summary>
    static void WriteAtomically(string target, Action<string> write) {
        var temp = target + ".tmp";
        try {
            if (File.Exists(temp)) File.Delete(temp);
            write(temp);
            File.Move(temp, target, overwrite: true);
        } finally {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
        }
    }

    internal static string Explain(Exception ex) => ex switch {
        // Windows reports a file held open by Word as ACCESS_DENIED on the replace, so name the
        // likely cause rather than the generic permission wording.
        UnauthorizedAccessException => "the file is open in another program, or is read-only — close it and generate again.",
        IOException io when io.Message.Contains("another process", StringComparison.OrdinalIgnoreCase)
            => "the file is open in another program — close it and generate again.",
        DirectoryNotFoundException => "the Resume Root Folder could not be found.",
        _ => ex.Message
    };
}

/// <summary>
/// DOCX via the Open XML SDK — no Word, no COM, no Office automation.
///
/// Style system: every visual decision comes from the <see cref="ResumeStyle"/>; this class only converts
/// those tokens into Word's units (half-points, twips) and its real built-in styles, so the Navigation
/// Pane shows the resume's structure.
/// </summary>
public static class DocxWriter {
    // Word's built-in style ids. Section headings are Heading 1, company and skill-category headings
    // are Heading 2, and an optional project subtitle is Heading 3.
    const string Normal = "Normal", H1 = "Heading1", H2 = "Heading2", H3 = "Heading3";

    public static void Write(ResumeDocument resume, string path) {
        var style = resume.Style;

        using var word = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = word.AddMainDocumentPart();
        main.Document = new Document();
        AddStyleDefinitions(main, style);

        var body = main.Document.AppendChild(new Body());

        if (resume.Name.Length > 0) body.AppendChild(Paragraph(ResumeDocument.Cased(resume.Name, style.Name), style.Name, style));
        if (resume.Title.Length > 0) body.AppendChild(Paragraph(ResumeDocument.Cased(resume.Title, style.Headline), style.Headline, style));
        if (resume.Contact.Length > 0) body.AppendChild(Paragraph(ResumeDocument.Cased(resume.Contact, style.Contact), style.Contact, style));

        if (resume.Summary.Length > 0) {
            AddSectionHeading(body, resume.Heading(ResumeDocument.SummaryHeading), style);
            // Forced regular weight: the summary never carries inline emphasis.
            body.AppendChild(Paragraph(resume.Summary, style.Body, style, bold: false));
        }

        if (resume.Skills.Count > 0) {
            AddSectionHeading(body, resume.Heading(ResumeDocument.SkillsHeading), style);
            foreach (var skill in resume.Skills) {
                if (skill.Category.Length > 0)
                    body.AppendChild(Paragraph(ResumeDocument.Cased(skill.Category, style.SkillCategory), style.SkillCategory, style, styleId: H2));
                // Skill values are always regular weight, whatever the style asked for.
                if (skill.Skills.Length > 0) body.AppendChild(Paragraph(skill.Skills, style.SkillValues, style, bold: false));
            }
        }

        if (resume.Experience.Count > 0) {
            AddSectionHeading(body, resume.Heading(ResumeDocument.ExperienceHeading), style);
            foreach (var job in resume.Experience) {
                if (job.Heading.Length > 0)
                    body.AppendChild(Paragraph(ResumeDocument.Cased(job.Heading, style.CompanyHeading), style.CompanyHeading, style, styleId: H2));
                if (job.Subtitle.Length > 0)
                    body.AppendChild(Paragraph(job.Subtitle, style.Subtitle, style, styleId: H3));
                if (job.Metadata.Length > 0) AddStyledMetadata(body, job.Metadata, style);
                foreach (var line in job.Lines) AddStyledBullet(body, line, style);
            }
        }

        if (resume.Certifications.Count > 0) {
            AddSectionHeading(body, resume.Heading(ResumeDocument.CertificationsHeading), style);
            foreach (var certification in resume.Certifications)
                AddStyledBullet(body, BulletLine.Plain(certification), style);
        }

        if (resume.Education.Count > 0) {
            AddSectionHeading(body, resume.Heading(ResumeDocument.EducationHeading), style);
            foreach (var entry in resume.Education) {
                // Education is ordinary body text: no emphasis, whatever the style asked for.
                if (entry.Heading.Length > 0) body.AppendChild(Paragraph(entry.Heading, style.Education, style, bold: false));
                if (entry.Dates.Length > 0) body.AppendChild(Paragraph(entry.Dates, style.Education, style, bold: false));
            }
        }

        body.AppendChild(PageSetup(style.Page));
        main.Document.Save();
    }

    // ---------- reusable style application ----------

    static void AddSectionHeading(Body body, string text, ResumeStyle style) =>
        body.AppendChild(Paragraph(text, style.SectionHeading, style, styleId: H1));

    static void AddStyledMetadata(Body body, string text, ResumeStyle style) =>
        body.AppendChild(Paragraph(text, style.Metadata, style));

    /// <summary>
    /// Bullets use a literal bullet character with a hanging indent rather than a numbering part.
    /// Fewer moving parts, and ATS text extraction sees exactly the bullet text either way.
    /// </summary>
    static void AddStyledBullet(Body body, BulletLine line, ResumeStyle style) {
        var paragraph = new Paragraph(ParagraphProps(style.Bullet, null));
        paragraph.AppendChild(Run(ResumeDocument.Bullet, style.Bullet, style, bold: false));
        foreach (var segment in line.Segments)
            paragraph.AppendChild(Run(segment.Text, style.Bullet, style, bold: segment.Bold));
        body.AppendChild(paragraph);
    }

    static Paragraph Paragraph(string text, TextStyle text_style, ResumeStyle style, string? styleId = null, bool? bold = null) {
        var paragraph = new Paragraph(ParagraphProps(text_style, styleId));
        paragraph.AppendChild(Run(text, text_style, style, bold ?? text_style.Bold));
        return paragraph;
    }

    /// <summary>
    /// applyParagraphStyle: spacing, alignment, indents, borders, keep-with-next.
    /// The append order is the one WordprocessingML's schema requires (pStyle, keepNext, pBdr,
    /// spacing, ind, jc) — out of order, Word reports the file as corrupt.
    /// </summary>
    static ParagraphProperties ParagraphProps(TextStyle style, string? styleId) {
        var props = new ParagraphProperties();
        if (styleId is not null) props.AppendChild(new ParagraphStyleId { Val = styleId });
        if (style.KeepWithNext) props.AppendChild(new KeepNext());

        if (style.BottomBorder)
            props.AppendChild(new ParagraphBorders(new BottomBorder {
                Val = BorderValues.Single, Size = 6, Space = 1, Color = Hex(style.Color)
            }));

        props.AppendChild(new SpacingBetweenLines {
            Before = Twips(style.SpaceBefore).ToString(),
            After = Twips(style.SpaceAfter).ToString(),
            Line = Line(style.LineSpacing).ToString(),
            LineRule = LineSpacingRuleValues.Auto
        });

        if (style.LeftIndent > 0 || style.HangingIndent > 0) {
            var indentation = new Indentation { Left = Inches(style.LeftIndent).ToString() };
            if (style.HangingIndent > 0) indentation.Hanging = Inches(style.HangingIndent).ToString();
            props.AppendChild(indentation);
        }

        if (Alignment(style.Alignment) is JustificationValues justification)
            props.AppendChild(new Justification { Val = justification });

        return props;
    }

    /// <summary>applyRunStyle: font, weight, colour, size — again in schema order (rFonts, b, i, color, sz).</summary>
    static Run Run(string text, TextStyle text_style, ResumeStyle style, bool bold) {
        var run = new Run();
        var props = new RunProperties();
        props.AppendChild(new RunFonts { Ascii = style.Fonts.Family, HighAnsi = style.Fonts.Family, ComplexScript = style.Fonts.Family });
        if (bold) props.AppendChild(new Bold());
        if (text_style.Italic) props.AppendChild(new Italic());
        props.AppendChild(new DocumentFormat.OpenXml.Wordprocessing.Color { Val = Hex(text_style.Color) });
        props.AppendChild(new FontSize { Val = HalfPoints(text_style.FontSize).ToString() });
        props.AppendChild(new FontSizeComplexScript { Val = HalfPoints(text_style.FontSize).ToString() });
        run.AppendChild(props);
        run.AppendChild(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        return run;
    }

    // ---------- real Word styles ----------

    /// <summary>
    /// Normal plus Heading 1-3, so Word's Navigation Pane and any structure-aware reader see the
    /// resume's outline. Paragraphs also carry direct formatting, which is what keeps the appearance
    /// identical in readers that ignore the style part.
    /// </summary>
    static void AddStyleDefinitions(MainDocumentPart main, ResumeStyle style) {
        var part = main.AddNewPart<StyleDefinitionsPart>();
        part.Styles = new Styles();

        part.Styles.AppendChild(new DocDefaults(
            new RunPropertiesDefault(new RunPropertiesBaseStyle(
                new RunFonts { Ascii = style.Fonts.Family, HighAnsi = style.Fonts.Family, ComplexScript = style.Fonts.Family },
                new DocumentFormat.OpenXml.Wordprocessing.Color { Val = Hex(style.Colors.Body) },
                new FontSize { Val = HalfPoints(style.Body.FontSize).ToString() })),
            new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(
                new SpacingBetweenLines {
                    Before = "0", After = Twips(style.Body.SpaceAfter).ToString(),
                    Line = Line(style.Body.LineSpacing).ToString(), LineRule = LineSpacingRuleValues.Auto
                }))));

        part.Styles.AppendChild(Definition(Normal, "Normal", null, style.Body, style, outlineLevel: null, isDefault: true));
        part.Styles.AppendChild(Definition(H1, "heading 1", Normal, style.SectionHeading, style, outlineLevel: 0));
        part.Styles.AppendChild(Definition(H2, "heading 2", Normal, style.CompanyHeading, style, outlineLevel: 1));
        part.Styles.AppendChild(Definition(H3, "heading 3", Normal, style.Subtitle, style, outlineLevel: 2));

        part.Styles.Save();
    }

    static Style Definition(string id, string name, string? basedOn, TextStyle text_style, ResumeStyle style,
                            int? outlineLevel, bool isDefault = false) {
        var definition = new Style { Type = StyleValues.Paragraph, StyleId = id, Default = isDefault };
        definition.AppendChild(new StyleName { Val = name });
        if (basedOn is not null) {
            definition.AppendChild(new BasedOn { Val = basedOn });
            definition.AppendChild(new NextParagraphStyle { Val = basedOn });
        }
        definition.AppendChild(new PrimaryStyle());

        // Schema order again: keepNext, spacing, then outlineLvl near the end.
        var paragraphProps = new StyleParagraphProperties();
        if (text_style.KeepWithNext) paragraphProps.AppendChild(new KeepNext());
        paragraphProps.AppendChild(new SpacingBetweenLines {
            Before = Twips(text_style.SpaceBefore).ToString(),
            After = Twips(text_style.SpaceAfter).ToString(),
            Line = Line(text_style.LineSpacing).ToString(),
            LineRule = LineSpacingRuleValues.Auto
        });
        if (outlineLevel is int level) paragraphProps.AppendChild(new OutlineLevel { Val = level });
        definition.AppendChild(paragraphProps);

        var runProps = new StyleRunProperties();
        runProps.AppendChild(new RunFonts { Ascii = style.Fonts.Family, HighAnsi = style.Fonts.Family, ComplexScript = style.Fonts.Family });
        if (text_style.Bold) runProps.AppendChild(new Bold());
        if (text_style.Italic) runProps.AppendChild(new Italic());
        runProps.AppendChild(new DocumentFormat.OpenXml.Wordprocessing.Color { Val = Hex(text_style.Color) });
        runProps.AppendChild(new FontSize { Val = HalfPoints(text_style.FontSize).ToString() });
        runProps.AppendChild(new FontSizeComplexScript { Val = HalfPoints(text_style.FontSize).ToString() });
        definition.AppendChild(runProps);

        return definition;
    }

    /// <summary>Single-column page setup: size and margins only. No columns, frames or backgrounds.</summary>
    static SectionProperties PageSetup(PageStyle page) {
        var letter = page.Size.Equals("A4", StringComparison.OrdinalIgnoreCase) ? (Width: 11906u, Height: 16838u) : (Width: 12240u, Height: 15840u);
        return new SectionProperties(
            new PageSize { Width = letter.Width, Height = letter.Height },
            new PageMargin {
                Top = Inches(page.MarginTop), Bottom = Inches(page.MarginBottom),
                Left = (uint)Inches(page.MarginLeft), Right = (uint)Inches(page.MarginRight),
                Header = 0, Footer = 0, Gutter = 0
            });
    }

    // ---------- unit conversion ----------

    static int HalfPoints(double points) => (int)Math.Round(points * 2, MidpointRounding.AwayFromZero);
    static int Twips(double points) => (int)Math.Round(points * 20, MidpointRounding.AwayFromZero);
    static int Inches(double inches) => (int)Math.Round(inches * 1440, MidpointRounding.AwayFromZero);
    static int Line(double multiple) => (int)Math.Round(multiple * 240, MidpointRounding.AwayFromZero);
    static string Hex(string color) => color.TrimStart('#').ToUpperInvariant();

    static JustificationValues? Alignment(string alignment) => alignment.ToLowerInvariant() switch {
        "center" => JustificationValues.Center,
        "right" => JustificationValues.Right,
        "justify" => JustificationValues.Both,
        _ => null
    };
}
