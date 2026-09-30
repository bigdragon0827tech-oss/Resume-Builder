using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace ResumeBuilder;

// ---------------------------------------------------------------------------
// A6.6.9 — resume documents generated from a validated tailored profile.
//
// Input is always results\<jobId>.json (or the baseline profile), which has already been
// normalized and strictly validated. Generation only ever READS that file: a document failure
// can never damage or invalidate the saved JSON, and candidate-profile.json is never written here.
//
// The tailored JSON supplies the text. How the finished resume looks is a separate choice:
// the resume named for this generation, else the style reference last chosen in Settings,
// else the app's default style. The original resume is never the style source.
// A source paragraph in a style-reference DOCX with no tailored counterpart is removed.
// PDF is converted from that DOCX. DocxWriter is the default style when no reference file is used.
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
    /// writes the profile. Output goes to ResumeRoot\yyyy-MM-dd\&lt;Company&gt; - &lt;Role&gt;\&lt;Candidate Name&gt;.docx;
    /// <see cref="ResumeOutputManager"/> owns every folder and file-name decision, including the
    /// revision suffix that keeps a second run from overwriting the first.
    /// <paramref name="effectiveStylePath"/> is optional: when given, the fully resolved style is
    /// written there, which is what makes a styling problem debuggable after the fact.
    /// </summary>
    public static GenerationResult Generate(
        string company, string role, string profilePath, AppSettings settings, string? effectiveStylePath = null,
        string? jobId = null, string? jobUrl = null, string? outputFolder = null, string? styleReferencePath = null) {

        var result = new GenerationResult { DocxRequested = settings.Docx, PdfRequested = settings.Pdf };
        if (result.Disabled) return result;

        if (string.IsNullOrWhiteSpace(outputFolder) && string.IsNullOrWhiteSpace(settings.ResumeRootFolder)) {
            result.FatalError = "the Resume Root Folder is not configured in Settings.";
            return result;
        }
        if (!File.Exists(profilePath)) {
            result.FatalError = "the validated profile file was not found.";
            return result;
        }

        ResumeDocument document;
        try {
            Directory.CreateDirectory(string.IsNullOrWhiteSpace(outputFolder) ? settings.ResumeRootFolder : outputFolder);
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
            // The documents are named after the candidate (profile info.name), e.g. "Billy Lin.docx".
            // Email tasks pass their own folder. Job tasks keep the dated Resume Root layout.
            paths = string.IsNullOrWhiteSpace(outputFolder)
                ? ResumeOutputManager.Resolve(settings.ResumeRootFolder, company, role, candidateName: document.Name)
                : ResumeOutputManager.ResolveInFolder(outputFolder, document.Name);
            Directory.CreateDirectory(paths.JobFolder);
        } catch (Exception ex) {
            result.FatalError = Explain(ex);
            return result;
        }

        result.OutputFolder = paths.JobFolder;
        PerfLog.Line("OUTPUT folder: " + paths.JobFolder);

        if (settings.Docx) {
            try {
                var template = ResumeStyleSource.Resolve(styleReferencePath, settings.StyleReferenceResume);
                WriteAtomically(paths.DocxPath, temp => {
                    if (template is null) {
                        PerfLog.Line("RESUME style-template priority=default");
                        DocxWriter.Write(document, temp);
                        return;
                    }
                    PerfLog.Line("RESUME style-template priority=" + template.Priority);
                    File.Copy(template.Path, temp, overwrite: true);
                    TemplateDocxWriter.ReplaceText(temp, document);
                    PerfLog.Line("RESUME content-replaced");
                });
                result.DocxPath = paths.DocxPath;
                PerfLog.Line("RESUME tailored-saved " + paths.DocxPath);
            } catch (Exception ex) {
                result.DocxError = Explain(ex);
            }
        }

        if (settings.Pdf) {
            var sourceDocx = result.DocxPath ?? "";
            PerfLog.Line("PDF source-docx " + sourceDocx);
            PerfLog.Line("PDF convert-start");
            try {
                if (sourceDocx.Length == 0 || !File.Exists(sourceDocx))
                    throw new PdfConvertException("missing-docx", "The tailored DOCX was not created, so the PDF was not converted.");
                WriteAtomically(paths.PdfPath, temp => PdfWriter.ConvertDocx(sourceDocx, temp));
                result.PdfPath = paths.PdfPath;
                PerfLog.Line("PDF convert-success " + paths.PdfPath);
            } catch (PdfConvertException ex) {
                PerfLog.Line("PDF convert-failed " + ex.Reason);
                result.PdfError = ex.Message;
            } catch (Exception ex) {
                PerfLog.Line("PDF convert-failed " + ex.GetType().Name);
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

    /// <summary>
    /// Which DOCX supplies the look. The original resume is not a choice here.
    /// A missing request file falls through to the last selected reference, then to no file.
    /// </summary>
    public static class ResumeStyleSource {
        public sealed class Choice {
            public string Priority = "";
            public string Path = "";
        }

        public static Choice? Resolve(string? requestPath, string? selectedPath) {
            if (Usable(requestPath))
                return new Choice { Priority = "request", Path = Path.GetFullPath(requestPath!) };
            if (Usable(selectedPath))
                return new Choice { Priority = "selected", Path = Path.GetFullPath(selectedPath!) };
            return null;
        }

        public static bool Usable(string? path) =>
            !string.IsNullOrWhiteSpace(path)
            && path.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)
            && File.Exists(path);
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

/// <summary>
/// Copies the original resume and replaces paragraph text in place.
/// Paragraph properties, run properties, styles, numbering, and section settings are left as copied.
/// </summary>
public static class TemplateDocxWriter {
    public static void ReplaceText(string docxPath, ResumeDocument resume) {
        using var word = WordprocessingDocument.Open(docxPath, true);
        var body = word.MainDocumentPart?.Document?.Body
            ?? throw new InvalidDataException("The original resume has no document body.");
        var paragraphs = body.Descendants<Paragraph>().Where(paragraph => !IsField(paragraph)).ToList();

        var index = 0;
        var preamble = new List<Paragraph>();
        while (index < paragraphs.Count && HeadingLevel(paragraphs[index]) != 1) {
            if (ParagraphText(paragraphs[index]).Length > 0)
                preamble.Add(paragraphs[index]);
            index++;
        }
        FillPreamble(preamble, resume);

        while (index < paragraphs.Count) {
            if (HeadingLevel(paragraphs[index]) != 1) {
                index++;
                continue;
            }
            var kind = SectionKind(ParagraphText(paragraphs[index]));
            index++;
            var section = new List<Paragraph>();
            while (index < paragraphs.Count && HeadingLevel(paragraphs[index]) != 1)
                section.Add(paragraphs[index++]);

            switch (kind) {
                case "summary":
                    FillSummary(section, resume.Summary);
                    break;
                case "skills":
                    FillSkills(section, resume.Skills);
                    break;
                case "experience":
                    FillExperience(section, resume.Experience);
                    break;
                case "certs":
                    FillCertifications(section, resume.Certifications);
                    break;
                case "education":
                    FillEducation(section, resume.Education);
                    break;
            }
        }

        word.MainDocumentPart!.Document.Save();
    }

    static void FillPreamble(List<Paragraph> lines, ResumeDocument resume) {
        if (lines.Count == 0) return;
        if (lines.Count == 1) {
            if (resume.Name.Length > 0) Apply(lines[0], resume.Name);
            return;
        }
        if (resume.Name.Length > 0) Apply(lines[0], resume.Name);
        if (lines.Count == 2) {
            var second = resume.Title.Length > 0 ? resume.Title : resume.Contact;
            if (second.Length > 0) Apply(lines[1], second);
            return;
        }
        if (resume.Title.Length > 0) Apply(lines[1], resume.Title);
        if (resume.Contact.Length > 0) Apply(lines[2], resume.Contact);
    }

    static void FillSummary(List<Paragraph> section, string summary) {
        var bodies = ContentParagraphs(section);
        if (string.IsNullOrWhiteSpace(summary)) {
            foreach (var paragraph in bodies) paragraph.Remove();
            return;
        }
        if (bodies.Count == 0) return;
        var parts = SplitAcross(summary, bodies.Count);
        for (var i = 0; i < bodies.Count; i++) {
            if (parts[i].Length > 0) Apply(bodies[i], parts[i]);
            else bodies[i].Remove();
        }
    }

    static void FillSkills(List<Paragraph> section, List<SkillBlock> skills) {
        var groups = Groups(section);
        RemoveLooseText(section, groups);
        if (skills.Count == 0) {
            foreach (var group in groups) RemoveGroup(group);
            return;
        }
        var count = Math.Min(groups.Count, skills.Count);
        for (var i = 0; i < count; i++)
            FillSkillGroup(groups[i], skills[i]);

        if (skills.Count > groups.Count && groups.Count > 0) {
            var sample = groups[^1];
            var anchor = LastOf(sample);
            for (var i = groups.Count; i < skills.Count; i++) {
                var created = CloneGroup(sample, ref anchor);
                FillSkillGroup(created, skills[i]);
            }
        }

        for (var i = skills.Count; i < groups.Count; i++)
            RemoveGroup(groups[i]);
    }

    static void FillSkillGroup(ParagraphGroup group, SkillBlock skill) {
        if (group.Heading is not null) {
            if (skill.Category.Length > 0) Apply(group.Heading, skill.Category);
            else if (skill.Skills.Length > 0 && group.Intro.Count == 0 && group.Bullets.Count == 0)
                Apply(group.Heading, skill.Skills);
            else SetParagraphText(group.Heading, "");
        }

        var slots = group.Intro.Concat(group.Bullets).ToList();
        if (skill.Skills.Length == 0) {
            foreach (var paragraph in slots) paragraph.Remove();
            return;
        }
        if (slots.Count == 0 && group.Heading is not null && skill.Category.Length > 0)
            slots.Add(InsertClone(group.Heading));
        FillRepeated(slots, new List<string> { skill.Skills }, slots.Any(IsBulletParagraph));
    }

    static void FillExperience(List<Paragraph> section, List<ExperienceBlock> jobs) {
        var groups = Groups(section);
        RemoveLooseText(section, groups);
        if (jobs.Count == 0) {
            foreach (var group in groups) RemoveGroup(group);
            return;
        }
        var count = Math.Min(groups.Count, jobs.Count);
        for (var i = 0; i < count; i++)
            FillJob(groups[i], jobs[i]);

        if (jobs.Count > groups.Count && groups.Count > 0) {
            var sample = groups[^1];
            var anchor = LastOf(sample);
            for (var i = groups.Count; i < jobs.Count; i++) {
                var created = CloneGroup(sample, ref anchor);
                FillJob(created, jobs[i]);
            }
        }

        for (var i = jobs.Count; i < groups.Count; i++)
            RemoveGroup(groups[i]);
    }

    /// <summary>
    /// Heading, then location/employment/arrangement, then an optional subtitle.
    /// Any further source line (a project name or a technology line the JSON does not have)
    /// is removed. Bullets are created or removed so the accepted lines all appear.
    /// </summary>
    static void FillJob(ParagraphGroup group, ExperienceBlock job) {
        if (group.Heading is not null) {
            if (job.Heading.Length > 0) Apply(group.Heading, job.Heading);
            else SetParagraphText(group.Heading, "");
        }

        var intro = new List<string>();
        if (job.Metadata.Length > 0) intro.Add(job.Metadata);
        if (job.Subtitle.Length > 0) intro.Add(job.Subtitle);
        if (group.Intro.Count == 0 && intro.Count > 0 && group.Heading is not null)
            group.Intro.Add(InsertClone(group.Heading));
        FillRepeated(group.Intro, intro, keepBullet: false);

        var lines = job.Lines.Select(line => line.Text).Where(text => text.Length > 0).ToList();
        if (group.Bullets.Count == 0 && lines.Count > 0) {
            var prototype = group.Intro.LastOrDefault(paragraph => paragraph.Parent is not null) ?? group.Heading;
            if (prototype is not null) group.Bullets.Add(InsertClone(prototype));
        }
        FillRepeated(group.Bullets, lines, keepBullet: true);
    }

    static void FillCertifications(List<Paragraph> section, List<string> certifications) {
        var slots = ContentParagraphs(section);
        if (slots.Count == 0) return;
        FillRepeated(slots, certifications, keepBullet: slots.Any(IsBulletParagraph));
    }

    static void FillEducation(List<Paragraph> section, List<EducationBlock> entries) {
        var lines = ContentParagraphs(section);
        if (entries.Count == 0) {
            foreach (var paragraph in lines) paragraph.Remove();
            return;
        }
        if (lines.Count == 0) return;

        if (lines.Count == entries.Count * 2) {
            for (var i = 0; i < entries.Count; i++) {
                var heading = entries[i].Heading.Replace(" — ", " - ");
                if (heading.Length > 0) Apply(lines[i * 2], heading);
                else SetParagraphText(lines[i * 2], "");
                if (entries[i].Dates.Length > 0) Apply(lines[i * 2 + 1], entries[i].Dates);
                else lines[i * 2 + 1].Remove();
            }
            return;
        }

        var written = new List<string>();
        foreach (var entry in entries) {
            var line = EducationLine(entry, ParagraphText(lines[0]));
            if (line.Length > 0) written.Add(line);
        }
        FillRepeated(lines, written, keepBullet: false);
    }

    static List<Paragraph> ContentParagraphs(List<Paragraph> section) =>
        section.Where(paragraph => HeadingLevel(paragraph) == 0 && ParagraphText(paragraph).Length > 0).ToList();

    /// <summary>Text that sits in the section but not under a company or skill heading is source residue.</summary>
    static void RemoveLooseText(List<Paragraph> section, List<ParagraphGroup> groups) {
        if (groups.Count == 0) return;
        var used = new HashSet<Paragraph>();
        foreach (var group in groups) {
            if (group.Heading is not null) used.Add(group.Heading);
            foreach (var paragraph in group.Intro) used.Add(paragraph);
            foreach (var paragraph in group.Bullets) used.Add(paragraph);
        }
        foreach (var paragraph in section) {
            if (used.Contains(paragraph) || HeadingLevel(paragraph) != 0) continue;
            if (ParagraphText(paragraph).Length > 0) paragraph.Remove();
        }
    }

    static Paragraph InsertClone(Paragraph prototype) {
        var clone = (Paragraph)prototype.CloneNode(true);
        prototype.InsertAfterSelf(clone);
        return clone;
    }

    static string EducationLine(EducationBlock entry, string template) {
        var degree = entry.Degree.Trim();
        if (entry.Major.Length > 0 && degree.IndexOf(entry.Major, StringComparison.OrdinalIgnoreCase) < 0)
            degree = degree.Length > 0 ? degree + " " + entry.Major : entry.Major.Trim();
        var school = entry.School.Trim();
        var separator = template.Contains(" — ", StringComparison.Ordinal) ? " — " : " - ";
        var head = degree.Length > 0 && school.Length > 0 ? degree + separator + school
            : degree.Length > 0 ? degree : school;
        if (entry.Dates.Length > 0 && !head.Contains(entry.Dates, StringComparison.Ordinal))
            head = head.Length > 0 ? head + " | " + entry.Dates.Trim() : entry.Dates.Trim();
        return head;
    }

    static void FillRepeated(List<Paragraph> slots, List<string> lines, bool keepBullet) {
        if (lines.Count == 0) {
            foreach (var slot in slots) Detach(slot);
            return;
        }
        if (slots.Count == 0) return;
        var count = Math.Min(slots.Count, lines.Count);
        for (var i = 0; i < count; i++)
            Apply(slots[i], lines[i], keepBullet);

        var anchor = slots[count - 1];
        for (var i = count; i < lines.Count; i++) {
            var clone = (Paragraph)anchor.CloneNode(true);
            anchor.InsertAfterSelf(clone);
            anchor = clone;
            Apply(clone, lines[i], keepBullet);
        }

        for (var i = lines.Count; i < slots.Count; i++)
            Detach(slots[i]);
    }

    static void Apply(Paragraph paragraph, string incoming, bool keepBullet = false) {
        var existing = ParagraphText(paragraph);
        var text = incoming ?? "";
        if (!existing.Contains("  |  ", StringComparison.Ordinal))
            text = text.Replace("  |  ", " | ");
        if (keepBullet || IsBulletParagraph(paragraph)) {
            var prefix = BulletPrefix(existing);
            if (prefix.Length > 0) {
                var marker = prefix.Trim();
                if (text.StartsWith(marker, StringComparison.Ordinal))
                    text = text[marker.Length..].TrimStart();
                text = prefix + text.TrimStart();
            }
        }
        SetParagraphText(paragraph, MatchCase(existing, Clean(text)));
    }

    static void SetParagraphText(Paragraph paragraph, string text) {
        var nodes = paragraph.Descendants<Text>().ToList();
        if (nodes.Count == 0) {
            var run = paragraph.Elements<Run>().FirstOrDefault() ?? paragraph.AppendChild(new Run());
            run.AppendChild(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
            return;
        }
        nodes[0].Space = SpaceProcessingModeValues.Preserve;
        nodes[0].Text = text;
        for (var i = 1; i < nodes.Count; i++)
            nodes[i].Text = "";
    }

    static List<ParagraphGroup> Groups(List<Paragraph> section) {
        var groups = new List<ParagraphGroup>();
        ParagraphGroup? current = null;
        foreach (var paragraph in section) {
            if (HeadingLevel(paragraph) == 2) {
                current = new ParagraphGroup { Heading = paragraph };
                groups.Add(current);
                continue;
            }
            if (current is null || ParagraphText(paragraph).Length == 0) continue;
            if (IsBulletParagraph(paragraph)) current.Bullets.Add(paragraph);
            else current.Intro.Add(paragraph);
        }
        return groups;
    }

    static ParagraphGroup CloneGroup(ParagraphGroup sample, ref Paragraph anchor) {
        var created = new ParagraphGroup();
        if (sample.Heading is { Parent: not null }) {
            var clone = (Paragraph)sample.Heading.CloneNode(true);
            anchor.InsertAfterSelf(clone);
            anchor = clone;
            created.Heading = clone;
        }
        foreach (var paragraph in sample.Intro) {
            if (paragraph.Parent is null) continue;
            var clone = (Paragraph)paragraph.CloneNode(true);
            anchor.InsertAfterSelf(clone);
            anchor = clone;
            created.Intro.Add(clone);
        }
        foreach (var paragraph in sample.Bullets) {
            if (paragraph.Parent is null) continue;
            var clone = (Paragraph)paragraph.CloneNode(true);
            anchor.InsertAfterSelf(clone);
            anchor = clone;
            created.Bullets.Add(clone);
        }
        return created;
    }

    static Paragraph LastOf(ParagraphGroup group) {
        for (var i = group.Bullets.Count - 1; i >= 0; i--)
            if (group.Bullets[i].Parent is not null) return group.Bullets[i];
        for (var i = group.Intro.Count - 1; i >= 0; i--)
            if (group.Intro[i].Parent is not null) return group.Intro[i];
        return group.Heading!;
    }

    static void RemoveGroup(ParagraphGroup group) {
        Detach(group.Heading);
        foreach (var paragraph in group.Intro) Detach(paragraph);
        foreach (var paragraph in group.Bullets) Detach(paragraph);
    }

    static void Detach(Paragraph? paragraph) {
        if (paragraph?.Parent is not null) paragraph.Remove();
    }

    static int HeadingLevel(Paragraph paragraph) {
        var style = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value?.Replace(" ", "") ?? "";
        if (style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) && style.Length > "Heading".Length) {
            var suffix = style["Heading".Length..];
            if ((suffix is "1" or "2" or "3") && int.TryParse(suffix, out var level))
                return level;
        }

        var text = ParagraphText(paragraph).Trim();
        if (text.Length == 0 || text.Length > 40 || IsBulletParagraph(paragraph)) return 0;
        if (SectionKind(text) == "other") return 0;
        if (LettersAreUpper(text)) return 1;
        return 0;
    }

    static string SectionKind(string heading) {
        var text = heading.ToLowerInvariant();
        if (text.Contains("summary") || text.Contains("objective") || text.Contains("profile")) return "summary";
        if (text.Contains("skill")) return "skills";
        if (text.Contains("experience") || text.Contains("employment") || text.Contains("work history")) return "experience";
        if (text.Contains("certif") || text.Contains("licen")) return "certs";
        if (text.Contains("educat")) return "education";
        return "other";
    }

    static bool IsBulletParagraph(Paragraph paragraph) {
        if (paragraph.ParagraphProperties?.NumberingProperties is not null) return true;
        var text = ParagraphText(paragraph);
        var i = 0;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        if (i >= text.Length) return false;
        var c = text[i];
        if (c is '•' or '·' or '●' or '◦' or '▪' or '▸' or '►' or '\u2023' or '\u2043' or '\u2219' or '\uF0B7')
            return true;
        return c is '-' or '*' or '–' or '—' && i + 1 < text.Length && text[i + 1] == ' ';
    }

    static string BulletPrefix(string existing) {
        var i = 0;
        while (i < existing.Length && !char.IsLetterOrDigit(existing[i])) i++;
        if (i == 0 || i > 6) return "";
        var prefix = existing[..i];
        return prefix.Trim().Length == 0 ? "" : prefix;
    }

    static string ParagraphText(Paragraph paragraph) =>
        string.Concat(paragraph.Descendants<Text>().Select(node => node.Text));

    static bool IsField(Paragraph paragraph) => paragraph.Descendants<FieldChar>().Any();

    static string MatchCase(string existing, string incoming) =>
        LettersAreUpper(existing) ? incoming.ToUpperInvariant() : incoming;

    static bool LettersAreUpper(string text) {
        var any = false;
        foreach (var c in text) {
            if (!char.IsLetter(c)) continue;
            if (char.IsLower(c)) return false;
            any = true;
        }
        return any;
    }

    static string Clean(string text) {
        var buffer = new char[text.Length];
        var n = 0;
        foreach (var c in text) {
            if (c is '\r' or '\n' or '\t') {
                if (n == 0 || buffer[n - 1] == ' ') continue;
                buffer[n++] = ' ';
                continue;
            }
            if (char.IsControl(c)) continue;
            buffer[n++] = c;
        }
        return new string(buffer, 0, n).Trim();
    }

    static List<string> SplitAcross(string text, int slots) {
        var parts = new string[slots];
        for (var i = 0; i < slots; i++) parts[i] = "";
        var pieces = BreakText(text);
        if (pieces.Count == 0 || slots == 0) return parts.ToList();
        if (slots == 1) {
            parts[0] = string.Join(" ", pieces);
            return parts.ToList();
        }
        for (var i = 0; i < pieces.Count; i++) {
            var bucket = (int)((long)i * slots / pieces.Count);
            if (bucket >= slots) bucket = slots - 1;
            parts[bucket] = parts[bucket].Length == 0 ? pieces[i] : parts[bucket] + " " + pieces[i];
        }
        return parts.ToList();
    }

    static List<string> BreakText(string text) {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var nonempty = new List<string>();
        foreach (var line in lines) {
            var trimmed = line.Trim();
            if (trimmed.Length > 0) nonempty.Add(trimmed);
        }
        if (nonempty.Count != 1) return nonempty;

        var source = nonempty[0];
        var sentences = new List<string>();
        var start = 0;
        for (var i = 0; i < source.Length; i++) {
            if (source[i] is '.' or '!' or '?' && i + 1 < source.Length && source[i + 1] == ' ') {
                var sentence = source[start..(i + 1)].Trim();
                if (sentence.Length > 0) sentences.Add(sentence);
                start = i + 2;
            }
        }
        var tail = source[start..].Trim();
        if (tail.Length > 0) sentences.Add(tail);
        return sentences.Count > 0 ? sentences : nonempty;
    }

    sealed class ParagraphGroup {
        public Paragraph? Heading;
        public List<Paragraph> Intro = new();
        public List<Paragraph> Bullets = new();
    }
}
