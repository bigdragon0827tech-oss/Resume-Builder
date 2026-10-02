using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
#pragma warning disable CS0105 // The style-test project already imports System.IO globally.
using System.IO;
#pragma warning restore CS0105
using System.Xml.Linq;

namespace ResumeBuilder;

/// <summary>
/// The only resume-tailoring path. Job Tasks and Email Tasks both call this.
/// A finished answer is checked only after capture — never while ChatGPT is still writing it.
/// </summary>
public static class HtmlTailor {
    public static string RootDirectory => ProfileContext.HtmlTailoringPath;

    /// <summary>Tests point this at a temporary folder. The app leaves it empty.</summary>
    public static string OriginalCacheRoot { get; set; } = "";

    static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    static readonly Regex Fence = new(@"```(?:html|HTML)?[^\n]*\r?\n([\s\S]*?)```", RegexOptions.Compiled);

    static readonly HashSet<string> Tags = new(StringComparer.Ordinal) {
        "html", "head", "style", "body", "section", "div", "p", "span", "h1", "h2", "h3",
        "strong", "em", "ul", "ol", "li", "br", "table", "tr", "td"
    };
    static readonly HashSet<string> Attributes = new(StringComparer.Ordinal) {
        "class", "data-href", "data-page", "data-tab", "data-marker", "data-shy", "data-hyphen"
    };
    static readonly HashSet<string> CssProperties = new(StringComparer.Ordinal) {
        "font-family", "font-size", "font-weight", "font-style", "text-decoration", "color",
        "background-color", "text-align", "margin-top", "margin-bottom", "margin-left", "margin-right",
        "text-indent", "line-height", "border-top", "border-bottom", "border-left", "border-right",
        "border-inside-h", "border-inside-v", "letter-spacing", "tab-stops", "width", "vertical-align",
        "page-break-before", "size", "margin", "header", "footer"
    };

    public static string JobDirectory(string jobId) => Path.Combine(RootDirectory, SafeId(jobId));

    /// <summary>
    /// Builds the ChatGPT request from the user's tailoring prompt, the job description, and the
    /// resume HTML. The source HTML file is kept under its own name.
    /// </summary>
    public static PreparedRequest Prepare(JobTask job, AppSettings settings) {
        var docx = SourceDocx(job, settings);
        var profile = ReadProfile(settings);
        var promptPath = PromptModes.IsNormal(settings.PromptMode) ? settings.NormalPrompt : settings.MasterPrompt;
        if (string.IsNullOrWhiteSpace(promptPath) || !File.Exists(promptPath))
            throw new InvalidOperationException(PromptModes.IsNormal(settings.PromptMode)
                ? "Configure an existing Normal Prompt text file in Settings, or switch Prompt Mode back to Resume."
                : "Configure an existing Master Prompt text file in Settings.");
        var promptText = TailoringText(promptPath);
        var html = NormalizedOriginal(docx);
        var directory = JobDirectory(job.JobId ?? "");
        Directory.CreateDirectory(directory);
        var sourcePath = Path.Combine(directory, "source.html");
        WriteTextAtomic(sourcePath, html);
        var bound = ResumeContent.Bind(html);
        WriteTextAtomic(Path.Combine(directory, "content-model.json"), bound.ModelJson());

        var text = promptText.TrimEnd()
            + "\n\n===== JOB =====\n"
            + "Company: " + (job.Company ?? "") + "\n"
            + "Title: " + (job.Title ?? "") + "\n"
            + "Link: " + (job.Link ?? "") + "\n\n"
            + "Job description:\n" + (job.Jd ?? "")
            + "\n\n" + ResumeContent.ModelMarker + "\n"
            + bound.ModelJson()
            + "\n\n===== JSON PATCH =====\n"
            + "Return only changed editable fields. Do not return HTML.\n"
            + PromptConversion.PatchContract;

        var prepared = new PreparedRequest {
            JobId = job.JobId ?? "",
            Company = job.Company ?? "",
            Title = job.Title ?? "",
            PromptMode = PromptModes.IsNormal(settings.PromptMode) ? PromptModes.Normal : PromptModes.Resume,
            HtmlTailoring = true,
            Text = text
        };
        RequestPreparation.Save(prepared);
        PerfLog.Line("HTML TAILOR id=" + prepared.JobId + " profile=" + ProfileContext.LogToken);
        PerfLog.Line("HTML source=" + docx);
        return prepared;
    }

