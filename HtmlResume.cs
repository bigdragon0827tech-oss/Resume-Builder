using System.Globalization;
using System.Text;
#pragma warning disable CS0105 // The style-test project already imports System.IO globally.
using System.IO;
#pragma warning restore CS0105
using System.Xml.Linq;

namespace ResumeBuilder;

/// <summary>
/// Parallel DOCX → HTML → DOCX prototype. Production tailoring does not call this.
/// </summary>
public static class HtmlRoundTrip {
    public static string PrototypeDirectory => Path.Combine(Storage.DataDir, "HtmlPrototype");

    public static HtmlRoundTripResult ConvertFile(string sourceDocx, string? directory = null) {
        directory ??= PrototypeDirectory;
        Directory.CreateDirectory(directory);
        var stem = Path.GetFileNameWithoutExtension(sourceDocx);
        var htmlPath = Path.Combine(directory, stem + ".html");
        var docxPath = Path.Combine(directory, stem + ".roundtrip.docx");

        var toHtml = System.Diagnostics.Stopwatch.StartNew();
        var read = DocxHtmlReader.Read(sourceDocx);
        var html = HtmlResumeHtml.Emit(read);
        html = HtmlResumeHtml.Normalize(html);
        toHtml.Stop();
        File.WriteAllText(htmlPath, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var toDocx = System.Diagnostics.Stopwatch.StartNew();
        HtmlDocxWriter.Write(html, docxPath);
        toDocx.Stop();

        var types = read.Unsupported.Distinct(StringComparer.Ordinal).ToList();
        PerfLog.Line("HTML ROUNDTRIP source=" + Path.GetFileName(sourceDocx));
        PerfLog.Line("HTML ROUNDTRIP docx-to-html-ms=" + toHtml.ElapsedMilliseconds);
        PerfLog.Line("HTML ROUNDTRIP html-bytes=" + new FileInfo(htmlPath).Length);
        PerfLog.Line("HTML ROUNDTRIP html-to-docx-ms=" + toDocx.ElapsedMilliseconds);
        PerfLog.Line("HTML ROUNDTRIP output=" + docxPath);
        PerfLog.Line("HTML ROUNDTRIP unsupported=" + read.Unsupported.Count +
                     (types.Count == 0 ? "" : " " + string.Join(",", types)));

        return new HtmlRoundTripResult {
            SourcePath = sourceDocx,
            HtmlPath = htmlPath,
            DocxPath = docxPath,
            DocxToHtmlMs = toHtml.ElapsedMilliseconds,
            HtmlToDocxMs = toDocx.ElapsedMilliseconds,
            HtmlBytes = (int)new FileInfo(htmlPath).Length,
            Unsupported = read.Unsupported
        };
    }

    /// <summary>Visible characters in body, headers and footers, in that order. Fields contribute their result text only.</summary>
    public static string VisibleText(string docxPath) {
        using var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(docxPath, false);
        var main = doc.MainDocumentPart ?? throw new InvalidDataException("The DOCX has no document.");
        var sb = new StringBuilder();
        AppendPart(sb, main.Document?.Body);
        foreach (var header in main.HeaderParts) AppendPart(sb, header.Header);
        foreach (var footer in main.FooterParts) AppendPart(sb, footer.Footer);
        return sb.ToString().TrimEnd('\n');
    }

    static void AppendPart(StringBuilder sb, DocumentFormat.OpenXml.OpenXmlElement? root) {
        if (root is null) return;
        foreach (var paragraph in root.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>()) {
            foreach (var child in paragraph.Descendants()) {
                switch (child) {
                    case DocumentFormat.OpenXml.Wordprocessing.Text text:
                        sb.Append(text.Text);
                        break;
                    case DocumentFormat.OpenXml.Wordprocessing.TabChar:
                        sb.Append('\t');
                        break;
                    case DocumentFormat.OpenXml.Wordprocessing.Break line:
                        var kind = line.Type?.Value;
                        if (kind != DocumentFormat.OpenXml.Wordprocessing.BreakValues.Page
                            && kind != DocumentFormat.OpenXml.Wordprocessing.BreakValues.Column)
                            sb.Append('\n');
                        break;
                }
            }
            sb.Append('\n');
        }
    }
}

public sealed class HtmlRoundTripResult {
    public string SourcePath { get; init; } = "";
    public string HtmlPath { get; init; } = "";
    public string DocxPath { get; init; } = "";
    public long DocxToHtmlMs { get; init; }
    public long HtmlToDocxMs { get; init; }
    public int HtmlBytes { get; init; }
    public IReadOnlyList<string> Unsupported { get; init; } = Array.Empty<string>();
}

sealed class HtmlBlock {
    public string Tag = "p";
    public Dictionary<string, string> Style = new(StringComparer.Ordinal);
    public string? SourceStyle;
    public string? Marker;
    public List<HtmlInline> Inlines = new();
    public List<HtmlBlock> Children = new();
}

sealed class HtmlInline {
    public string Kind = "text";
    public string Text = "";
    public string? Href;
    public bool PageField;
    public Dictionary<string, string> Style = new(StringComparer.Ordinal);
}

sealed class HtmlResumeDocument {
    public Dictionary<string, string> Page = new(StringComparer.Ordinal);
    public Dictionary<string, string> Body = new(StringComparer.Ordinal);
    public List<HtmlBlock> Header = new();
    public List<HtmlBlock> Footer = new();
    public List<HtmlBlock> BodyBlocks = new();
    public List<string> Unsupported = new();
}

static class HtmlResumeHtml {
    public static string Emit(HtmlResumeDocument doc) {
        var classes = new Dictionary<string, string>(StringComparer.Ordinal);
        string ClassFor(Dictionary<string, string> style) {
            if (style.Count == 0) return "";
            var key = string.Join(";", style.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + ":" + p.Value));
            if (!classes.TryGetValue(key, out var name)) {
                name = "c" + (classes.Count + 1).ToString(CultureInfo.InvariantCulture);
                classes[key] = name;
            }
            return name;
        }

