using System.IO;
using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;
using PdfSharp.Fonts;
using Font = MigraDoc.DocumentObjectModel.Font;

namespace ResumeBuilder;

/// <summary>
/// PDF output via PDFsharp/MigraDoc (MIT). A6.6.12 replaced the hidden-WebView2 PrintToPdf path:
/// rendering a PDF no longer starts a browser, so it costs no extra processes and no ~80 MB of
/// transient renderer memory per document. No Word, no COM, no Office, no browser.
///
/// Both renderers still consume the same <see cref="ResumeDocument"/>, so the DOCX and the PDF carry
/// identical content — a test walks this document model and compares it with the DOCX round trip.
/// </summary>
public static class PdfWriter {
    static PdfWriter() {
        // PDFsharp resolves fonts itself; point it at the installed Windows fonts once.
        GlobalFontSettings.FontResolver ??= new ResumeFontResolver();
    }

    const string FontFamily = "Calibri";

    public static void Write(ResumeDocument resume, string path) {
        var renderer = new PdfDocumentRenderer { Document = BuildDocument(resume) };
        renderer.RenderDocument();
        renderer.PdfDocument.Save(path);
    }

    /// <summary>The document model, exposed so content parity with the DOCX can be asserted.</summary>
    public static Document BuildDocument(ResumeDocument resume) {
        var doc = new Document();
        doc.Info.Title = string.IsNullOrWhiteSpace(resume.Name) ? "Resume" : resume.Name + " — Resume";

        var normal = doc.Styles["Normal"];
        if (normal is null) throw new InvalidOperationException("MigraDoc default style is missing.");
        normal.Font.Name = FontFamily;
        normal.Font.Size = Unit.FromPoint(10.5);
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(3);

        var section = doc.AddSection();
        section.PageSetup.PageFormat = PageFormat.Letter;
        section.PageSetup.TopMargin = Unit.FromCentimeter(1.4);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(1.4);
        section.PageSetup.LeftMargin = Unit.FromCentimeter(1.5);
        section.PageSetup.RightMargin = Unit.FromCentimeter(1.5);

        if (resume.Name.Length > 0) Add(section, resume.Name, 16, bold: true, after: 2);
        if (resume.Title.Length > 0) Add(section, resume.Title, 12, bold: true, after: 2);
        if (resume.Contact.Length > 0) Add(section, resume.Contact, 9, after: 8);

        if (resume.Summary.Length > 0) {
            Heading(section, "PROFESSIONAL SUMMARY");
            Add(section, resume.Summary, after: 6);
        }

        if (resume.Skills.Count > 0) {
            Heading(section, "SKILLS");
            foreach (var s in resume.Skills) {
                var p = section.AddParagraph();
                p.Format.SpaceAfter = Unit.FromPoint(2);
                if (s.Category.Length > 0) p.AddFormattedText(s.Category + ": ", TextFormat.Bold);
                p.AddText(s.Skills);
            }
            section.AddParagraph().Format.SpaceAfter = Unit.FromPoint(4);
        }

        if (resume.Experience.Count > 0) {
            Heading(section, "PROFESSIONAL EXPERIENCE");
            foreach (var e in resume.Experience) {
                Add(section, ResumeDocument.Join(e.Title, e.Company), bold: true, after: 1);
                var meta = ResumeDocument.Join(e.Dates, e.Location);
                if (meta.Length > 0) Add(section, meta, 9, italic: true, after: 3);
                foreach (var line in e.Lines) Bullet(section, line);
                section.AddParagraph().Format.SpaceAfter = Unit.FromPoint(4);
            }
        }

        if (resume.Certifications.Count > 0) {
            Heading(section, "CERTIFICATIONS");
            foreach (var c in resume.Certifications) Bullet(section, c);
            section.AddParagraph().Format.SpaceAfter = Unit.FromPoint(4);
        }

        if (resume.Education.Count > 0) {
            Heading(section, "EDUCATION");
            foreach (var ed in resume.Education) {
                var degree = ed.Major.Length > 0 && !ed.Degree.Contains(ed.Major, StringComparison.OrdinalIgnoreCase)
                    ? ResumeDocument.Join(ed.Degree, ed.Major) : ed.Degree;
                Add(section, ResumeDocument.Join(degree, ed.School), bold: true, after: 1);
                if (ed.Dates.Length > 0) Add(section, ed.Dates, 9, italic: true, after: 5);
            }
        }

        return doc;
    }

