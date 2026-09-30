using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;
using PdfSharp.Fonts;
using Color = MigraDoc.DocumentObjectModel.Color;
using Font = MigraDoc.DocumentObjectModel.Font;

namespace ResumeBuilder;

/// <summary>
/// The generated PDF is Word's export of the tailored DOCX, so the PDF matches that file.
/// The MigraDoc builder below is kept for the existing content-parity checks and is not the PDF
/// written for a job.
/// </summary>
public static class PdfWriter {
    const int PdfFormat = 17;

    static PdfWriter() {
        // PDFsharp resolves fonts itself; point it at the installed Windows fonts once.
        GlobalFontSettings.FontResolver ??= new ResumeFontResolver();
    }

    /// <summary>Exports one already-written DOCX to PDF. Does not rebuild the resume.</summary>
    public static void ConvertDocx(string docxPath, string pdfPath) {
        if (string.IsNullOrWhiteSpace(docxPath) || !File.Exists(docxPath))
            throw new PdfConvertException("missing-docx", "The tailored DOCX was not created, so the PDF was not converted.");
        if (Type.GetTypeFromProgID("Word.Application") is null)
            throw new PdfConvertException("word-missing", "Microsoft Word is not available to convert the tailored DOCX to PDF.");

        var session = new WordSession();
        Exception? error = null;
        var thread = new Thread(() => {
            try { ExportWithWord(docxPath, pdfPath, session); }
            catch (PdfConvertException ex) { error = ex; }
            catch (Exception ex) { error = new PdfConvertException(FailureReason(ex), "The tailored DOCX could not be converted to PDF."); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(90))) {
            int pid;
            bool owned;
            lock (session) {
                pid = session.Pid;
                owned = session.Owned;
            }
            if (owned && pid > 0) {
                try { Process.GetProcessById(pid).Kill(entireProcessTree: true); } catch { /* already gone */ }
            }
            throw new PdfConvertException("word-timeout", "Word did not finish converting the DOCX to PDF.");
        }
        if (error is not null) throw error;
    }

    static void ExportWithWord(string docxPath, string pdfPath, WordSession session) {
        var before = WinwordPids();
        object? application = null;
        object? documents = null;
        object? document = null;
        var ownsApplication = false;
        var ownedPid = 0;
        var export = Path.Combine(Path.GetDirectoryName(pdfPath) ?? Path.GetTempPath(), Path.GetRandomFileName() + ".pdf");
        try {
            application = Activator.CreateInstance(Type.GetTypeFromProgID("Word.Application")!)!;
            ownedPid = OwnedProcessId(application, before, out ownsApplication);
            RememberOwned(session, ownsApplication, ownedPid);
            PerfLog.Line("PDF WORD start pid=" + ownedPid.ToString(CultureInfo.InvariantCulture));
            if (ownsApplication) {
                ComSet(application, "Visible", false);
                ComSet(application, "DisplayAlerts", 0);
            }

            documents = ComGet(application, "Documents");
            document = ComCall(documents!, "Open", new object[] { docxPath, false, true, false });
            ComCall(document!, "ExportAsFixedFormat", new object[] { export, PdfFormat, false, 0 });
            if (ownedPid == 0 && ownsApplication) {
                var started = StartedPids(before);
                if (started.Count == 1) {
                    ownedPid = started[0];
                    RememberOwned(session, true, ownedPid);
                }
            }
            if (!File.Exists(export) || new FileInfo(export).Length == 0)
                throw new PdfConvertException("empty-pdf", "Word did not produce a PDF.");
            if (File.Exists(pdfPath)) File.Delete(pdfPath);
            File.Move(export, pdfPath);
        } finally {
            if (document is not null) {
                try { ComCall(document, "Close", new object[] { 0, Type.Missing, Type.Missing }); }
                catch { /* already closed */ }
                PerfLog.Line("PDF WORD document-close");
            }
            ReleaseCom(document);
            document = null;
            ReleaseCom(documents);
            documents = null;
            if (application is not null && ownsApplication) {
                try { ComCall(application, "Quit", new object[] { 0, Type.Missing, Type.Missing }); }
                catch { /* already gone */ }
                PerfLog.Line("PDF WORD quit");
            }
            ReleaseCom(application);
            application = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            foreach (var pid in StartedPids(before)) WaitForWinwordExit(pid);
            PerfLog.Line("PDF WORD cleanup-complete");
            try { if (File.Exists(export)) File.Delete(export); } catch { /* best effort */ }
        }
    }

    static void RememberOwned(WordSession session, bool ownsApplication, int ownedPid) {
        lock (session) {
            session.Owned = ownsApplication && ownedPid > 0;
            session.Pid = ownedPid;
        }
    }

    /// <summary>
    /// The process this conversion started. A WINWORD that was already running belongs to the user
    /// and is never quit or killed. An automation instance often has no window yet, so a single new
    /// process id is enough to claim it.
    /// </summary>
    static int OwnedProcessId(object word, HashSet<int> before, out bool ownsApplication) {
        var deadline = Environment.TickCount64 + 2000;
        while (true) {
            var comPid = WordProcessId(word);
            if (comPid > 0 && before.Contains(comPid)) {
                ownsApplication = false;
                return 0;
            }
            if (comPid > 0) {
                ownsApplication = true;
                return comPid;
            }
            var started = StartedPids(before);
            if (started.Count == 1) {
                ownsApplication = true;
                return started[0];
            }
            if (Environment.TickCount64 >= deadline) break;
            Thread.Sleep(40);
        }
        // No window handle. Quit only when this call could not have attached to Word the user already had open.
        ownsApplication = before.Count == 0;
        var created = StartedPids(before);
        return created.Count == 1 ? created[0] : 0;
    }

    static List<int> StartedPids(HashSet<int> before) =>
        WinwordPids().Where(id => !before.Contains(id)).ToList();

    static object? ComGet(object target, string name) =>
        target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);

