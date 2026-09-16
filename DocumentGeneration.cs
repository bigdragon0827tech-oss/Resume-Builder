using System.IO;
using System.Text;
using System.Text.Json;
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
// One content model (ResumeDocument) feeds both renderers, so the DOCX and the PDF always carry
// the same content even though their layout engines differ.
// ---------------------------------------------------------------------------

public sealed class ExperienceBlock {
    public string Title = "", Company = "", Dates = "", Location = "";
    public List<string> Lines = new();
}

public sealed class EducationBlock {
    public string Degree = "", Major = "", School = "", Dates = "";
}

public sealed class SkillBlock {
    public string Category = "", Skills = "";
}

/// <summary>The resume as ordered content, built once from the canonical profile.</summary>
public sealed class ResumeDocument {
    public string Name = "", Title = "", Contact = "", Summary = "";
    public List<SkillBlock> Skills = new();
    public List<ExperienceBlock> Experience = new();
    public List<string> Certifications = new();
    public List<EducationBlock> Education = new();

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
                Dates = Range(Str(item, "startDate"), Str(item, "endDate")),
                Lines = Array(item, "descriptionLines").Where(v => v.ValueKind == JsonValueKind.String)
                                                       .Select(v => v.GetString()!.Trim())
                                                       .Where(v => v.Length > 0).ToList()
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

        return r;
    }

    /// <summary>Every piece of visible text, in order. Both renderers must reproduce exactly this.</summary>
    public IEnumerable<string> ContentLines() {
        if (Name.Length > 0) yield return Name;
        if (Title.Length > 0) yield return Title;
        if (Contact.Length > 0) yield return Contact;

        if (Summary.Length > 0) { yield return "PROFESSIONAL SUMMARY"; yield return Summary; }

        if (Skills.Count > 0) {
            yield return "SKILLS";
            foreach (var s in Skills) yield return s.Category.Length > 0 ? s.Category + ": " + s.Skills : s.Skills;
        }

        if (Experience.Count > 0) {
            yield return "PROFESSIONAL EXPERIENCE";
            foreach (var e in Experience) {
                yield return Join(e.Title, e.Company);
                var meta = Join(e.Dates, e.Location);
                if (meta.Length > 0) yield return meta;
                foreach (var line in e.Lines) yield return line;
            }
        }

        if (Certifications.Count > 0) {
            yield return "CERTIFICATIONS";
            foreach (var c in Certifications) yield return c;
        }

        if (Education.Count > 0) {
            yield return "EDUCATION";
            foreach (var ed in Education) {
                var degree = ed.Major.Length > 0 && !ed.Degree.Contains(ed.Major, StringComparison.OrdinalIgnoreCase)
                    ? Join(ed.Degree, ed.Major) : ed.Degree;
                yield return Join(degree, ed.School);
                if (ed.Dates.Length > 0) yield return ed.Dates;
            }
        }
    }

    internal static string Join(string a, string b) =>
        a.Length > 0 && b.Length > 0 ? a + " — " + b : a.Length > 0 ? a : b;

    static string Range(string start, string end) =>
        start.Length > 0 && end.Length > 0 ? start + " – " + end : start.Length > 0 ? start : end;

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
        return AnyFailure ? text + " — the tailored JSON is saved and can be regenerated." : "Documents generated: " + text + ".";
    }
}

public static class ResumeGenerator {
    /// <summary>"Google - Senior Software Engineer" — company and role only, as configured.</summary>
    public static string FileBase(string company, string role) {
        var name = ResumeDocument.Join(Clean(company), Clean(role)).Replace(" — ", " - ");
        if (name.Length == 0) name = "Resume";
        return name.Length > 120 ? name.Substring(0, 120).TrimEnd() : name;
    }