    /// <summary>Every visible line of a built document, in order — the parity check's input.</summary>
    public static List<string> ExtractText(Document document) {
        var lines = new List<string>();
        foreach (var sectionElement in document.Sections) {
            if (sectionElement is not Section section) continue;
            foreach (var element in section.Elements) {
                if (element is not Paragraph paragraph) continue;
                var text = Flatten(paragraph);
                if (text.Length > 0) lines.Add(text);
            }
        }
        return lines;
    }

    static string Flatten(Paragraph paragraph) {
        var text = "";
        foreach (var element in paragraph.Elements) {
            switch (element) {
                case Text t: text += t.Content; break;
                case FormattedText f: text += Flatten(f); break;
                case Character c: text += c.Char; break;
            }
        }
        return text.Trim();
    }

    static string Flatten(FormattedText formatted) {
        var text = "";
        foreach (var element in formatted.Elements) {
            switch (element) {
                case Text t: text += t.Content; break;
                case FormattedText f: text += Flatten(f); break;
                case Character c: text += c.Char; break;
            }
        }
        return text;
    }

    static void Heading(Section section, string text) {
        var p = section.AddParagraph();
        p.Format.SpaceBefore = Unit.FromPoint(6);
        p.Format.SpaceAfter = Unit.FromPoint(3);
        p.Format.Borders.Bottom.Width = 0.5;
        p.AddFormattedText(text, new Font { Bold = true, Size = Unit.FromPoint(11) });
    }

    /// <summary>Bullets mirror the DOCX: a bullet character with a hanging indent, not a list part.</summary>
    static void Bullet(Section section, string text) {
        var p = section.AddParagraph();
        p.Format.LeftIndent = Unit.FromCentimeter(0.6);
        p.Format.FirstLineIndent = Unit.FromCentimeter(-0.3);
        p.Format.SpaceAfter = Unit.FromPoint(2);
        p.AddText("• " + text);
    }

    static void Add(Section section, string text, double size = 10.5, bool bold = false, bool italic = false, double after = 3) {
        var p = section.AddParagraph();
        p.Format.SpaceAfter = Unit.FromPoint(after);
        p.AddFormattedText(text, new Font { Size = Unit.FromPoint(size), Bold = bold, Italic = italic });
    }
}

/// <summary>
/// Resolves the resume font from the installed Windows fonts. PDFsharp 6 has no font source of its
/// own, so this maps the four faces we use to real font files, with fallbacks — keeping PDF output
/// deterministic and free of any GDI/WPF-flavoured package variant.
/// </summary>
sealed class ResumeFontResolver : IFontResolver {
    static readonly string FontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    // Regular, bold, italic, bold-italic — first file that exists wins.
    static readonly string[][] Candidates = {
        new[] { "calibri.ttf", "segoeui.ttf", "arial.ttf" },
        new[] { "calibrib.ttf", "segoeuib.ttf", "arialbd.ttf" },
        new[] { "calibrii.ttf", "segoeuii.ttf", "ariali.ttf" },
        new[] { "calibriz.ttf", "segoeuiz.ttf", "arialbi.ttf" }
    };

    static int Index(bool bold, bool italic) => (bold ? 1 : 0) + (italic ? 2 : 0);

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic) =>
        new FontResolverInfo("resume#" + Index(isBold, isItalic));

    public byte[]? GetFont(string faceName) {
        var index = faceName.StartsWith("resume#", StringComparison.Ordinal)
            ? int.Parse(faceName.Substring("resume#".Length)) : 0;

        foreach (var file in Candidates[index]) {
            var path = Path.Combine(FontsDir, file);
            if (File.Exists(path)) return File.ReadAllBytes(path);
        }
        // Last resort: any regular face we can find, so a PDF is still produced.
        foreach (var file in Candidates[0]) {
            var path = Path.Combine(FontsDir, file);
            if (File.Exists(path)) return File.ReadAllBytes(path);
        }
        throw new FileNotFoundException("No usable system font was found for the PDF.");
    }
}