        var sb = new StringBuilder();
        var body = new StringBuilder();
        void Blocks(List<HtmlBlock> blocks, int indent) {
            foreach (var block in blocks) Block(body, block, indent, ClassFor);
        }
        if (doc.Header.Count > 0) {
            body.Append("<section class=\"header\">\n");
            Blocks(doc.Header, 1);
            body.Append("</section>\n");
        }
        Blocks(doc.BodyBlocks, 0);
        if (doc.Footer.Count > 0) {
            body.Append("<section class=\"footer\">\n");
            Blocks(doc.Footer, 1);
            body.Append("</section>\n");
        }

        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<html>\n<head>\n<style>\n");
        if (doc.Page.Count > 0) sb.Append("@page { ").Append(Declarations(doc.Page)).Append(" }\n");
        if (doc.Body.Count > 0) sb.Append("body { ").Append(Declarations(doc.Body)).Append(" }\n");
        foreach (var pair in classes.OrderBy(p => int.Parse(p.Value[1..], CultureInfo.InvariantCulture)))
            sb.Append('.').Append(pair.Value).Append(" { ").Append(pair.Key.Replace(";", "; ")).Append(" }\n");
        sb.Append("</style>\n</head>\n<body>\n");
        sb.Append(body);
        sb.Append("</body>\n</html>\n");
        return sb.ToString();
    }

    public static string Normalize(string html) {
        var parsed = XDocument.Parse(html, LoadOptions.PreserveWhitespace);
        var style = parsed.Descendants("style").FirstOrDefault()?.Value ?? "";
        var rules = ParseRules(style);
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<html>\n<head>\n<style>\n");
        foreach (var rule in rules.OrderBy(r => r.Key, StringComparer.Ordinal))
            sb.Append(rule.Key).Append(" { ").Append(Declarations(rule.Value)).Append(" }\n");
        sb.Append("</style>\n</head>\n<body>\n");
        var body = parsed.Root?.Element("body") ?? throw new InvalidDataException("Normalized HTML has no body.");
        foreach (var node in body.Elements()) WriteElement(sb, node, 0);
        sb.Append("</body>\n</html>\n");
        return sb.ToString();
    }

    public static Dictionary<string, Dictionary<string, string>> ParseRules(string css) {
        var rules = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var text = css.Replace("\r", "");
        var i = 0;
        while (i < text.Length) {
            var open = text.IndexOf('{', i);
            if (open < 0) break;
            var close = text.IndexOf('}', open);
            if (close < 0) break;
            var selector = text[i..open].Trim();
            var body = text[(open + 1)..close];
            if (selector.Length > 0) rules[selector] = ParseDeclarations(body);
            i = close + 1;
        }
        return rules;
    }