    /// <summary>
    /// Checks a finished answer, puts locked facts back, and saves tailored.html.
    /// A failure leaves source.html and any earlier tailored.html where they are.
    /// </summary>
    public static HtmlTailorResult Accept(string captured, string jobId) {
        var directory = JobDirectory(jobId);
        var sourcePath = Path.Combine(directory, "source.html");
        if (!File.Exists(sourcePath))
            return HtmlTailorResult.Failed("The source HTML for this job is missing, so nothing was written.");
        var sourceBytes = File.ReadAllBytes(sourcePath);
        var tailoredPath = Path.Combine(directory, "tailored.html");
        var tailoredBytes = File.Exists(tailoredPath) ? File.ReadAllBytes(tailoredPath) : null;
        try {
            var sourceHtml = File.ReadAllText(sourcePath);
            var bound = ResumeContent.Bind(sourceHtml);
            WriteTextAtomic(Path.Combine(directory, "content-model.json"), bound.ModelJson());
            var applied = bound.Apply(captured);
            if (!applied.Ok) {
                LogReject("patch", applied.Error);
                SaveRejected(directory, captured);
                return HtmlTailorResult.Failed(applied.Error + " The existing resume was not changed.");
            }
            if (applied.Ignored.Count > 0)
                PerfLog.Line("HTML PATCH ignored-count=" + applied.Ignored.Count + " id=" + SafeId(jobId));
            if (!TryValidate(applied.Html, out var error)) {
                LogReject("validate", error);
                SaveRejected(directory, captured);
                return HtmlTailorResult.Failed(error + " The existing resume was not changed.");
            }
            WriteTextAtomic(tailoredPath, applied.Html);
            if (!File.ReadAllBytes(sourcePath).AsSpan().SequenceEqual(sourceBytes))
                throw new InvalidDataException("The source HTML was changed.");
            PerfLog.Line("HTML accepted id=" + SafeId(jobId));
            return HtmlTailorResult.Succeeded(applied.Html, tailoredPath);
        } catch (Exception ex) {
            SaveRejected(directory, captured);
            if (tailoredBytes is null) {
                if (File.Exists(tailoredPath)) File.Delete(tailoredPath);
            } else if (!File.Exists(tailoredPath) || !File.ReadAllBytes(tailoredPath).AsSpan().SequenceEqual(tailoredBytes)) {
                File.WriteAllBytes(tailoredPath, tailoredBytes);
            }
            if (File.Exists(sourcePath) && !File.ReadAllBytes(sourcePath).AsSpan().SequenceEqual(sourceBytes))
                File.WriteAllBytes(sourcePath, sourceBytes);
            var message = ex is InvalidDataException or InvalidOperationException
                ? ex.Message
                : "The HTML answer could not be checked.";
            PerfLog.Line("HTML TAILOR rejected " + SafeId(jobId) + " " + ex.GetType().Name);
            return HtmlTailorResult.Failed(message + " The existing resume was not changed.");
        }
    }

    public static bool TryExtract(string? captured, out string html, out string error) {
        html = "";
        error = "";
        if (string.IsNullOrWhiteSpace(captured)) {
            error = "The answer was empty.";
            return false;
        }
        var text = captured.Trim();
        var htmlFences = new List<string>();
        foreach (Match match in Fence.Matches(text)) {
            var body = match.Groups[1].Value.Trim();
            if (body.Contains("<html", StringComparison.OrdinalIgnoreCase)) htmlFences.Add(body);
        }
        if (htmlFences.Count > 1) {
            error = "More than one HTML block was returned.";
            return false;
        }
        if (htmlFences.Count == 1) text = htmlFences[0];
        var opens = CountOf(text, "<html");
        if (opens > 1) {
            error = "More than one HTML block was returned.";
            return false;
        }
        var start = text.IndexOf("<?xml", StringComparison.OrdinalIgnoreCase);
        var htmlAt = text.IndexOf("<html", StringComparison.OrdinalIgnoreCase);
        if (htmlAt < 0) {
            error = "The answer did not contain an HTML resume.";
            return false;
        }
        if (start < 0 || start > htmlAt) start = htmlAt;
        var end = text.LastIndexOf("</html>", StringComparison.OrdinalIgnoreCase);
        if (end < start) {
            error = "The HTML resume is incomplete.";
            return false;
        }
        html = text[start..(end + "</html>".Length)].Trim();
        return true;
    }