    static string Clean(string value) {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(value.Length);
        foreach (var c in value ?? "") sb.Append(invalid.Contains(c) ? ' ' : c);
        var collapsed = string.Join(" ", sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Trim().TrimEnd('.');
    }

    /// <summary>
    /// Generates the enabled documents from an already-validated profile file. Never throws, never
    /// writes the profile, and overwrites the previous Company + Role output for the same job.
    /// </summary>
    public static GenerationResult Generate(
        string company, string role, string profilePath, AppSettings settings) {

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

        var baseName = FileBase(company, role);

        if (settings.Docx) {
            var target = Path.Combine(settings.ResumeRootFolder, baseName + ".docx");
            try {
                WriteAtomically(target, temp => DocxWriter.Write(document, temp));
                result.DocxPath = target;
            } catch (Exception ex) {
                result.DocxError = Explain(ex);
            }
        }

        if (settings.Pdf) {
            var target = Path.Combine(settings.ResumeRootFolder, baseName + ".pdf");
            try {
                WriteAtomically(target, temp => PdfWriter.Write(document, temp));
                result.PdfPath = target;
            } catch (Exception ex) {
                result.PdfError = Explain(ex);
            }
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

/// <summary>DOCX via the Open XML SDK — no Word, no COM, no Office automation.</summary>
public static class DocxWriter {
    public static void Write(ResumeDocument resume, string path) {
        using var word = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = word.AddMainDocumentPart();
        main.Document = new Document();
        var body = main.Document.AppendChild(new Body());

        if (resume.Name.Length > 0) body.AppendChild(Para(resume.Name, size: 32, bold: true, after: 40));
        if (resume.Title.Length > 0) body.AppendChild(Para(resume.Title, size: 24, bold: true, after: 40));
        if (resume.Contact.Length > 0) body.AppendChild(Para(resume.Contact, size: 18, after: 160));

        if (resume.Summary.Length > 0) {
            body.AppendChild(Heading("PROFESSIONAL SUMMARY"));
            body.AppendChild(Para(resume.Summary, after: 160));
        }

        if (resume.Skills.Count > 0) {
            body.AppendChild(Heading("SKILLS"));
            foreach (var s in resume.Skills) {
                var p = new Paragraph(Props(after: 40));
                if (s.Category.Length > 0) p.AppendChild(Run(s.Category + ": ", bold: true));
                p.AppendChild(Run(s.Skills));
                body.AppendChild(p);
            }
            body.AppendChild(Para("", after: 120));
        }

        if (resume.Experience.Count > 0) {
            body.AppendChild(Heading("PROFESSIONAL EXPERIENCE"));
            foreach (var e in resume.Experience) {
                body.AppendChild(Para(ResumeDocument.Join(e.Title, e.Company), bold: true, after: 20));
                var meta = ResumeDocument.Join(e.Dates, e.Location);
                if (meta.Length > 0) body.AppendChild(Para(meta, size: 18, italic: true, after: 60));
                foreach (var line in e.Lines) body.AppendChild(Bullet(line));
                body.AppendChild(Para("", after: 120));
            }
        }

        if (resume.Certifications.Count > 0) {
            body.AppendChild(Heading("CERTIFICATIONS"));
            foreach (var c in resume.Certifications) body.AppendChild(Bullet(c));
            body.AppendChild(Para("", after: 120));
        }

        if (resume.Education.Count > 0) {
            body.AppendChild(Heading("EDUCATION"));
            foreach (var ed in resume.Education) {
                var degree = ed.Major.Length > 0 && !ed.Degree.Contains(ed.Major, StringComparison.OrdinalIgnoreCase)
                    ? ResumeDocument.Join(ed.Degree, ed.Major) : ed.Degree;
                body.AppendChild(Para(ResumeDocument.Join(degree, ed.School), bold: true, after: 20));
                if (ed.Dates.Length > 0) body.AppendChild(Para(ed.Dates, size: 18, italic: true, after: 100));
            }
        }

        main.Document.Save();
    }

    static Paragraph Heading(string text) {
        var p = new Paragraph(Props(before: 120, after: 60));
        p.AppendChild(Run(text, bold: true, size: 22));
        return p;
    }

    /// <summary>
    /// Bullets use a literal bullet character with a hanging indent rather than a numbering part.
    /// Fewer moving parts, and ATS text extraction sees exactly the bullet text either way.
    /// </summary>
    static Paragraph Bullet(string text) {
        var props = Props(after: 40);
        props.AppendChild(new Indentation { Left = "360", Hanging = "180" });
        var p = new Paragraph(props);
        p.AppendChild(Run("• " + text));
        return p;
    }

    static Paragraph Para(string text, int size = 21, bool bold = false, bool italic = false, int after = 80) {
        var p = new Paragraph(Props(after: after));
        p.AppendChild(Run(text, bold, size, italic));
        return p;
    }

    static ParagraphProperties Props(int before = 0, int after = 80) {
        var props = new ParagraphProperties();
        props.AppendChild(new SpacingBetweenLines { Before = before.ToString(), After = after.ToString(), Line = "252", LineRule = LineSpacingRuleValues.Auto });
        return props;
    }

    static Run Run(string text, bool bold = false, int size = 21, bool italic = false) {
        var run = new Run();
        var props = new RunProperties();
        props.AppendChild(new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri" });
        props.AppendChild(new FontSize { Val = size.ToString() });
        if (bold) props.AppendChild(new Bold());
        if (italic) props.AppendChild(new Italic());
        run.AppendChild(props);
        run.AppendChild(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        return run;
    }
}