    public static Dictionary<string, string> ParseDeclarations(string body) {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in body.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            var colon = part.IndexOf(':');
            if (colon <= 0) continue;
            map[part[..colon].Trim()] = part[(colon + 1)..].Trim();
        }
        return map;
    }

    static string Declarations(Dictionary<string, string> style) =>
        string.Join("; ", style.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + ": " + p.Value)) +
        (style.Count == 0 ? "" : ";");

    static void Block(StringBuilder sb, HtmlBlock block, int indent, Func<Dictionary<string, string>, string> classFor) {
        var pad = new string(' ', indent * 2);
        var cls = Classes(classFor(block.Style), block.SourceStyle, block.Marker);
        if (block.Tag is "ul" or "ol" or "table" or "tr" or "td" or "section") {
            sb.Append(pad).Append('<').Append(block.Tag).Append(cls).Append(">\n");
            foreach (var child in block.Children) Block(sb, child, indent + 1, classFor);
            sb.Append(pad).Append("</").Append(block.Tag).Append(">\n");
            return;
        }
        sb.Append(pad).Append('<').Append(block.Tag).Append(cls).Append('>');
        foreach (var inline in Merge(block.Inlines)) Inline(sb, inline, classFor);
        sb.Append("</").Append(block.Tag).Append(">\n");
    }

    static List<HtmlInline> Merge(List<HtmlInline> inlines) {
        var merged = new List<HtmlInline>();
        foreach (var inline in inlines) {
            if (merged.Count > 0 && inline.Kind == "text" && merged[^1].Kind == "text"
                && inline.Href is null && merged[^1].Href is null && !inline.PageField && !merged[^1].PageField
                && Same(inline.Style, merged[^1].Style)) {
                merged[^1].Text += inline.Text;
                continue;
            }
            merged.Add(new HtmlInline {
                Kind = inline.Kind, Text = inline.Text, Href = inline.Href, PageField = inline.PageField,
                Style = new Dictionary<string, string>(inline.Style, StringComparer.Ordinal)
            });
        }
        return merged;
    }

    static bool Same(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var v) && v == p.Value);

    static void Inline(StringBuilder sb, HtmlInline inline, Func<Dictionary<string, string>, string> classFor) {
        if (inline.Kind == "br") { sb.Append("<br/>"); return; }
        if (inline.Kind == "tab") { sb.Append("<span data-tab=\"1\"></span>"); return; }
        if (inline.Kind == "shy") { sb.Append("<span data-shy=\"1\"></span>"); return; }
        if (inline.Kind == "nbhyphen") { sb.Append("<span data-hyphen=\"1\"></span>"); return; }
        var bold = inline.Style.TryGetValue("font-weight", out var weight) && weight == "700";
        var italic = inline.Style.TryGetValue("font-style", out var slant) && slant == "italic";
        var cls = classFor(inline.Style.Where(p => p.Key is not "font-weight" and not "font-style")
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));
        var attrs = new StringBuilder();
        if (cls.Length > 0) attrs.Append(" class=\"").Append(cls).Append('"');
        if (inline.Href is not null) attrs.Append(" data-href=\"").Append(Escape(inline.Href)).Append('"');
        if (inline.PageField) attrs.Append(" data-page=\"1\"");
        var text = Escape(inline.Text);
        if (bold && italic) sb.Append("<strong><em").Append(attrs).Append('>').Append(text).Append("</em></strong>");
        else if (bold) sb.Append("<strong").Append(attrs).Append('>').Append(text).Append("</strong>");
        else if (italic) sb.Append("<em").Append(attrs).Append('>').Append(text).Append("</em>");
        else sb.Append("<span").Append(attrs).Append('>').Append(text).Append("</span>");
    }

    static string Classes(string format, string? source, string? marker) {
        var names = new List<string>();
        if (!string.IsNullOrEmpty(format)) names.Add(format);
        if (!string.IsNullOrEmpty(source)) names.Add(source);
        var attrs = names.Count == 0 ? "" : " class=\"" + string.Join(' ', names) + "\"";
        if (!string.IsNullOrEmpty(marker)) attrs += " data-marker=\"" + Escape(marker) + "\"";
        return attrs;
    }

    static void WriteElement(StringBuilder sb, XElement element, int indent) {
        var pad = new string(' ', indent * 2);
        var name = element.Name.LocalName;
        if (name is "ul" or "ol" or "table" or "tr" or "td" or "section") {
            sb.Append(pad).Append('<').Append(name).Append(Attributes(element)).Append(">\n");
            foreach (var child in element.Elements()) WriteElement(sb, child, indent + 1);
            sb.Append(pad).Append("</").Append(name).Append(">\n");
            return;
        }
        sb.Append(pad).Append('<').Append(name).Append(Attributes(element)).Append('>');
        foreach (var node in element.Nodes()) {
            if (node is XElement child) WriteElementInline(sb, child);
            else if (node is XText text && text.Value.Length > 0) sb.Append(Escape(text.Value));
        }
        sb.Append("</").Append(name).Append(">\n");
    }

    static void WriteElementInline(StringBuilder sb, XElement element) {
        if (element.Name.LocalName == "br") { sb.Append("<br/>"); return; }
        sb.Append('<').Append(element.Name.LocalName).Append(Attributes(element)).Append('>');
        foreach (var node in element.Nodes()) {
            if (node is XText text) sb.Append(Escape(text.Value));
            else if (node is XElement child) WriteElementInline(sb, child);
        }
        sb.Append("</").Append(element.Name.LocalName).Append('>');
    }

    static string Attributes(XElement element) =>
        string.Concat(element.Attributes().OrderBy(a => a.Name.LocalName, StringComparer.Ordinal)
            .Select(a => " " + a.Name.LocalName + "=\"" + Escape(a.Value) + "\""));

    public static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    public static string Pt(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture) + "pt";

    public static bool TryPt(string? text, out double pt) {
        pt = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (text.EndsWith("pt", StringComparison.OrdinalIgnoreCase)) text = text[..^2];
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out pt);
    }
}