    public static bool TryValidate(string html, out string error) {
        error = "";
        XDocument doc;
        try {
            doc = XDocument.Parse(html, LoadOptions.PreserveWhitespace);
        } catch (System.Xml.XmlException) {
            error = "The HTML is not well formed.";
            return false;
        }
        if (doc.Root is null || doc.Root.Name.LocalName != "html" || doc.Root.Element("body") is null) {
            error = "The HTML resume is missing its document body.";
            return false;
        }
        if (doc.DescendantNodes().OfType<XComment>().Any()) {
            error = "The HTML contains an unsupported comment.";
            return false;
        }
        foreach (var element in doc.Descendants()) {
            var name = element.Name.LocalName;
            if (!Tags.Contains(name)) {
                error = "The HTML contains an unsupported tag.";
                return false;
            }
            foreach (var attribute in element.Attributes()) {
                if (!Attributes.Contains(attribute.Name.LocalName)) {
                    error = "The HTML contains an unsupported attribute.";
                    return false;
                }
                if (attribute.Value.Contains("javascript:", StringComparison.OrdinalIgnoreCase)) {
                    error = "The HTML contains a script.";
                    return false;
                }
            }
        }
        var style = doc.Descendants("style").ToList();
        if (style.Count != 1) {
            error = "The HTML must contain its CSS once.";
            return false;
        }
        var css = style[0].Value;
        if (css.Contains("url(", StringComparison.OrdinalIgnoreCase)
            || css.Contains("@import", StringComparison.OrdinalIgnoreCase)
            || css.Contains("expression(", StringComparison.OrdinalIgnoreCase)
            || css.Contains("javascript:", StringComparison.OrdinalIgnoreCase)) {
            error = "The HTML CSS points at an external resource.";
            return false;
        }
        Dictionary<string, Dictionary<string, string>> rules;
        try {
            rules = HtmlResumeHtml.ParseRules(css);
        } catch (Exception) {
            error = "The HTML CSS could not be read.";
            return false;
        }
        foreach (var rule in rules) {
            if (!IsAllowedSelector(rule.Key)) {
                error = "The HTML contains unsupported CSS.";
                return false;
            }
            foreach (var declaration in rule.Value) {
                if (!CssProperties.Contains(declaration.Key)) {
                    error = "The HTML contains unsupported CSS.";
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// Puts locked facts back onto a tailored HTML resume. Paragraph, bullet, and inline-run
    /// counts are allowed to differ. A missing locked fact is restored or rejected by name.
    /// </summary>
    public static string Restore(string sourceHtml, string tailoredHtml, HtmlLockedFacts facts) {
        var source = XDocument.Parse(sourceHtml, LoadOptions.PreserveWhitespace);
        var tailored = XDocument.Parse(tailoredHtml, LoadOptions.PreserveWhitespace);
        RequireChrome(source, tailored);
        RestoreStyle(source, tailored);
        RequireKnownClasses(source, tailored);
        var sourceBlocks = Blocks(source);
        var returnedBlocks = Blocks(tailored);
        RestoreName(sourceBlocks, returnedBlocks, facts);
        var sourceAll = AllText(source);
        foreach (var item in facts.Required.Where(item => HasPhrase(sourceAll, item.Phrase)))
            EnsurePhrase(sourceBlocks, returnedBlocks, item);
        foreach (var block in sourceBlocks.Where(block => block.Experience)) {
            foreach (Match match in DateRange.Matches(block.Text))
                EnsurePhrase(sourceBlocks, returnedBlocks, new LockedPhrase("experience", "locked-date", match.Value));
            var content = SplitPipes(block.Text).Where(part => !part.Separator).ToList();
            if (content.Count >= 3) {
                var company = content[0].Text.Trim();
                if (company.Length >= 2)
                    EnsurePhrase(sourceBlocks, returnedBlocks, new LockedPhrase("experience", "locked-company", company));
            }
            foreach (var part in content) {
                var segment = part.Text.Trim();
                if (segment.Contains(',') && segment.Length is > 2 and < 80)
                    EnsurePhrase(sourceBlocks, returnedBlocks, new LockedPhrase("experience", "locked-location", segment));
            }
        }
        return HtmlResumeHtml.Normalize(tailored.ToString(SaveOptions.DisableFormatting));
    }

    static void RequireChrome(XDocument source, XDocument tailored) {
        _ = source;
        if (tailored.Root?.Element("body") is null || string.IsNullOrWhiteSpace(AllText(tailored)))
            Fail("structure", "missing-body");
    }

    static void RestoreStyle(XDocument source, XDocument tailored) {
        var sourceStyle = source.Descendants("style").FirstOrDefault();
        if (sourceStyle is null) return;
        var returnedStyle = tailored.Descendants("style").FirstOrDefault();
        if (returnedStyle is null) {
            var head = tailored.Root?.Element("head");
            if (head is null) {
                head = new XElement("head");
                tailored.Root?.AddFirst(head);
            }
            head.Add(new XElement(sourceStyle));
            return;
        }
        returnedStyle.Value = sourceStyle.Value;
    }

    static void RequireKnownClasses(XDocument source, XDocument tailored) {
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in ClassTokens(source)) allowed.Add(token);
        foreach (var token in ClassTokens(tailored)) {
            if (!allowed.Contains(token)) Fail("style", "new-class");
        }
    }

    static IEnumerable<string> ClassTokens(XDocument doc) {
        foreach (var element in doc.Descendants()) {
            var value = element.Attribute("class")?.Value;
            if (string.IsNullOrWhiteSpace(value)) continue;
            foreach (var token in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                yield return token;
        }
    }

    static void RestoreName(List<TextBlock> source, List<TextBlock> returned, HtmlLockedFacts facts) {
        if (facts.Name.Length == 0) return;
        var sourceAll = Joined(source);
        var returnedAll = Joined(returned);
        if (!HasPhrase(sourceAll, facts.Name) || HasPhrase(returnedAll, facts.Name)) return;
        var target = returned.FirstOrDefault(block => !block.Bullet && !block.Heading && block.Text.Length > 0 && block.Text.Length < 80);
        if (target is null) return;
        SetText(target, facts.Name);
        target.Text = facts.Name;
    }

    static void EnsurePhrase(List<TextBlock> source, List<TextBlock> returned, LockedPhrase item) {
        if (HasPhrase(Joined(returned), item.Phrase)) return;
        var origin = source.Where(block => HasPhrase(block.Text, item.Phrase))
            .OrderBy(block => block.Bullet ? 1 : 0).ThenBy(block => block.Text.Length).FirstOrDefault();
        var target = origin is null ? null : BestMatch(origin, returned);
        if (origin is null || target is null) return;
        var updated = RestoreLine(origin.Text, target.Text, item.Phrase);
        if (updated is null || !HasPhrase(updated, item.Phrase)) return;
        SetText(target, updated);
        target.Text = updated.Trim();
    }

    static TextBlock? BestMatch(TextBlock source, List<TextBlock> returned) {
        TextBlock? best = null;
        var bestScore = 0;
        foreach (var candidate in returned) {
            if (candidate.Bullet != source.Bullet) continue;
            var score = 0;
            if (source.Experience && candidate.Experience) score += 5;
            if (source.Heading && candidate.Heading) score += 5;
            if (source.Text.Contains('|') && candidate.Text.Contains('|')) score += 2;
            foreach (var word in Regex.Split(source.Text, @"[^\p{L}\p{N}]+")) {
                if (word.Length < 5) continue;
                if (candidate.Text.Contains(word, StringComparison.OrdinalIgnoreCase)) score += 3;
            }
            foreach (Match match in DateRange.Matches(source.Text)) {
                var range = match.Value;
                if (candidate.Text.Contains(range, StringComparison.OrdinalIgnoreCase)) score += 8;
                var dash = range.LastIndexOfAny(new[] { '-', '–', '—' });
                if (dash >= 0) {
                    var end = range[(dash + 1)..].Trim();
                    if (end.Length >= 4 && candidate.Text.Contains(end, StringComparison.OrdinalIgnoreCase)) score += 6;
                }
            }
            if (score > bestScore) {
                best = candidate;
                bestScore = score;
            }
        }
        return bestScore >= 4 ? best : null;
    }

    public static HtmlLockedFacts FactsFromProfile(JsonElement profile) {
        if (profile.ValueKind == JsonValueKind.Object && profile.TryGetProperty("profile", out var nested))
            profile = nested;
        var facts = new HtmlLockedFacts();
        if (profile.ValueKind != JsonValueKind.Object) return facts;
        if (profile.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object) {
            facts.Name = Str(info, "name");
            facts.Add("locked-facts", "locked-name", facts.Name);
            facts.Add("locked-facts", "locked-contact", Str(info, "email"));
            facts.Add("locked-facts", "locked-contact", Str(info, "phone"));
            facts.Add("locked-facts", "locked-contact", Str(info, "linkedin"));
            facts.Add("experience", "locked-location", Str(info, "location"));
        }
        Each(profile, "experience", job => {
            facts.Add("experience", "locked-company", Str(job, "company"));
            AddRange(facts, "experience", "locked-date", Str(job, "startDate"), Str(job, "endDate"));
            facts.Add("experience", "locked-location", Str(job, "location"));
            facts.Add("experience", "locked-employment", Str(job, "employmentType"));
            facts.Add("experience", "locked-employment", Str(job, "workArrangement"));
        });
        Each(profile, "education", school => {
            facts.Add("education", "locked-education", Str(school, "degree"));
            facts.Add("education", "locked-education", Str(school, "major"));
            facts.Add("education", "locked-education", Str(school, "school"));
            AddRange(facts, "education", "locked-education", Str(school, "startDate"), Str(school, "endDate"));
        });
        Each(profile, "certifications", item => {
            facts.Add("certification", "locked-certification", Str(item, "name"));
            facts.Add("certification", "locked-certification", Str(item, "issuer"));
            facts.Add("certification", "locked-certification", Str(item, "date"));
        });
        return facts;
    }

    public static GenerationResult WriteDocuments(JobTask job, AppSettings settings, string html, string? folder = null) {
        var result = new GenerationResult { DocxRequested = settings.Docx, PdfRequested = settings.Pdf };
        if (!settings.Docx && !settings.Pdf) return result;
        if (string.IsNullOrWhiteSpace(folder) && string.IsNullOrWhiteSpace(settings.ResumeRootFolder)) {
            result.DocxError = "Choose a Resume Root folder in Settings.";
            return result;
        }
        var name = CandidateName(settings);
        var paths = string.IsNullOrWhiteSpace(folder)
            ? ResumeOutputManager.Resolve(
                settings.ResumeRootFolder, job.Company ?? "", job.Title ?? "", candidateName: name)
            : ResumeOutputManager.ResolveInFolder(folder, name);
        result.OutputFolder = paths.JobFolder;
        Directory.CreateDirectory(paths.JobFolder);
        try {
            if (settings.Docx) {
                WriteDocx(html, paths.DocxPath);
                result.DocxPath = paths.DocxPath;
                PerfLog.Line("HTML docx=" + paths.DocxPath);
            }
        } catch (Exception ex) {
            result.DocxError = ResumeGenerator.Explain(ex);
            PerfLog.Line("HTML TAILOR docx-failed " + ex.GetType().Name);
            return result;
        }
        if (settings.Pdf) {
            var docxForPdf = result.DocxPath;
            string? temporary = null;
            try {
                if (docxForPdf is null) {
                    temporary = Path.Combine(Path.GetTempPath(), "rb-html-" + Guid.NewGuid().ToString("N") + ".docx");
                    WriteDocx(html, temporary);
                    docxForPdf = temporary;
                }
                var pdfTemp = paths.PdfPath + ".tmp";
                if (File.Exists(pdfTemp)) File.Delete(pdfTemp);
                PdfWriter.ConvertDocx(docxForPdf, pdfTemp);
                if (File.Exists(paths.PdfPath))
                    throw new IOException("A resume with that name already exists.");
                File.Move(pdfTemp, paths.PdfPath);
                result.PdfPath = paths.PdfPath;
                PerfLog.Line("HTML pdf=" + paths.PdfPath);
            } catch (Exception ex) {
                result.PdfError = ex is PdfConvertException pdf ? pdf.Message : ResumeGenerator.Explain(ex);
                PerfLog.Line("HTML TAILOR pdf-failed " + ex.GetType().Name);
                var pdfTemp = paths.PdfPath + ".tmp";
                try { if (File.Exists(pdfTemp)) File.Delete(pdfTemp); } catch { /* the DOCX is already saved */ }
            } finally {
                if (temporary is not null) {
                    try { if (File.Exists(temporary)) File.Delete(temporary); } catch { /* the PDF was the requested file */ }
                }
            }
        }
        try {
            ResumeOutputManager.SaveMetadata(paths.MetadataPath, new ResumeOutputMetadata {
                JobId = job.JobId ?? "",
                Company = job.Company ?? "",
                Role = job.Title ?? "",
                JobUrl = job.Link ?? "",
                GeneratedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                DocxFile = result.DocxPath is null ? null : Path.GetFileName(result.DocxPath),
                PdfFile = result.PdfPath is null ? null : Path.GetFileName(result.PdfPath)
            });
            result.MetadataPath = paths.MetadataPath;
        } catch (Exception ex) {
            PerfLog.Line("OUTPUT metadata failed: " + ex.GetType().Name);
        }
        return result;
    }

    public static void WriteDocx(string html, string docxPath) {
        if (File.Exists(docxPath)) throw new IOException("A resume with that name already exists.");
        var directory = Path.GetDirectoryName(docxPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temp = docxPath + ".tmp";
        if (File.Exists(temp)) File.Delete(temp);
        try {
            HtmlDocxWriter.Write(html, temp);
            File.Move(temp, docxPath);
        } finally {
            if (File.Exists(temp)) {
                try { File.Delete(temp); } catch { /* the finished file is already in place */ }
            }
        }
    }

    public static void WriteSource(string jobId, string html) {
        var directory = JobDirectory(jobId);
        Directory.CreateDirectory(directory);
        WriteTextAtomic(Path.Combine(directory, "source.html"), html);
    }

    /// <summary>HTML tailoring reads the Original Resume only. A style reference is not a source.</summary>
    public static string OriginalResumeFile(AppSettings settings) {
        if (ResumeGenerator.ResumeStyleSource.Usable(settings.OriginalResume))
            return Path.GetFullPath(settings.OriginalResume);
        throw new InvalidOperationException("Choose an Original Resume before using HTML tailoring.");
    }

    /// <summary>
    /// Normalized HTML for the Original Resume. A new file, or a changed file, is read again.
    /// An unchanged file reuses the stored HTML.
    /// </summary>
    public static string NormalizedOriginal(string docxPath) {
        var full = Path.GetFullPath(docxPath);
        var hash = PromptConversion.HashFile(full);
        var root = string.IsNullOrWhiteSpace(OriginalCacheRoot) ? RootDirectory : OriginalCacheRoot;
        Directory.CreateDirectory(root);
        var htmlPath = Path.Combine(root, "original-resume.html");
        var metaPath = Path.Combine(root, "original-resume.json");
        if (File.Exists(htmlPath) && File.Exists(metaPath)) {
            try {
                var cached = JsonSerializer.Deserialize<OriginalResumeCache>(File.ReadAllText(metaPath));
                if (cached is not null
                    && string.Equals(cached.ResumePath, full, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(cached.Hash, hash, StringComparison.OrdinalIgnoreCase))
                    return File.ReadAllText(htmlPath);
            } catch (JsonException) { /* the resume is read again */ }
            catch (IOException) { /* the resume is read again */ }
        }
        var html = HtmlResumeHtml.Normalize(HtmlResumeHtml.Emit(DocxHtmlReader.Read(full)));
        WriteTextAtomic(htmlPath, html);
        var meta = JsonSerializer.Serialize(new OriginalResumeCache { ResumePath = full, Hash = hash });
        WriteTextAtomic(metaPath, meta);
        PerfLog.Line("HTML TAILOR original-html=" + htmlPath);
        return html;
    }

    /// <summary>Stores normalized HTML for the Original Resume. Callers use this when HTML tailoring is on.</summary>
    public static void RememberOriginal(AppSettings settings) {
        if (!ResumeGenerator.ResumeStyleSource.Usable(settings.OriginalResume)) return;
        NormalizedOriginal(settings.OriginalResume);
    }

    static string SourceDocx(JobTask job, AppSettings settings) {
        _ = job;
        return OriginalResumeFile(settings);
    }

    static string TailoringText(string promptPath) => PromptConversion.RequireText(promptPath);

    static JsonElement ReadProfile(AppSettings settings) {
        var profilePath = !string.IsNullOrWhiteSpace(settings.CandidateProfile) && File.Exists(settings.CandidateProfile)
            ? settings.CandidateProfile : CandidateProfileStore.CandidateProfilePath;
        if (!File.Exists(profilePath))
            throw new InvalidOperationException("The structured Candidate Profile has not been initialized yet. Create and save the baseline profile first.");
        using var doc = JsonDocument.Parse(File.ReadAllText(profilePath));
        var profile = doc.RootElement.Clone();
        if (profile.ValueKind == JsonValueKind.Object && profile.TryGetProperty("profile", out var nested))
            profile = nested.Clone();
        if (profile.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Candidate Profile JSON must contain a profile object (either at the root or under a top-level \"profile\" property).");
        return profile;
    }

    static HtmlLockedFacts LoadFacts() => FactsFromProfile(ReadProfile(Storage.LoadSettings()));

    static string CandidateName(AppSettings settings) {
        try { return FactsFromProfile(ReadProfile(settings)).Name; }
        catch (Exception) { return ""; }
    }

    static void AddRange(HtmlLockedFacts facts, string stage, string reason, string start, string end) {
        facts.Add(stage, reason, start);
        facts.Add(stage, reason, end);
        if (start.Length == 0 || end.Length == 0) return;
        facts.Add(stage, reason, start + " - " + end);
        facts.Add(stage, reason, start + " – " + end);
        facts.Add(stage, reason, start + " — " + end);
    }

    static void Each(JsonElement profile, string array, Action<JsonElement> item) {
        if (!profile.TryGetProperty(array, out var list) || list.ValueKind != JsonValueKind.Array) return;
        foreach (var entry in list.EnumerateArray())
            if (entry.ValueKind == JsonValueKind.Object) item(entry);
    }

    static string Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    /// <summary>Restores one locked phrase inside a line. Returns null when the line cannot be repaired.</summary>
    static string? RestoreLine(string source, string tailored, string phrase) {
        if (HasPhrase(tailored, phrase)) return tailored;
        var sourceParts = SplitPipes(source);
        var tailoredParts = SplitPipes(tailored);
        if (sourceParts.Count != tailoredParts.Count) return null;
        var repaired = false;
        for (var i = 0; i < sourceParts.Count; i++) {
            if (sourceParts[i].Separator || !HasPhrase(sourceParts[i].Text, phrase)) continue;
            if (HasPhrase(tailoredParts[i].Text, phrase)) continue;
            tailoredParts[i].Text = sourceParts[i].Text;
            repaired = true;
        }
        if (!repaired) return null;
        return string.Concat(tailoredParts.Select(part => part.Text));
    }

    static List<Piece> SplitPipes(string text) {
        var parts = Regex.Split(text, @"(\s*\|\s*)");
        return parts.Select(part => new Piece { Text = part, Separator = part.Contains('|') }).ToList();
    }

    static bool HasPhrase(string text, string phrase) {
        if (phrase.Length == 0) return false;
        var index = 0;
        while (index < text.Length) {
            var found = text.IndexOf(phrase, index, StringComparison.OrdinalIgnoreCase);
            if (found < 0) return false;
            var end = found + phrase.Length;
            var before = found == 0 || !char.IsLetterOrDigit(text[found - 1]);
            var after = end >= text.Length || !char.IsLetterOrDigit(text[end]);
            if (before && after) return true;
            index = found + 1;
        }
        return false;
    }

    static string AllText(XDocument doc) =>
        string.Concat(doc.Root?.DescendantNodes().OfType<XText>().Select(text => text.Value) ?? Array.Empty<string>());

    static readonly Regex DateRange = new(
        @"\b(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Sept|Oct|Nov|Dec)[a-z]*\.?\s+\d{4}\s*[-–—]\s*(?:Present|Current|(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Sept|Oct|Nov|Dec)[a-z]*\.?\s+\d{4})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static void LogShape(string sourceHtml, string returnedHtml) {
        PerfLog.Line("HTML TAILOR source-elements=" + ElementCount(sourceHtml));
        PerfLog.Line("HTML TAILOR returned-elements=" + ElementCount(returnedHtml));
        PerfLog.Line("HTML TAILOR source-experience-count=" + ExperienceCount(sourceHtml));
        PerfLog.Line("HTML TAILOR returned-experience-count=" + ExperienceCount(returnedHtml));
    }

    static void LogReject(string stage, string reason) {
        var clean = (reason ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (clean.Length > 160) clean = clean[..160];
        PerfLog.Line("HTML TAILOR reject-stage=" + stage + " reason=" + clean);
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    static void Fail(string stage, string reason) {
        LogReject(stage, reason);
        throw new InvalidDataException(reason);
    }

    static int ElementCount(string html) {
        try {
            return XDocument.Parse(html, LoadOptions.PreserveWhitespace).Root?.Element("body")?.Descendants().Count() ?? 0;
        } catch (System.Xml.XmlException) {
            return -1;
        }
    }

    static int ExperienceCount(string html) {
        try {
            return CountExperience(Blocks(XDocument.Parse(html, LoadOptions.PreserveWhitespace)));
        } catch (System.Xml.XmlException) {
            return -1;
        }
    }

    static int CountExperience(List<TextBlock> blocks) {
        var headings = blocks.Count(block => block.Heading && block.Text.Contains("EXPERIENCE", StringComparison.OrdinalIgnoreCase));
        return headings > 0 ? headings : blocks.Count(block => block.Experience);
    }

    static List<TextBlock> Blocks(XDocument doc) {
        var body = doc.Root?.Element("body");
        var list = new List<TextBlock>();
        if (body is null) return list;
        foreach (var element in body.Descendants()) {
            var name = element.Name.LocalName;
            if (name is not ("p" or "h1" or "h2" or "h3" or "li")) continue;
            if (element.Ancestors().Any(ancestor => ancestor.Name.LocalName is "p" or "h1" or "h2" or "h3" or "li" or "section"))
                continue;
            var text = string.Concat(element.DescendantNodes().OfType<XText>().Select(node => node.Value)).Trim();
            list.Add(new TextBlock {
                Element = element,
                Text = text,
                Bullet = text.StartsWith('•') || text.StartsWith("- ", StringComparison.Ordinal),
                Heading = IsSectionHeading(text),
                Experience = !text.StartsWith('•') && text.Contains('|') && DateRange.IsMatch(text)
            });
        }
        return list;
    }

    static bool IsSectionHeading(string text) {
        if (text.Length == 0 || text.Length > 90 || text.StartsWith('•') || text.Contains('|')) return false;
        var letters = text.Count(char.IsLetter);
        if (letters < 4) return false;
        if (text.Count(char.IsUpper) < letters * 0.6) return false;
        return SectionTitle.IsMatch(text);
    }

    static readonly Regex SectionTitle = new(
        @"\b(SUMMARY|SKILL|EXPERIENCE|EDUCATION|CERTIFICATION|PROJECT|PUBLICATION|AWARD|HONOR|LANGUAGE|INTEREST|VOLUNTEER|REFERENCE|OBJECTIVE|PROFILE|CONTACT|TRAINING|COURSE)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static void SetText(TextBlock block, string text) {
        var nodes = block.Element.DescendantNodes().OfType<XText>().ToList();
        if (nodes.Count == 0) {
            block.Element.Add(new XText(text));
            return;
        }
        nodes[0].Value = text;
        for (var i = 1; i < nodes.Count; i++) nodes[i].Value = "";
    }

    static string Joined(List<TextBlock> blocks) => string.Join("\n", blocks.Select(block => block.Text));

    static bool IsAllowedSelector(string selector) =>
        selector == "body" || selector == "@page" || Regex.IsMatch(selector, @"^\.[A-Za-z0-9_-]+$");

    static int CountOf(string text, string token) {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.OrdinalIgnoreCase)) >= 0) {
            count++;
            index += token.Length;
        }
        return count;
    }

    static void SaveRejected(string directory, string text) {
        try {
            Directory.CreateDirectory(directory);
            WriteTextAtomic(Path.Combine(directory, "tailored.rejected.html"), text ?? "");
        } catch (Exception ex) {
            PerfLog.Line("HTML TAILOR reject-save-failed " + ex.GetType().Name);
        }
    }

    static void WriteTextAtomic(string path, string text) {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temp = path + ".tmp";
        File.WriteAllText(temp, text, Utf8);
        File.Move(temp, path, overwrite: true);
    }

    static string SafeId(string? jobId) {
        if (string.IsNullOrWhiteSpace(jobId)) return "job";
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(jobId.Length);
        foreach (var character in jobId) builder.Append(invalid.Contains(character) ? '_' : character);
        var text = builder.ToString().Trim();
        return text.Length == 0 ? "job" : text;
    }

    sealed class TextBlock {
        public XElement Element = null!;
        public string Text = "";
        public bool Bullet;
        public bool Heading;
        public bool Experience;
    }

    sealed class Piece {
        public string Text = "";
        public bool Separator;
    }

    sealed class OriginalResumeCache {
        public string ResumePath { get; set; } = "";
        public string Hash { get; set; } = "";
    }
}

public sealed class HtmlLockedFacts {
    public string Name { get; set; } = "";
    public List<LockedPhrase> Required { get; } = new();

    public void Add(string stage, string reason, string? value) {
        var text = (value ?? "").Trim();
        if (text.Length < 2) return;
        if (Required.Exists(item => item.Phrase.Equals(text, StringComparison.OrdinalIgnoreCase))) return;
        Required.Add(new LockedPhrase(stage, reason, text));
    }
}

public sealed class LockedPhrase {
    public LockedPhrase(string stage, string reason, string phrase) {
        Stage = stage;
        Reason = reason;
        Phrase = phrase;
    }

    public string Stage { get; }
    public string Reason { get; }
    public string Phrase { get; }
}

public sealed class HtmlTailorResult {
    public bool Ok { get; init; }
    public string Html { get; init; } = "";
    public string Error { get; init; } = "";
    public string SavedHtmlPath { get; init; } = "";

    public static HtmlTailorResult Succeeded(string html, string path) =>
        new() { Ok = true, Html = html, SavedHtmlPath = path };

    public static HtmlTailorResult Failed(string error) =>
        new() { Ok = false, Error = error };
}