    static void ComSet(object target, string name, object value) =>
        target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, new[] { value });

    static object? ComCall(object target, string name, object[] args) =>
        target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args);

    static HashSet<int> WinwordPids() {
        var ids = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName("WINWORD")) {
            try { ids.Add(process.Id); }
            catch { /* exited while we were listing */ }
            finally { process.Dispose(); }
        }
        return ids;
    }

    static void ReleaseCom(object? com) {
        if (com is null) return;
        try { Marshal.FinalReleaseComObject(com); } catch { /* already released */ }
    }

    /// <summary>Word exits a moment after Quit. This waits only for the process this conversion started.</summary>
    static void WaitForWinwordExit(int pid) {
        var deadline = Environment.TickCount64 + 8000;
        while (Environment.TickCount64 < deadline) {
            if (!WinwordPids().Contains(pid)) return;
            Thread.Sleep(50);
        }
    }

    static string FailureReason(Exception ex) {
        if (ex is COMException com && com.HResult is unchecked((int)0x80010001) or unchecked((int)0x8001010A))
            return "word-busy";
        if (ex is COMException) return "word-export";
        return ex.GetType().Name;
    }

    static int WordProcessId(object word) {
        try {
            var hwnd = Convert.ToInt32(ComGet(word, "Hwnd"), CultureInfo.InvariantCulture);
            if (hwnd == 0) return 0;
            GetWindowThreadProcessId((IntPtr)hwnd, out uint pid);
            return (int)pid;
        } catch (Exception ex) when (ex is InvalidCastException or COMException or TargetInvocationException or FormatException or OverflowException) {
            return 0;
        }
    }

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    sealed class WordSession {
        public int Pid;
        public bool Owned;
    }

    public static void Write(ResumeDocument resume, string path) {
        var renderer = new PdfDocumentRenderer { Document = BuildDocument(resume) };
        renderer.RenderDocument();
        renderer.PdfDocument.Save(path);
    }

    /// <summary>The document model, exposed so content parity with the DOCX can be asserted.</summary>
    public static Document BuildDocument(ResumeDocument resume) {
        var style = resume.Style;

        var doc = new Document();
        doc.Info.Title = string.IsNullOrWhiteSpace(resume.Name) ? "Resume" : resume.Name + " — Resume";

        var normal = doc.Styles["Normal"];
        if (normal is null) throw new InvalidOperationException("MigraDoc default style is missing.");
        normal.Font.Name = style.Fonts.Family;
        normal.Font.Size = Unit.FromPoint(style.Body.FontSize);
        normal.Font.Color = Rgb(style.Colors.Body);
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(style.Body.SpaceAfter);

        var section = doc.AddSection();
        section.PageSetup.PageFormat = style.Page.Size.Equals("A4", StringComparison.OrdinalIgnoreCase) ? PageFormat.A4 : PageFormat.Letter;
        section.PageSetup.TopMargin = Unit.FromInch(style.Page.MarginTop);
        section.PageSetup.BottomMargin = Unit.FromInch(style.Page.MarginBottom);
        section.PageSetup.LeftMargin = Unit.FromInch(style.Page.MarginLeft);
        section.PageSetup.RightMargin = Unit.FromInch(style.Page.MarginRight);

        if (resume.Name.Length > 0) Add(section, ResumeDocument.Cased(resume.Name, style.Name), style.Name, style);
        if (resume.Title.Length > 0) Add(section, ResumeDocument.Cased(resume.Title, style.Headline), style.Headline, style);
        if (resume.Contact.Length > 0) Add(section, ResumeDocument.Cased(resume.Contact, style.Contact), style.Contact, style);

        if (resume.Summary.Length > 0) {
            Heading(section, resume.Heading(ResumeDocument.SummaryHeading), style);
            // Forced regular weight: the summary never carries inline emphasis.
            Add(section, resume.Summary, style.Body, style, bold: false);
        }

        if (resume.Skills.Count > 0) {
            Heading(section, resume.Heading(ResumeDocument.SkillsHeading), style);
            foreach (var skill in resume.Skills) {
                if (skill.Category.Length > 0) Add(section, ResumeDocument.Cased(skill.Category, style.SkillCategory), style.SkillCategory, style);
                // Skill values are always regular weight, whatever the style asked for.
                if (skill.Skills.Length > 0) Add(section, skill.Skills, style.SkillValues, style, bold: false);
            }
        }

        if (resume.Experience.Count > 0) {
            Heading(section, resume.Heading(ResumeDocument.ExperienceHeading), style);
            foreach (var job in resume.Experience) {
                if (job.Heading.Length > 0) Add(section, ResumeDocument.Cased(job.Heading, style.CompanyHeading), style.CompanyHeading, style);
                if (job.Subtitle.Length > 0) Add(section, job.Subtitle, style.Subtitle, style);
                if (job.Metadata.Length > 0) Metadata(section, job.Metadata, style);
                foreach (var line in job.Lines) Bullet(section, line, style);
            }
        }

        if (resume.Certifications.Count > 0) {
            Heading(section, resume.Heading(ResumeDocument.CertificationsHeading), style);
            foreach (var certification in resume.Certifications) Bullet(section, BulletLine.Plain(certification), style);
        }

        if (resume.Education.Count > 0) {
            Heading(section, resume.Heading(ResumeDocument.EducationHeading), style);
            foreach (var entry in resume.Education) {
                // Education is ordinary body text: no emphasis, whatever the style asked for.
                if (entry.Heading.Length > 0) Add(section, entry.Heading, style.Education, style, bold: false);
                if (entry.Dates.Length > 0) Add(section, entry.Dates, style.Education, style, bold: false);
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

    // ---------- reusable style application ----------

    static void Heading(Section section, string text, ResumeStyle style) {
        var paragraph = Paragraph(section, style.SectionHeading);
        if (style.SectionHeading.BottomBorder) {
            paragraph.Format.Borders.Bottom.Width = 0.5;
            paragraph.Format.Borders.Bottom.Color = Rgb(style.SectionHeading.Color);
        }
        paragraph.AddFormattedText(text, RunFont(style.SectionHeading, style, style.SectionHeading.Bold));
    }

    static void Metadata(Section section, string text, ResumeStyle style) =>
        Add(section, text, style.Metadata, style);

    /// <summary>Bullets mirror the DOCX: a bullet character with a hanging indent, not a list part.</summary>
    static void Bullet(Section section, BulletLine line, ResumeStyle style) {
        var paragraph = Paragraph(section, style.Bullet);
        paragraph.AddFormattedText(ResumeDocument.Bullet, RunFont(style.Bullet, style, bold: false));
        foreach (var segment in line.Segments)
            paragraph.AddFormattedText(segment.Text, RunFont(style.Bullet, style, segment.Bold));
    }

    static void Add(Section section, string text, TextStyle text_style, ResumeStyle style, bool? bold = null) {
        var paragraph = Paragraph(section, text_style);
        paragraph.AddFormattedText(text, RunFont(text_style, style, bold ?? text_style.Bold));
    }

    static Paragraph Paragraph(Section section, TextStyle text_style) {
        var paragraph = section.AddParagraph();
        ApplyParagraphStyle(paragraph, text_style);
        return paragraph;
    }

    static void ApplyParagraphStyle(Paragraph paragraph, TextStyle style) {
        paragraph.Format.SpaceBefore = Unit.FromPoint(style.SpaceBefore);
        paragraph.Format.SpaceAfter = Unit.FromPoint(style.SpaceAfter);
        paragraph.Format.LineSpacingRule = LineSpacingRule.Multiple;
        paragraph.Format.LineSpacing = style.LineSpacing;
        paragraph.Format.Alignment = Alignment(style.Alignment);
        paragraph.Format.KeepWithNext = style.KeepWithNext;
        if (style.LeftIndent > 0) paragraph.Format.LeftIndent = Unit.FromInch(style.LeftIndent);
        if (style.HangingIndent > 0) paragraph.Format.FirstLineIndent = Unit.FromInch(-style.HangingIndent);
    }

    static Font RunFont(TextStyle text_style, ResumeStyle style, bool bold) => new() {
        Name = style.Fonts.Family,
        Size = Unit.FromPoint(text_style.FontSize),
        Bold = bold,
        Italic = text_style.Italic,
        Color = Rgb(text_style.Color)
    };

    static ParagraphAlignment Alignment(string alignment) => alignment.ToLowerInvariant() switch {
        "center" => ParagraphAlignment.Center,
        "right" => ParagraphAlignment.Right,
        "justify" => ParagraphAlignment.Justify,
        _ => ParagraphAlignment.Left
    };

    static Color Rgb(string hex) {
        var (r, g, b) = ResumeStyle.Rgb(hex);
        return new Color(r, g, b);
    }
}

/// <summary>
/// Resolves the resume font from the installed Windows fonts. PDFsharp 6 has no font source of its
/// own, so this maps each allowed family to real font files, with fallbacks — keeping PDF output
/// deterministic and free of any GDI/WPF-flavoured package variant.
/// </summary>
sealed class ResumeFontResolver : IFontResolver {
    static readonly string FontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    // Regular, bold, italic, bold-italic for every family the style system allows.
    static readonly Dictionary<string, string[]> Families = new(StringComparer.OrdinalIgnoreCase) {
        ["Arial"] = new[] { "arial.ttf", "arialbd.ttf", "ariali.ttf", "arialbi.ttf" },
        ["Calibri"] = new[] { "calibri.ttf", "calibrib.ttf", "calibrii.ttf", "calibriz.ttf" },
        ["Cambria"] = new[] { "cambria.ttc", "cambriab.ttf", "cambriai.ttf", "cambriaz.ttf" },
        ["Garamond"] = new[] { "gara.ttf", "garabd.ttf", "garait.ttf", "garabd.ttf" },
        ["Georgia"] = new[] { "georgia.ttf", "georgiab.ttf", "georgiai.ttf", "georgiaz.ttf" },
        ["Helvetica"] = new[] { "arial.ttf", "arialbd.ttf", "ariali.ttf", "arialbi.ttf" },
        ["Tahoma"] = new[] { "tahoma.ttf", "tahomabd.ttf", "tahoma.ttf", "tahomabd.ttf" },
        ["Times New Roman"] = new[] { "times.ttf", "timesbd.ttf", "timesi.ttf", "timesbi.ttf" },
        ["Verdana"] = new[] { "verdana.ttf", "verdanab.ttf", "verdanai.ttf", "verdanaz.ttf" }
    };

    // Used when the requested family is not installed on this machine.
    static readonly string[][] Fallbacks = {
        new[] { "arial.ttf", "calibri.ttf", "segoeui.ttf" },
        new[] { "arialbd.ttf", "calibrib.ttf", "segoeuib.ttf" },
        new[] { "ariali.ttf", "calibrii.ttf", "segoeuii.ttf" },
        new[] { "arialbi.ttf", "calibriz.ttf", "segoeuiz.ttf" }
    };

    static int Index(bool bold, bool italic) => (bold ? 1 : 0) + (italic ? 2 : 0);

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic) {
        var family = Families.ContainsKey(familyName) ? familyName : "Arial";
        return new FontResolverInfo(family + "#" + Index(isBold, isItalic));
    }

    public byte[]? GetFont(string faceName) {
        var separator = faceName.LastIndexOf('#');
        var family = separator > 0 ? faceName.Substring(0, separator) : "Arial";
        var index = separator > 0 && int.TryParse(faceName.Substring(separator + 1), out var parsed) ? parsed : 0;

        if (Families.TryGetValue(family, out var files) && Read(files[index]) is byte[] face) return face;

        foreach (var candidate in Fallbacks[index])
            if (Read(candidate) is byte[] fallback) return fallback;

        // Last resort: any regular face we can find, so a PDF is still produced.
        foreach (var candidate in Fallbacks[0])
            if (Read(candidate) is byte[] regular) return regular;

        throw new FileNotFoundException("No usable system font was found for the PDF.");
    }

    static byte[]? Read(string file) {
        var path = Path.Combine(FontsDir, file);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }
}

/// <summary>A DOCX-to-PDF conversion that did not produce a file. <see cref="Reason"/> is the log token.</summary>
public sealed class PdfConvertException : Exception {
    public PdfConvertException(string reason, string message) : base(message) {
        Reason = reason;
    }

    public string Reason { get; }
}
