using System.Globalization;
#pragma warning disable CS0105 // The style-test project already imports System.IO globally.
using System.IO;
#pragma warning restore CS0105
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace ResumeBuilder;

/// <summary>
/// Builds a new DOCX from normalized HTML. It does not open or copy the source package.
/// </summary>
static class HtmlDocxWriter {
    public static void Write(string html, string docxPath) {
        var parsed = XDocument.Parse(html, LoadOptions.PreserveWhitespace);
        var styleText = parsed.Descendants("style").FirstOrDefault()?.Value ?? "";
        var rules = HtmlResumeHtml.ParseRules(styleText);
        var body = parsed.Root?.Element("body") ?? throw new InvalidDataException("HTML has no body.");
        var header = body.Elements("section").FirstOrDefault(IsHeader);
        var footer = body.Elements("section").FirstOrDefault(IsFooter);
        var flow = body.Elements().Where(e => e != header && e != footer).ToList();

        var directory = Path.GetDirectoryName(docxPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        using var word = WordprocessingDocument.Create(docxPath, WordprocessingDocumentType.Document);
        var main = word.AddMainDocumentPart();
        var numbering = new Lists(main);
        numbering.Collect(flow);
        if (header is not null) numbering.Collect(new[] { header });
        if (footer is not null) numbering.Collect(new[] { footer });
        numbering.Save();
        AddStyles(main, rules);

        var documentBody = new W.Body();
        foreach (var element in flow) WriteBlock(element, documentBody, main, numbering, rules);
        var section = PageSetup(rules);
        if (header is not null) {
            var part = main.AddNewPart<HeaderPart>();
            var content = new W.Header();
            foreach (var element in header.Elements()) WriteBlock(element, content, part, numbering, rules);
            if (!content.Elements<W.Paragraph>().Any()) content.Append(new W.Paragraph());
            part.Header = content;
            section.InsertAt(new W.HeaderReference { Type = W.HeaderFooterValues.Default, Id = main.GetIdOfPart(part) }, 0);
        }
        if (footer is not null) {
            var part = main.AddNewPart<FooterPart>();
            var content = new W.Footer();
            foreach (var element in footer.Elements()) WriteBlock(element, content, part, numbering, rules);
            if (!content.Elements<W.Paragraph>().Any()) content.Append(new W.Paragraph());
            part.Footer = content;
            var afterHeader = section.Elements<W.HeaderReference>().FirstOrDefault();
            if (afterHeader is null) section.InsertAt(new W.FooterReference { Type = W.HeaderFooterValues.Default, Id = main.GetIdOfPart(part) }, 0);
            else section.InsertAfter(new W.FooterReference { Type = W.HeaderFooterValues.Default, Id = main.GetIdOfPart(part) }, afterHeader);
        }
        documentBody.Append(section);
        main.Document = new W.Document(documentBody);
        main.Document.Save();
    }

    static bool IsHeader(XElement element) => Classes(element).Contains("header");
    static bool IsFooter(XElement element) => Classes(element).Contains("footer");

    static void AddStyles(MainDocumentPart main, Dictionary<string, Dictionary<string, string>> rules) {
        rules.TryGetValue("body", out var body);
        var part = main.AddNewPart<StyleDefinitionsPart>();
        var styles = new W.Styles();
        var defaults = new W.RunPropertiesBaseStyle();
        if (FontOf(body) is string font)
            defaults.Append(new W.RunFonts { Ascii = font, HighAnsi = font, ComplexScript = font });
        if (body is not null && Hex(Value(body, "color")) is string color)
            defaults.Append(new W.Color { Val = color });
        if (body is not null && HtmlResumeHtml.TryPt(Value(body, "font-size"), out var size)) {
            var half = HalfPoints(size).ToString(CultureInfo.InvariantCulture);
            defaults.Append(new W.FontSize { Val = half });
            defaults.Append(new W.FontSizeComplexScript { Val = half });
        }
        styles.Append(new W.DocDefaults(new W.RunPropertiesDefault(defaults), new W.ParagraphPropertiesDefault()));
        styles.Append(ParagraphStyle("Normal", "Normal", null, isDefault: true, outline: null));
        styles.Append(ParagraphStyle("Heading1", "heading 1", "Normal", isDefault: false, outline: 0));
        styles.Append(ParagraphStyle("Heading2", "heading 2", "Normal", isDefault: false, outline: 1));
        styles.Append(ParagraphStyle("Heading3", "heading 3", "Normal", isDefault: false, outline: 2));
        part.Styles = styles;
        part.Styles.Save();
    }

    static W.Style ParagraphStyle(string id, string name, string? basedOn, bool isDefault, int? outline) {
        var style = new W.Style { Type = W.StyleValues.Paragraph, StyleId = id, Default = isDefault };
        style.Append(new W.StyleName { Val = name });
        if (basedOn is not null) style.Append(new W.BasedOn { Val = basedOn });
        style.Append(new W.PrimaryStyle());
        if (outline is int level)
            style.Append(new W.StyleParagraphProperties(new W.OutlineLevel { Val = level }));
        return style;
    }

    static W.SectionProperties PageSetup(Dictionary<string, Dictionary<string, string>> rules) {
        rules.TryGetValue("@page", out var page);
        double width = 612, height = 792;
        if (page is not null && Value(page, "size") is string size) {
            var parts = size.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && HtmlResumeHtml.TryPt(parts[0], out var w) && HtmlResumeHtml.TryPt(parts[1], out var h)) {
                width = w;
                height = h;
            }
        }
        var margins = page is null ? Array.Empty<string>() : (Value(page, "margin") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        double Side(int index, double fallback) =>
            index < margins.Length && HtmlResumeHtml.TryPt(margins[index], out var pt) ? pt : fallback;
        double top = Side(0, 72), right = margins.Length == 1 ? top : Side(1, 72);
        double bottom = margins.Length switch { 1 => top, 2 => top, _ => Side(2, 72) };
        double left = margins.Length switch { 1 => top, 2 => right, 3 => right, _ => Side(3, 72) };
        double header = page is not null && HtmlResumeHtml.TryPt(Value(page, "header"), out var headerPt) ? headerPt : 36;
        double footer = page is not null && HtmlResumeHtml.TryPt(Value(page, "footer"), out var footerPt) ? footerPt : 36;
        return new W.SectionProperties(
            new W.PageSize { Width = (uint)Twips(width), Height = (uint)Twips(height) },
            new W.PageMargin {
                Top = Twips(top),
                Right = (uint)Math.Max(0, Twips(right)),
                Bottom = Twips(bottom),
                Left = (uint)Math.Max(0, Twips(left)),
                Header = (uint)Math.Max(0, Twips(header)),
                Footer = (uint)Math.Max(0, Twips(footer)),
                Gutter = 0
            });
    }

    static void WriteBlock(XElement element, OpenXmlElement parent, OpenXmlPart part, Lists lists,
                            Dictionary<string, Dictionary<string, string>> rules) {
        switch (element.Name.LocalName) {
            case "ul":
            case "ol":
                foreach (var item in element.Elements())
                    parent.Append(Paragraph(item, element, part, lists, rules));
                break;
            case "table":
                parent.Append(Table(element, part, lists, rules));
                break;
            case "div":
            case "section":
                foreach (var child in element.Elements()) WriteBlock(child, parent, part, lists, rules);
                break;
            default:
                parent.Append(Paragraph(element, list: null, part, lists, rules));
                break;
        }
    }

    static W.Paragraph Paragraph(XElement element, XElement? list, OpenXmlPart part, Lists lists,
                                 Dictionary<string, Dictionary<string, string>> rules) {
        var css = StyleOf(element, rules);
        var paragraph = new W.Paragraph();
        var props = new W.ParagraphProperties();
        var tag = element.Name.LocalName;
        if (tag is "h1" or "h2" or "h3") {
            props.Append(new W.ParagraphStyleId { Val = tag switch { "h1" => "Heading1", "h2" => "Heading2", _ => "Heading3" } });
        }
        if (Value(css, "page-break-before") == "always") props.Append(new W.PageBreakBefore());
        if (list is not null) {
            var marker = list.Attribute("data-marker")?.Value ?? (list.Name.LocalName == "ol" ? "%1." : "•");
            props.Append(new W.NumberingProperties(
                new W.NumberingLevelReference { Val = 0 },
                new W.NumberingId { Val = lists.IdFor(list.Name.LocalName == "ol", marker) }));
        }
        var borders = new W.ParagraphBorders();
        var hasBorder = false;
        if (MakeBorder<W.TopBorder>(css, "border-top") is { } top) { borders.TopBorder = top; hasBorder = true; }
        if (MakeBorder<W.LeftBorder>(css, "border-left") is { } leftBorder) { borders.LeftBorder = leftBorder; hasBorder = true; }
        if (MakeBorder<W.BottomBorder>(css, "border-bottom") is { } bottom) { borders.BottomBorder = bottom; hasBorder = true; }
        if (MakeBorder<W.RightBorder>(css, "border-right") is { } rightBorder) { borders.RightBorder = rightBorder; hasBorder = true; }
        if (hasBorder) props.Append(borders);
        if (Hex(Value(css, "background-color")) is string fill)
            props.Append(new W.Shading { Val = W.ShadingPatternValues.Clear, Fill = fill });
        if (Value(css, "tab-stops") is string stops) {
            var tabs = new W.Tabs();
            foreach (var stop in stops.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
                var bits = stop.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (bits.Length == 0 || !int.TryParse(bits[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pos)) continue;
                var kind = bits.Length > 1 ? bits[1] : "left";
                tabs.Append(new W.TabStop {
                    Val = kind switch {
                        "right" => W.TabStopValues.Right,
                        "center" => W.TabStopValues.Center,
                        "decimal" => W.TabStopValues.Decimal,
                        _ => W.TabStopValues.Left
                    },
                    Position = pos
                });
            }
            if (tabs.Any()) props.Append(tabs);
        }
        var spacing = new W.SpacingBetweenLines();
        var hasSpacing = false;
        if (HtmlResumeHtml.TryPt(Value(css, "margin-top"), out var before)) { spacing.Before = Twips(before).ToString(CultureInfo.InvariantCulture); hasSpacing = true; }
        if (HtmlResumeHtml.TryPt(Value(css, "margin-bottom"), out var after)) { spacing.After = Twips(after).ToString(CultureInfo.InvariantCulture); hasSpacing = true; }
        if (Value(css, "line-height") is string line && LineSpacing(line) is { } lineSpacing) {
            spacing.Line = lineSpacing.Value;
            spacing.LineRule = lineSpacing.Rule;
            hasSpacing = true;
        }
        if (hasSpacing) props.Append(spacing);
        var indent = new W.Indentation();
        var hasIndent = false;
        if (HtmlResumeHtml.TryPt(Value(css, "margin-left"), out var left)) { indent.Left = Twips(left).ToString(CultureInfo.InvariantCulture); hasIndent = true; }
        if (HtmlResumeHtml.TryPt(Value(css, "margin-right"), out var right)) { indent.Right = Twips(right).ToString(CultureInfo.InvariantCulture); hasIndent = true; }
        if (HtmlResumeHtml.TryPt(Value(css, "text-indent"), out var textIndent)) {
            if (textIndent < 0) indent.Hanging = Twips(-textIndent).ToString(CultureInfo.InvariantCulture);
            else indent.FirstLine = Twips(textIndent).ToString(CultureInfo.InvariantCulture);
            hasIndent = true;
        }
        if (hasIndent) props.Append(indent);
        if (Value(css, "text-align") is string align) {
            var justification = align switch {
                "center" => W.JustificationValues.Center,
                "right" => W.JustificationValues.Right,
                "justify" => W.JustificationValues.Both,
                _ => W.JustificationValues.Left
            };
            props.Append(new W.Justification { Val = justification });
        }
        if (tag is "h1" or "h2" or "h3")
            props.Append(new W.OutlineLevel { Val = tag switch { "h1" => 0, "h2" => 1, _ => 2 } });
        if (props.Any()) paragraph.Append(props);
        WriteInlines(element, paragraph, part, new Look(), null);
        return paragraph;
    }

    static void WriteInlines(XElement element, W.Paragraph paragraph, OpenXmlPart part, Look inherited, string? href) {
        var look = inherited.Copy();
        Apply(look, element);
        var link = element.Attribute("data-href")?.Value ?? href;
        if (element.Attribute("data-tab") is not null && string.IsNullOrEmpty(element.Value)) {
            paragraph.Append(new W.Run(RunProps(look), new W.TabChar()));
            return;
        }
        if (element.Attribute("data-shy") is not null) {
            paragraph.Append(new W.Run(RunProps(look), new W.SoftHyphen()));
            return;
        }
        if (element.Attribute("data-hyphen") is not null) {
            paragraph.Append(new W.Run(RunProps(look), new W.NoBreakHyphen()));
            return;
        }
        var page = element.Attribute("data-page") is not null;
        if (!element.Elements().Any()) {
            EmitText(paragraph, part, element.Value, look, link, page);
            return;
        }
        foreach (var node in element.Nodes()) {
            if (node is XText text) {
                if (text.Value.Length == 0 || (string.IsNullOrWhiteSpace(text.Value) && text.Parent?.Name.LocalName is not "span" and not "strong" and not "em"))
                    continue;
                EmitText(paragraph, part, text.Value, look, link, page);
            } else if (node is XElement child && child.Name.LocalName == "br") {
                paragraph.Append(new W.Run(RunProps(look), new W.Break()));
            } else if (node is XElement nested) {
                WriteInlines(nested, paragraph, part, look, link);
            }
        }
    }

    static void EmitText(W.Paragraph paragraph, OpenXmlPart part, string text, Look look, string? href, bool page) {
        if (text.Length == 0 && !page) return;
        if (page) {
            paragraph.Append(new W.Run(RunProps(look), new W.FieldChar { FieldCharType = W.FieldCharValues.Begin }));
            paragraph.Append(new W.Run(RunProps(look), new W.FieldCode(" PAGE ") { Space = SpaceProcessingModeValues.Preserve }));
            paragraph.Append(new W.Run(RunProps(look), new W.FieldChar { FieldCharType = W.FieldCharValues.Separate }));
            paragraph.Append(new W.Run(RunProps(look), Text(text.Length == 0 ? "1" : text)));
            paragraph.Append(new W.Run(RunProps(look), new W.FieldChar { FieldCharType = W.FieldCharValues.End }));
            return;
        }
        var run = new W.Run(RunProps(look), Text(text));
        if (href is null || !TryUri(href, out var uri)) {
            paragraph.Append(run);
            return;
        }
        var rel = part.AddHyperlinkRelationship(uri, true);
        paragraph.Append(new W.Hyperlink(run) { Id = rel.Id, History = true });
    }

    static W.Text Text(string value) {
        var text = new W.Text(value);
        if (value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]) || value.Contains("  ")))
            text.Space = SpaceProcessingModeValues.Preserve;
        return text;
    }

    static bool TryUri(string href, out Uri uri) {
        try {
            uri = new Uri(href, UriKind.RelativeOrAbsolute);
            return true;
        } catch (UriFormatException) {
            uri = null!;
            return false;
        }
    }

    static W.Table Table(XElement element, OpenXmlPart part, Lists lists, Dictionary<string, Dictionary<string, string>> rules) {
        var css = StyleOf(element, rules);
        var rows = element.Elements("tr").ToList();
        var widths = rows.FirstOrDefault()?.Elements("td").Select(cell => {
            var cellCss = StyleOf(cell, rules);
            return HtmlResumeHtml.TryPt(Value(cellCss, "width"), out var pt) ? Twips(pt) : 0;
        }).ToList() ?? new List<int>();
        var tableWidth = HtmlResumeHtml.TryPt(Value(css, "width"), out var declared) ? Twips(declared) : widths.Sum();
        if (tableWidth <= 0) tableWidth = 9360;

        var table = new W.Table();
        var props = new W.TableProperties(
            new W.TableWidth { Width = tableWidth.ToString(CultureInfo.InvariantCulture), Type = W.TableWidthUnitValues.Dxa });
        var borders = new W.TableBorders();
        var hasBorder = false;
        if (MakeBorder<W.TopBorder>(css, "border-top") is { } top) { borders.TopBorder = top; hasBorder = true; }
        if (MakeBorder<W.LeftBorder>(css, "border-left") is { } leftBorder) { borders.LeftBorder = leftBorder; hasBorder = true; }
        if (MakeBorder<W.BottomBorder>(css, "border-bottom") is { } bottom) { borders.BottomBorder = bottom; hasBorder = true; }
        if (MakeBorder<W.RightBorder>(css, "border-right") is { } rightBorder) { borders.RightBorder = rightBorder; hasBorder = true; }
        if (MakeBorder<W.InsideHorizontalBorder>(css, "border-inside-h") is { } insideH) { borders.InsideHorizontalBorder = insideH; hasBorder = true; }
        if (MakeBorder<W.InsideVerticalBorder>(css, "border-inside-v") is { } insideV) { borders.InsideVerticalBorder = insideV; hasBorder = true; }
        if (hasBorder) props.Append(borders);
        if (Hex(Value(css, "background-color")) is string fill)
            props.Append(new W.Shading { Val = W.ShadingPatternValues.Clear, Fill = fill });
        props.Append(new W.TableLayout { Type = W.TableLayoutValues.Fixed });
        table.Append(props);
        var grid = new W.TableGrid();
        foreach (var width in widths.Count > 0 ? widths : new List<int> { tableWidth })
            grid.Append(new W.GridColumn { Width = (width > 0 ? width : tableWidth).ToString(CultureInfo.InvariantCulture) });
        table.Append(grid);

        foreach (var row in rows) {
            var tableRow = new W.TableRow();
            var index = 0;
            foreach (var cell in row.Elements("td")) {
                var cellCss = StyleOf(cell, rules);
                var cellProps = new W.TableCellProperties();
                var width = index < widths.Count && widths[index] > 0 ? widths[index] : 0;
                if (width > 0)
                    cellProps.Append(new W.TableCellWidth { Width = width.ToString(CultureInfo.InvariantCulture), Type = W.TableWidthUnitValues.Dxa });
                var cellBorders = new W.TableCellBorders();
                var cellHasBorder = false;
                if (MakeBorder<W.TopBorder>(cellCss, "border-top") is { } cellTop) { cellBorders.TopBorder = cellTop; cellHasBorder = true; }
                if (MakeBorder<W.LeftBorder>(cellCss, "border-left") is { } cellLeft) { cellBorders.LeftBorder = cellLeft; cellHasBorder = true; }
                if (MakeBorder<W.BottomBorder>(cellCss, "border-bottom") is { } cellBottom) { cellBorders.BottomBorder = cellBottom; cellHasBorder = true; }
                if (MakeBorder<W.RightBorder>(cellCss, "border-right") is { } cellRight) { cellBorders.RightBorder = cellRight; cellHasBorder = true; }
                if (cellHasBorder) cellProps.Append(cellBorders);
                if (Hex(Value(cellCss, "background-color")) is string cellFill)
                    cellProps.Append(new W.Shading { Val = W.ShadingPatternValues.Clear, Fill = cellFill });
                if (Value(cellCss, "vertical-align") is string valign)
                    cellProps.Append(new W.TableCellVerticalAlignment {
                        Val = valign == "middle" ? W.TableVerticalAlignmentValues.Center
                            : valign == "bottom" ? W.TableVerticalAlignmentValues.Bottom
                            : W.TableVerticalAlignmentValues.Top
                    });
                var tableCell = new W.TableCell();
                if (cellProps.Any()) tableCell.Append(cellProps);
                var wrote = false;
                foreach (var child in cell.Elements()) {
                    WriteBlock(child, tableCell, part, lists, rules);
                    wrote = true;
                }
                if (!wrote) tableCell.Append(new W.Paragraph());
                tableRow.Append(tableCell);
                index++;
            }
            table.Append(tableRow);
        }
        if (!table.Elements<W.TableRow>().Any())
            table.Append(new W.TableRow(new W.TableCell(new W.Paragraph())));
        return table;
    }

    static T? MakeBorder<T>(Dictionary<string, string> css, string property) where T : W.BorderType, new() {
        if (Value(css, property) is not string text) return null;
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !HtmlResumeHtml.TryPt(parts[0], out var width)) return null;
        var space = parts.Length >= 4 && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        return new T {
            Val = parts[1] == "double" ? W.BorderValues.Double : W.BorderValues.Single,
            Size = (uint)Math.Max(2, (int)Math.Round(width * 8, MidpointRounding.AwayFromZero)),
            Space = (uint)Math.Max(0, space),
            Color = Hex(parts[2]) ?? "000000"
        };
    }

    static Dictionary<string, string> StyleOf(XElement element, Dictionary<string, Dictionary<string, string>> rules) {
        var css = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in Classes(element)) {
            if (rules.TryGetValue("." + name, out var rule))
                foreach (var pair in rule) css[pair.Key] = pair.Value;
        }
        return css;
    }

    static List<string> Classes(XElement element) =>
        (element.Attribute("class")?.Value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

    static void Apply(Look look, XElement element) {
        var css = element.Document == null ? new Dictionary<string, string>() : StyleOf(element, RulesOf(element));
        if (element.Name.LocalName is "strong") look.Bold = true;
        if (element.Name.LocalName is "em") look.Italic = true;
        if (FontOf(css) is string font) look.Font = font;
        if (HtmlResumeHtml.TryPt(Value(css, "font-size"), out var size)) look.SizePt = size;
        if (Value(css, "font-weight") is "700" or "bold") look.Bold = true;
        if (Value(css, "font-style") is "italic") look.Italic = true;
        if (Value(css, "text-decoration")?.Contains("underline", StringComparison.OrdinalIgnoreCase) == true) look.Underline = true;
        if (Hex(Value(css, "color")) is string color) look.Color = color;
        if (Hex(Value(css, "background-color")) is string shading) look.Shading = shading;
        if (HtmlResumeHtml.TryPt(Value(css, "letter-spacing"), out var letter)) look.LetterPt = letter;
    }

    static Dictionary<string, Dictionary<string, string>> RulesOf(XElement element) {
        var style = element.Document?.Descendants("style").FirstOrDefault()?.Value ?? "";
        return HtmlResumeHtml.ParseRules(style);
    }

    static W.RunProperties RunProps(Look look) {
        var props = new W.RunProperties();
        if (look.Font is not null)
            props.Append(new W.RunFonts { Ascii = look.Font, HighAnsi = look.Font, ComplexScript = look.Font });
        props.Append(new W.Bold { Val = look.Bold });
        props.Append(new W.BoldComplexScript { Val = look.Bold });
        props.Append(new W.Italic { Val = look.Italic });
        props.Append(new W.ItalicComplexScript { Val = look.Italic });
        if (look.Color is not null) props.Append(new W.Color { Val = look.Color });
        if (look.LetterPt is double letter)
            props.Append(new W.Spacing { Val = Twips(letter) });
        if (look.SizePt is double size) {
            var half = HalfPoints(size).ToString(CultureInfo.InvariantCulture);
            props.Append(new W.FontSize { Val = half });
            props.Append(new W.FontSizeComplexScript { Val = half });
        }
        if (look.Underline) props.Append(new W.Underline { Val = W.UnderlineValues.Single });
        if (look.Shading is not null)
            props.Append(new W.Shading { Val = W.ShadingPatternValues.Clear, Fill = look.Shading });
        return props;
    }

    static string? FontOf(Dictionary<string, string>? css) {
        if (css is null || Value(css, "font-family") is not string family) return null;
        var first = family.Split(',')[0].Trim().Trim('"', '\'');
        return first.Length == 0 ? null : first;
    }

    static string? Hex(string? color) {
        if (string.IsNullOrWhiteSpace(color)) return null;
        color = color.Trim();
        if (color.StartsWith('#')) color = color[1..];
        return color.Length == 6 && color.All(Uri.IsHexDigit) ? color.ToUpperInvariant() : null;
    }

    static string? Value(Dictionary<string, string>? css, string name) =>
        css is not null && css.TryGetValue(name, out var value) ? value : null;

    /// <summary>
    /// Unitless line-height (1, 1.0, 1.15) is a multiplier. Word stores that as automatic
    /// line spacing in 240ths of a line. A point length is a minimum, never a tiny exact box.
    /// </summary>
    static (string Value, W.LineSpacingRuleValues Rule)? LineSpacing(string line) {
        var text = line.Trim();
        if (IsMultiplier(text)
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var multiple)
            && multiple > 0) {
            var value = ((int)Math.Round(multiple * 240, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);
            return (value, W.LineSpacingRuleValues.Auto);
        }
        if (text.EndsWith("pt", StringComparison.OrdinalIgnoreCase)
            && HtmlResumeHtml.TryPt(text, out var points) && points >= 8)
            return (Twips(points).ToString(CultureInfo.InvariantCulture), W.LineSpacingRuleValues.AtLeast);
        return null;
    }

    static bool IsMultiplier(string text) {
        if (text.Length == 0) return false;
        foreach (var character in text)
            if (char.IsLetter(character) || character == '%') return false;
        return true;
    }

    static int Twips(double points) => (int)Math.Round(points * 20, MidpointRounding.AwayFromZero);
    static int HalfPoints(double points) => (int)Math.Round(points * 2, MidpointRounding.AwayFromZero);

    sealed class Look {
        public string? Font;
        public double? SizePt;
        public bool Bold;
        public bool Italic;
        public bool Underline;
        public string? Color;
        public string? Shading;
        public double? LetterPt;
        public Look Copy() => (Look)MemberwiseClone();
    }

    sealed class Lists {
        readonly MainDocumentPart _main;
        readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
        readonly List<(int Id, bool Ordered, string Marker)> _defs = new();

        public Lists(MainDocumentPart main) => _main = main;

        public void Collect(IEnumerable<XElement> elements) {
            foreach (var element in elements) Collect(element);
        }

        void Collect(XElement element) {
            if (element.Name.LocalName is "ul" or "ol")
                IdFor(element.Name.LocalName == "ol", element.Attribute("data-marker")?.Value ?? (element.Name.LocalName == "ol" ? "%1." : "•"));
            foreach (var child in element.Elements()) Collect(child);
        }

        public int IdFor(bool ordered, string marker) {
            var key = (ordered ? "ol:" : "ul:") + marker;
            if (_ids.TryGetValue(key, out var id)) return id;
            id = _ids.Count + 1;
            _ids[key] = id;
            _defs.Add((id, ordered, marker));
            return id;
        }

        public void Save() {
            if (_defs.Count == 0) return;
            var part = _main.AddNewPart<NumberingDefinitionsPart>();
            var numbering = new W.Numbering();
            foreach (var def in _defs) {
                numbering.Append(new W.AbstractNum(
                    new W.Nsid { Val = def.Id.ToString("X8", CultureInfo.InvariantCulture) },
                    new W.MultiLevelType { Val = W.MultiLevelValues.HybridMultilevel },
                    new W.Level(
                        new W.StartNumberingValue { Val = 1 },
                        new W.NumberingFormat { Val = def.Ordered ? W.NumberFormatValues.Decimal : W.NumberFormatValues.Bullet },
                        new W.LevelText { Val = def.Ordered ? (def.Marker.Contains("%1", StringComparison.Ordinal) ? def.Marker : "%1.") : def.Marker },
                        new W.LevelJustification { Val = W.LevelJustificationValues.Left }
                    ) { LevelIndex = 0 }
                ) { AbstractNumberId = def.Id });
            }
            foreach (var def in _defs)
                numbering.Append(new W.NumberingInstance(new W.AbstractNumId { Val = def.Id }) { NumberID = def.Id });
            part.Numbering = numbering;
        }
    }
}
