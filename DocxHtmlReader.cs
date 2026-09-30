using System.Globalization;
using System.Text.RegularExpressions;
#pragma warning disable CS0105 // The style-test project already imports System.IO globally.
using System.IO;
#pragma warning restore CS0105
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace ResumeBuilder;

/// <summary>
/// Reads a DOCX into the normalized HTML model. Text is copied as stored. Formatting comes from
/// direct properties, then paragraph and character styles, then document defaults.
/// </summary>
static class DocxHtmlReader {
    public static HtmlResumeDocument Read(string docxPath) {
        using var word = WordprocessingDocument.Open(docxPath, false);
        var main = word.MainDocumentPart ?? throw new InvalidDataException("The DOCX has no document.");
        var reader = new Reader(main);
        return reader.ReadDocument();
    }

    sealed class Reader {
        readonly MainDocumentPart _main;
        readonly Dictionary<string, OpenXmlElement> _styles = new(StringComparer.Ordinal);
        readonly Dictionary<string, string> _themeColors = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> _themeFonts = new(StringComparer.OrdinalIgnoreCase);
        readonly HtmlResumeDocument _doc = new();
        string? _defaultParagraphStyle;

        public Reader(MainDocumentPart main) {
            _main = main;
            LoadTheme();
            LoadStyles();
        }

        public HtmlResumeDocument ReadDocument() {
            var body = _main.Document?.Body ?? throw new InvalidDataException("The DOCX has no body.");
            ReadPage(body.Elements<W.SectionProperties>().LastOrDefault());
            ReadDefaults();
            var flow = new Builder(this, _main);
            flow.ReadContainer(body);
            _doc.BodyBlocks = flow.Blocks;

            var section = body.Elements<W.SectionProperties>().LastOrDefault();
            if (section is not null) ReadChrome(section);
            return _doc;
        }

        void Note(string type) => _doc.Unsupported.Add(type);

        void LoadTheme() {
            var theme = _main.ThemePart?.Theme;
            if (theme is null) return;
            var scheme = theme.Descendants().FirstOrDefault(e => e.LocalName == "clrScheme");
            if (scheme is not null) {
                foreach (var slot in scheme.Elements()) {
                    var srgb = slot.Descendants().FirstOrDefault(e => e.LocalName == "srgbClr");
                    var sys = slot.Descendants().FirstOrDefault(e => e.LocalName == "sysClr");
                    var hex = Attr(srgb, "val") ?? Attr(sys, "lastClr");
                    if (hex is not null) _themeColors[slot.LocalName] = hex;
                }
            }
            var fonts = theme.Descendants().FirstOrDefault(e => e.LocalName == "fontScheme");
            var major = fonts?.Elements().FirstOrDefault(e => e.LocalName == "majorFont");
            var minor = fonts?.Elements().FirstOrDefault(e => e.LocalName == "minorFont");
            var majorLatin = Attr(major?.Elements().FirstOrDefault(e => e.LocalName == "latin"), "typeface");
            var minorLatin = Attr(minor?.Elements().FirstOrDefault(e => e.LocalName == "latin"), "typeface");
            if (majorLatin is not null) _themeFonts["major"] = majorLatin;
            if (minorLatin is not null) _themeFonts["minor"] = minorLatin;
        }

        void LoadStyles() {
            var styles = _main.StyleDefinitionsPart?.Styles;
            if (styles is null) return;
            foreach (var style in styles.Elements<W.Style>()) {
                var id = style.StyleId?.Value;
                if (string.IsNullOrEmpty(id)) continue;
                _styles[id] = style;
                if (style.Default?.Value == true && style.Type?.Value == W.StyleValues.Paragraph)
                    _defaultParagraphStyle = id;
            }
        }

        void ReadDefaults() {
            var props = new Props();
            ApplyElement(props, DefaultChild("rPrDefault", "rPr"), isRun: true);
            ApplyElement(props, DefaultChild("pPrDefault", "pPr"), isRun: false);
            if (_defaultParagraphStyle is not null)
                ApplyChain(props, _defaultParagraphStyle);
            if (props.Font is not null) _doc.Body["font-family"] = CssFont(props.Font);
            if (props.SizeHalf is int size) _doc.Body["font-size"] = HtmlResumeHtml.Pt(size / 2.0);
            if (Hex(props.Color) is string color) _doc.Body["color"] = color;
        }

        OpenXmlElement? DefaultChild(string outer, string inner) =>
            _main.StyleDefinitionsPart?.Styles?.Descendants().FirstOrDefault(e => e.LocalName == outer)
                ?.ChildElements.FirstOrDefault(e => e.LocalName == inner);

        void ReadPage(W.SectionProperties? section) {
            if (section is null) return;
            var size = section.GetFirstChild<W.PageSize>();
            if (size?.Width?.Value is uint width && size.Height?.Value is uint height)
                _doc.Page["size"] = HtmlResumeHtml.Pt(width / 20.0) + " " + HtmlResumeHtml.Pt(height / 20.0);
            var margin = section.GetFirstChild<W.PageMargin>();
            if (margin is not null) {
                _doc.Page["margin"] = string.Join(" ", new[] {
                    HtmlResumeHtml.Pt((margin.Top?.Value ?? 0) / 20.0),
                    HtmlResumeHtml.Pt((margin.Right?.Value ?? 0) / 20.0),
                    HtmlResumeHtml.Pt((margin.Bottom?.Value ?? 0) / 20.0),
                    HtmlResumeHtml.Pt((margin.Left?.Value ?? 0) / 20.0)
                });
                if (margin.Header?.Value is uint header)
                    _doc.Page["header"] = HtmlResumeHtml.Pt(header / 20.0);
                if (margin.Footer?.Value is uint footer)
                    _doc.Page["footer"] = HtmlResumeHtml.Pt(footer / 20.0);
            }
            if (section.Elements<W.Columns>().FirstOrDefault()?.ColumnCount?.Value > 1)
                Note("columns");
        }

        void ReadChrome(W.SectionProperties section) {
            foreach (var reference in section.ChildElements.Where(e => e.LocalName is "headerReference" or "footerReference")) {
                var type = Attr(reference, "type") ?? "default";
                var id = Attr(reference, "id");
                if (id is null) continue;
                if (type != "default") {
                    Note(reference.LocalName == "headerReference" ? "header-" + type : "footer-" + type);
                    continue;
                }
                if (_main.GetPartById(id) is not OpenXmlPart part) continue;
                var root = part.RootElement;
                if (root is null) continue;
                var blocks = new Builder(this, part);
                blocks.ReadContainer(root);
                if (reference.LocalName == "headerReference") _doc.Header = blocks.Blocks;
                else _doc.Footer = blocks.Blocks;
            }
            if (section.Parent is W.Paragraph) Note("sectionBreak");
        }

        List<OpenXmlElement> Chain(string? styleId) {
            var chain = new List<OpenXmlElement>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var next = styleId;
            while (!string.IsNullOrEmpty(next) && seen.Add(next) && _styles.TryGetValue(next, out var style)) {
                chain.Add(style);
                next = Attr(Child(style, "basedOn"), "val");
            }
            chain.Reverse();
            return chain;
        }

        void ApplyChain(Props props, string? styleId) {
            foreach (var style in Chain(styleId)) {
                ApplyElement(props, Child(style, "pPr"), isRun: false);
                ApplyElement(props, Child(style, "rPr"), isRun: true);
            }
        }

        Props ParagraphProps(W.Paragraph paragraph) {
            var props = new Props();
            ApplyElement(props, DefaultChild("rPrDefault", "rPr"), isRun: true);
            ApplyElement(props, DefaultChild("pPrDefault", "pPr"), isRun: false);
            var styleId = Attr(Child(paragraph.ParagraphProperties, "pStyle"), "val") ?? _defaultParagraphStyle;
            ApplyChain(props, styleId);
            ApplyElement(props, paragraph.ParagraphProperties, isRun: false);
            props.StyleId = styleId;
            return props;
        }

        Props RunProps(Props paragraph, OpenXmlElement? runProperties) {
            var props = paragraph.Copy();
            var styleId = Attr(Child(runProperties, "rStyle"), "val");
            if (styleId is not null) {
                foreach (var style in Chain(styleId))
                    ApplyElement(props, Child(style, "rPr"), isRun: true);
            }
            ApplyElement(props, runProperties, isRun: true);
            return props;
        }

        void ApplyElement(Props props, OpenXmlElement? element, bool isRun) {
            if (element is null) return;
            if (isRun || element.LocalName == "rPr") ApplyRun(props, element.LocalName == "rPr" ? element : Child(element, "rPr"));
            if (!isRun) ApplyParagraph(props, element.LocalName == "pPr" ? element : element);
        }

        void ApplyParagraph(Props props, OpenXmlElement? pPr) {
            if (pPr is null || pPr.LocalName == "rPr") return;
            var spacing = Child(pPr, "spacing");
            if (spacing is not null) {
                if (IntAttr(spacing, "before") is int before) props.BeforeTwips = before;
                if (IntAttr(spacing, "after") is int after) props.AfterTwips = after;
                if (IntAttr(spacing, "line") is int line) props.LineTwips = line;
                if (Attr(spacing, "lineRule") is string rule) props.LineRule = rule;
            }
            var ind = Child(pPr, "ind");
            if (ind is not null) {
                if (IntAttr(ind, "left") is int left) props.LeftTwips = left;
                if (IntAttr(ind, "right") is int right) props.RightTwips = right;
                if (IntAttr(ind, "hanging") is int hanging) props.HangingTwips = hanging;
                if (IntAttr(ind, "firstLine") is int first) props.FirstTwips = first;
                if (Attr(ind, "left") is null && Attr(ind, "leftChars") is not null) Note("indentChars");
            }
            if (Attr(Child(pPr, "jc"), "val") is string align)
                props.Align = align;
            if (IntAttr(Child(pPr, "outlineLvl"), "val") is int outline)
                props.Outline = outline;
            var borders = Child(pPr, "pBdr");
            if (borders is not null) {
                props.BorderTop = BorderCss(Child(borders, "top"));
                props.BorderBottom = BorderCss(Child(borders, "bottom"));
                props.BorderLeft = BorderCss(Child(borders, "left"));
                props.BorderRight = BorderCss(Child(borders, "right"));
            }
            if (ShadeCss(Child(pPr, "shd")) is string shade) props.Shading = shade;
            var tabs = Child(pPr, "tabs");
            if (tabs is not null) {
                var stops = tabs.ChildElements.Where(e => e.LocalName == "tab").Select(tab => {
                    var pos = Attr(tab, "pos") ?? "0";
                    var kind = Attr(tab, "val") ?? "left";
                    return pos + " " + kind;
                });
                props.Tabs = string.Join(", ", stops);
            }
        }

        void ApplyRun(Props props, OpenXmlElement? rPr) {
            if (rPr is null) return;
            var fonts = Child(rPr, "rFonts");
            if (fonts is not null) {
                var family = Attr(fonts, "ascii") ?? Attr(fonts, "hAnsi") ?? ThemeFont(Attr(fonts, "asciiTheme") ?? Attr(fonts, "hAnsiTheme"));
                if (!string.IsNullOrEmpty(family)) props.Font = family;
            }
            if (OnOff(Child(rPr, "b")) is bool bold) props.Bold = bold;
            if (OnOff(Child(rPr, "i")) is bool italic) props.Italic = italic;
            var underline = Child(rPr, "u");
            if (underline is not null) {
                var val = Attr(underline, "val");
                props.Underline = val is not ("none" or "false" or "0");
            }
            var color = Child(rPr, "color");
            if (color is not null) props.Color = ColorHex(color, "val", "themeColor", "themeShade", "themeTint");
            if (IntAttr(Child(rPr, "sz"), "val") is int size) props.SizeHalf = size;
            if (IntAttr(Child(rPr, "spacing"), "val") is int spacing) props.LetterTwentieths = spacing;
            if (ShadeCss(Child(rPr, "shd")) is string shade) props.RunShading = shade;
            var highlight = Attr(Child(rPr, "highlight"), "val");
            if (highlight is not null && highlight != "none") props.RunShading ??= HighlightHex(highlight);
            if (Child(rPr, "strike") is not null || Child(rPr, "dstrike") is not null) Note("strike");
            if (Child(rPr, "vertAlign") is not null) Note("vertAlign");
            if (Child(rPr, "caps") is not null || Child(rPr, "smallCaps") is not null) Note("caps");
            if (IntAttr(Child(rPr, "w"), "val") is int scale && scale != 100) Note("characterScale");
            if (Child(rPr, "bdr") is not null) Note("runBorder");
            if (Child(rPr, "em") is not null) Note("emphasisMark");
        }

        string? BorderCss(OpenXmlElement? border) {
            if (border is null) return null;
            var val = Attr(border, "val");
            if (val is null or "nil" or "none") return null;
            var size = IntAttr(border, "sz") ?? 4;
            var color = ColorHex(border, "color", "themeColor", "themeShade", "themeTint") ?? "000000";
            var style = val == "double" ? "double" : "solid";
            var space = IntAttr(border, "space") ?? 0;
            return HtmlResumeHtml.Pt(size / 8.0) + " " + style + " #" + color + " " + space.ToString(CultureInfo.InvariantCulture);
        }

        string? ShadeCss(OpenXmlElement? shade) {
            if (shade is null) return null;
            var pattern = Attr(shade, "val");
            if (pattern is "nil" or "none") return null;
            var hex = ColorHex(shade, "fill", "themeFill", "themeFillShade", "themeFillTint");
            return hex is null ? null : "#" + hex;
        }

        string? ColorHex(OpenXmlElement element, string hexAttr, string themeAttr, string shadeAttr, string tintAttr) {
            var hex = Attr(element, hexAttr);
            if (hex is not null && hex != "auto" && IsHex(hex)) return hex.ToUpperInvariant();
            var theme = Attr(element, themeAttr);
            if (theme is null) return null;
            var slot = theme switch {
                "dark1" or "dk1" => "dk1",
                "light1" or "lt1" => "lt1",
                "dark2" or "dk2" => "dk2",
                "light2" or "lt2" => "lt2",
                "hyperlink" or "hlink" => "hlink",
                "followedHyperlink" or "folHlink" => "folHlink",
                _ => theme
            };
            if (!_themeColors.TryGetValue(slot, out var resolved) || !IsHex(resolved)) return null;
            if (Attr(element, shadeAttr) is string shade) return Mix(resolved, shade, tint: false);
            if (Attr(element, tintAttr) is string tint) return Mix(resolved, tint, tint: true);
            return resolved.ToUpperInvariant();
        }

        static string Mix(string hex, string amount, bool tint) {
            if (!int.TryParse(amount, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var factor))
                return hex.ToUpperInvariant();
            int Channel(int index) {
                var value = int.Parse(hex.Substring(index, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var mixed = tint ? value + (255 - value) * factor / 255 : value * factor / 255;
                return Math.Clamp(mixed, 0, 255);
            }
            return string.Create(CultureInfo.InvariantCulture, $"{Channel(0):X2}{Channel(2):X2}{Channel(4):X2}");
        }

        static string? HighlightHex(string name) => name.ToLowerInvariant() switch {
            "yellow" => "#FFFF00",
            "green" => "#00FF00",
            "cyan" => "#00FFFF",
            "magenta" => "#FF00FF",
            "blue" => "#0000FF",
            "red" => "#FF0000",
            "darkblue" => "#000080",
            "darkcyan" => "#008080",
            "darkgreen" => "#008000",
            "darkmagenta" => "#800080",
            "darkred" => "#800000",
            "darkyellow" => "#808000",
            "darkgray" => "#808080",
            "lightgray" => "#C0C0C0",
            "black" => "#000000",
            "white" => "#FFFFFF",
            _ => null
        };

        string? ThemeFont(string? theme) {
            if (theme is null) return null;
            var key = theme.StartsWith("major", StringComparison.OrdinalIgnoreCase) ? "major" : "minor";
            return _themeFonts.TryGetValue(key, out var font) ? font : null;
        }

        static string? Hex(string? color) =>
            color is not null && color != "000000" && IsHex(color) ? "#" + color.ToUpperInvariant() : null;

        static bool IsHex(string value) => value.Length == 6 && value.All(Uri.IsHexDigit);

        static bool? OnOff(OpenXmlElement? element) {
            if (element is null) return null;
            var val = Attr(element, "val");
            if (val is null) return true;
            return val is not ("0" or "false" or "off" or "none");
        }

        OpenXmlElement? Level(int numId, int ilvl) {
            var numbering = _main.NumberingDefinitionsPart?.Numbering;
            if (numbering is null) return null;
            var instance = numbering.ChildElements.FirstOrDefault(e =>
                e.LocalName == "num" && IntAttr(e, "numId") == numId);
            var abstractId = IntAttr(Child(instance, "abstractNumId"), "val");
            var abs = numbering.ChildElements.FirstOrDefault(e =>
                e.LocalName == "abstractNum" && IntAttr(e, "abstractNumId") == abstractId);
            return abs?.ChildElements.FirstOrDefault(e => e.LocalName == "lvl" && (IntAttr(e, "ilvl") ?? 0) == ilvl);
        }

        sealed class Builder {
            readonly Reader _reader;
            readonly OpenXmlPart _part;
            public List<HtmlBlock> Blocks { get; } = new();
            List<HtmlBlock>? _items;
            string? _kind;
            int _numId = -1;

            public Builder(Reader reader, OpenXmlPart part) {
                _reader = reader;
                _part = part;
            }

            public void ReadContainer(OpenXmlElement container) {
                foreach (var child in container.ChildElements) {
                    switch (child.LocalName) {
                        case "p" when child is W.Paragraph paragraph:
                            ReadParagraph(paragraph);
                            break;
                        case "tbl" when child is W.Table table:
                            Add(ReadTable(table));
                            break;
                        case "sdt":
                            _reader.Note("sdt");
                            var content = child.ChildElements.FirstOrDefault(e => e.LocalName.StartsWith("sdtContent", StringComparison.Ordinal));
                            if (content is not null) ReadContainer(content);
                            break;
                        case "AlternateContent":
                            _reader.Note("alternateContent");
                            var fallback = child.ChildElements.FirstOrDefault(e => e.LocalName == "Fallback")
                                ?? child.ChildElements.FirstOrDefault(e => e.LocalName == "Choice");
                            if (fallback is not null) ReadContainer(fallback);
                            break;
                        case "customXml":
                            _reader.Note("customXml");
                            ReadContainer(child);
                            break;
                        case "altChunk":
                            _reader.Note("altChunk");
                            break;
                        case "sectPr":
                            if (child.Parent is W.Paragraph) _reader.Note("sectionBreak");
                            break;
                        case "del":
                        case "moveFrom":
                            _reader.Note("revision");
                            break;
                        case "ins":
                        case "moveTo":
                            ReadContainer(child);
                            break;
                    }
                }
                Flush();
            }

            void Add(HtmlBlock block) {
                Flush();
                Blocks.Add(block);
            }

            void Flush() {
                if (_items is null) return;
                Blocks.Add(new HtmlBlock { Tag = _kind ?? "ul", Marker = _items.Count > 0 ? _marker : null, Children = _items });
                _items = null;
                _marker = null;
            }

            string? _marker;

            void ReadParagraph(W.Paragraph paragraph) {
                var props = _reader.ParagraphProps(paragraph);
                var num = Numbering(paragraph, props.StyleId);
                if (num is not null && props.LeftTwips is null && props.HangingTwips is null)
                    _reader.ApplyParagraph(props, Child(num.Value.Level, "pPr"));

                var block = new HtmlBlock {
                    Tag = num is null ? TagFor(props) : "li",
                    Style = ParagraphCss(props),
                    SourceStyle = SourceClass(props.StyleId)
                };
                ReadInlines(paragraph, props, block.Inlines, href: null);
                if (block.Inlines.Any(inline => inline.Kind == "page")) {
                    if (block.Inlines.All(inline => inline.Kind == "page")) block.Style["page-break-before"] = "always";
                    else _reader.Note("pageBreak");
                    block.Inlines.RemoveAll(inline => inline.Kind == "page");
                }
                if (num is null) {
                    Add(block);
                    return;
                }
                if (_items is null || _kind != num.Value.Kind || _numId != num.Value.NumId) {
                    Flush();
                    _items = new List<HtmlBlock>();
                    _kind = num.Value.Kind;
                    _numId = num.Value.NumId;
                    _marker = num.Value.Marker;
                }
                _items.Add(block);
            }

            (string Kind, int NumId, string Marker, OpenXmlElement? Level)? Numbering(W.Paragraph paragraph, string? styleId) {
                var direct = Child(paragraph.ParagraphProperties, "numPr");
                OpenXmlElement? numPr = direct;
                if (numPr is null && styleId is not null) {
                    foreach (var style in _reader.Chain(styleId).AsEnumerable().Reverse()) {
                        numPr = Child(Child(style, "pPr"), "numPr");
                        if (numPr is not null) break;
                    }
                }
                var numId = IntAttr(Child(numPr, "numId"), "val") ?? 0;
                if (numPr is null || numId == 0) return null;
                var ilvl = IntAttr(Child(numPr, "ilvl"), "val") ?? 0;
                var level = _reader.Level(numId, ilvl);
                var format = Attr(Child(level, "numFmt"), "val") ?? "bullet";
                var ordered = format is "decimal" or "decimalZero" or "upperLetter" or "lowerLetter" or "upperRoman" or "lowerRoman";
                var marker = Attr(Child(level, "lvlText"), "val");
                var symbolFont = Attr(Child(Child(Child(level, "rPr"), "rFonts"), "ascii"), "ascii")
                    ?? Attr(Child(Child(level, "rPr"), "rFonts"), "ascii");
                if (symbolFont is "Symbol" or "Wingdings" or "Wingdings 2" or "Wingdings 3") marker = "•";
                if (string.IsNullOrEmpty(marker) || marker.Any(ch => ch > 0xF000)) marker = ordered ? "%1." : "•";
                return (ordered ? "ol" : "ul", numId, marker, level);
            }

            void ReadInlines(OpenXmlElement container, Props paragraph, List<HtmlInline> inlines, string? href) {
                var depth = 0;
                var inResult = false;
                var instruction = "";
                foreach (var child in container.ChildElements) Walk(child);

                void Walk(OpenXmlElement node) {
                    switch (node.LocalName) {
                        case "hyperlink":
                            var link = node is W.Hyperlink hyperlink ? HyperlinkTarget(hyperlink.Id?.Value) : null;
                            foreach (var inner in node.ChildElements) WalkLinked(inner, link);
                            break;
                        case "r":
                            ReadRun(node);
                            break;
                        case "fldSimple":
                            ReadSimpleField(node);
                            break;
                        case "ins":
                        case "smartTag":
                        case "sdt":
                            if (node.LocalName == "sdt") _reader.Note("sdt");
                            var content = node.LocalName == "sdt"
                                ? node.ChildElements.FirstOrDefault(e => e.LocalName.StartsWith("sdtContent", StringComparison.Ordinal))
                                : node;
                            if (content is not null) foreach (var inner in content.ChildElements) Walk(inner);
                            break;
                        case "del":
                        case "moveFrom":
                            _reader.Note("revision");
                            break;
                        case "oMath":
                        case "oMathPara":
                            _reader.Note("equation");
                            break;
                        case "bookmarkStart":
                        case "bookmarkEnd":
                        case "proofErr":
                        case "commentRangeStart":
                        case "commentRangeEnd":
                        case "lastRenderedPageBreak":
                            break;
                        case "commentReference":
                            _reader.Note("comment");
                            break;
                        case "footnoteReference":
                            _reader.Note("footnote");
                            break;
                        case "endnoteReference":
                            _reader.Note("endnote");
                            break;
                        default:
                            if (node.LocalName is "drawing" or "pict" or "object") _reader.Note(node.LocalName);
                            break;
                    }
                }

                void WalkLinked(OpenXmlElement node, string? link) {
                    var previous = href;
                    href = link ?? href;
                    Walk(node);
                    href = previous;
                }

                void ReadSimpleField(OpenXmlElement field) {
                    var instr = Attr(field, "instr") ?? "";
                    var page = IsPage(instr);
                    if (!page && instr.Trim().Length > 0 && !instr.Contains("HYPERLINK", StringComparison.OrdinalIgnoreCase))
                        _reader.Note("field");
                    var link = HyperlinkInstruction(instr) ?? href;
                    foreach (var inner in field.ChildElements) {
                        if (inner.LocalName == "r") ReadRun(inner, page, link);
                    }
                }

                void ReadRun(OpenXmlElement run, bool? pageOverride = null, string? hrefOverride = null) {
                    var runProps = _reader.RunProps(paragraph, Child(run, "rPr"));
                    foreach (var piece in run.ChildElements) {
                        switch (piece.LocalName) {
                            case "rPr":
                            case "proofErr":
                            case "lastRenderedPageBreak":
                                break;
                            case "t" when piece is W.Text text:
                                if (depth > 0 && !inResult) break;
                                var page = pageOverride ?? (depth > 0 && inResult && IsPage(instruction));
                                if (depth > 0 && inResult && !page && !instruction.Contains("HYPERLINK", StringComparison.OrdinalIgnoreCase))
                                    _reader.Note("field");
                                AddText(text.Text, runProps, page, hrefOverride ?? href);
                                break;
                            case "tab":
                                if (depth == 0 || inResult) inlines.Add(new HtmlInline { Kind = "tab" });
                                break;
                            case "br":
                            case "cr":
                                var breakType = Attr(piece, "type");
                                if (breakType is "page") {
                                    if (depth == 0 || inResult) inlines.Add(new HtmlInline { Kind = "page" });
                                    break;
                                }
                                if (breakType is "column") { _reader.Note("columnBreak"); break; }
                                if (depth == 0 || inResult) inlines.Add(new HtmlInline { Kind = "br" });
                                break;
                            case "softHyphen":
                                if (depth == 0 || inResult) inlines.Add(new HtmlInline { Kind = "shy" });
                                break;
                            case "noBreakHyphen":
                                if (depth == 0 || inResult) inlines.Add(new HtmlInline { Kind = "nbhyphen" });
                                break;
                            case "sym":
                                _reader.Note("sym");
                                break;
                            case "drawing":
                            case "pict":
                            case "object":
                                _reader.Note(piece.LocalName);
                                break;
                            case "fldChar":
                                var kind = Attr(piece, "fldCharType");
                                if (kind == "begin") { if (depth == 0) instruction = ""; depth++; inResult = false; }
                                else if (kind == "separate" && depth == 1) inResult = true;
                                else if (kind == "end") { depth = Math.Max(0, depth - 1); if (depth == 0) inResult = false; }
                                break;
                            case "instrText":
                                instruction += piece.InnerText;
                                break;
                            case "footnoteReference":
                                _reader.Note("footnote");
                                break;
                            case "endnoteReference":
                                _reader.Note("endnote");
                                break;
                            case "commentReference":
                                _reader.Note("comment");
                                break;
                            case "AlternateContent":
                                _reader.Note("alternateContent");
                                break;
                        }
                    }
                }

                void AddText(string text, Props runProps, bool page, string? link) {
                    if (text.Length == 0) return;
                    inlines.Add(new HtmlInline {
                        Kind = "text",
                        Text = text,
                        Href = link,
                        PageField = page,
                        Style = RunCss(runProps)
                    });
                }
            }

            string? HyperlinkTarget(string? id) {
                if (id is null) return null;
                return _part.HyperlinkRelationships.FirstOrDefault(rel => rel.Id == id)?.Uri?.ToString();
            }

            HtmlBlock ReadTable(W.Table table) {
                var block = new HtmlBlock { Tag = "table" };
                var tblPr = Child(table, "tblPr");
                var styleId = Attr(Child(tblPr, "tblStyle"), "val");
                OpenXmlElement? stylePr = null;
                if (styleId is not null) {
                    foreach (var style in _reader.Chain(styleId))
                        stylePr = Child(style, "tblPr") ?? stylePr;
                }
                ApplyTableBorders(block, Child(stylePr, "tblBorders"));
                ApplyTableBorders(block, Child(tblPr, "tblBorders"));
                if (_reader.ShadeCss(Child(stylePr, "shd")) is string styleShade) block.Style["background-color"] = styleShade;
                if (_reader.ShadeCss(Child(tblPr, "shd")) is string shade) block.Style["background-color"] = shade;
                var grid = Child(table, "tblGrid")?.ChildElements.Where(e => e.LocalName == "gridCol").Select(e => IntAttr(e, "w") ?? 0).ToList()
                    ?? new List<int>();
                var sum = grid.Sum();
                if (sum > 0) block.Style["width"] = HtmlResumeHtml.Pt(sum / 20.0);
                foreach (var row in table.Elements<W.TableRow>()) {
                    var tr = new HtmlBlock { Tag = "tr" };
                    var index = 0;
                    foreach (var cell in row.Elements<W.TableCell>()) {
                        var td = new HtmlBlock { Tag = "td" };
                        var tcPr = Child(cell, "tcPr");
                        var width = IntAttr(Child(tcPr, "tcW"), "w");
                        if (width is null or 0) width = index < grid.Count ? grid[index] : 0;
                        if (width > 0) td.Style["width"] = HtmlResumeHtml.Pt(width.Value / 20.0);
                        if (_reader.ShadeCss(Child(tcPr, "shd")) is string cellShade) td.Style["background-color"] = cellShade;
                        ApplyTableBorders(td, Child(tcPr, "tcBorders"));
                        var align = Attr(Child(tcPr, "vAlign"), "val");
                        if (align is "center" or "bottom") td.Style["vertical-align"] = align == "center" ? "middle" : "bottom";
                        if (Child(tcPr, "gridSpan") is not null) _reader.Note("gridSpan");
                        if (Child(tcPr, "vMerge") is not null) _reader.Note("vMerge");
                        var inner = new Builder(_reader, _part);
                        inner.ReadContainer(cell);
                        td.Children = inner.Blocks;
                        if (td.Children.Count == 0) td.Children.Add(new HtmlBlock { Tag = "p" });
                        tr.Children.Add(td);
                        index++;
                    }
                    block.Children.Add(tr);
                }
                return block;
            }

            void ApplyTableBorders(HtmlBlock block, OpenXmlElement? borders) {
                if (borders is null) return;
                void One(string cssName, string side) {
                    if (_reader.BorderCss(Child(borders, side)) is string css) block.Style[cssName] = css;
                }
                One("border-top", "top");
                One("border-bottom", "bottom");
                One("border-left", "left");
                One("border-right", "right");
                One("border-inside-h", "insideH");
                One("border-inside-v", "insideV");
            }
        }

        static Dictionary<string, string> ParagraphCss(Props props) {
            var css = new Dictionary<string, string>(StringComparer.Ordinal);
            if (props.Align is "center" or "right" or "both") css["text-align"] = props.Align == "both" ? "justify" : props.Align;
            if (props.BeforeTwips is int before) css["margin-top"] = HtmlResumeHtml.Pt(before / 20.0);
            if (props.AfterTwips is int after) css["margin-bottom"] = HtmlResumeHtml.Pt(after / 20.0);
            if (props.LeftTwips is int left && left != 0) css["margin-left"] = HtmlResumeHtml.Pt(left / 20.0);
            if (props.RightTwips is int right && right != 0) css["margin-right"] = HtmlResumeHtml.Pt(right / 20.0);
            if (props.HangingTwips is int hanging && hanging != 0) css["text-indent"] = HtmlResumeHtml.Pt(-hanging / 20.0);
            else if (props.FirstTwips is int first && first != 0) css["text-indent"] = HtmlResumeHtml.Pt(first / 20.0);
            if (props.LineTwips is int line) {
                css["line-height"] = props.LineRule is "exact" or "atLeast"
                    ? HtmlResumeHtml.Pt(line / 20.0)
                    : (line / 240.0).ToString("0.###", CultureInfo.InvariantCulture);
            }
            if (props.BorderTop is not null) css["border-top"] = props.BorderTop;
            if (props.BorderBottom is not null) css["border-bottom"] = props.BorderBottom;
            if (props.BorderLeft is not null) css["border-left"] = props.BorderLeft;
            if (props.BorderRight is not null) css["border-right"] = props.BorderRight;
            if (props.Shading is not null) css["background-color"] = props.Shading;
            if (!string.IsNullOrEmpty(props.Tabs)) css["tab-stops"] = props.Tabs;
            return css;
        }

        static Dictionary<string, string> RunCss(Props props) {
            var css = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(props.Font)) css["font-family"] = CssFont(props.Font);
            if (props.SizeHalf is int size) css["font-size"] = HtmlResumeHtml.Pt(size / 2.0);
            if (props.Bold == true) css["font-weight"] = "700";
            if (props.Italic == true) css["font-style"] = "italic";
            if (props.Underline == true) css["text-decoration"] = "underline";
            if (Hex(props.Color) is string color) css["color"] = color;
            if (props.RunShading is not null) css["background-color"] = props.RunShading;
            if (props.LetterTwentieths is int spacing && spacing != 0)
                css["letter-spacing"] = HtmlResumeHtml.Pt(spacing / 20.0);
            return css;
        }

        static string TagFor(Props props) => props.Outline switch {
            0 => "h1",
            1 => "h2",
            2 => "h3",
            _ => "p"
        };

        static string? SourceClass(string? styleId) {
            if (string.IsNullOrEmpty(styleId) || styleId.Equals("Normal", StringComparison.OrdinalIgnoreCase)) return null;
            var slug = new string(styleId.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            return slug.Length == 0 ? null : "src-" + slug;
        }

        static string CssFont(string family) =>
            family.Any(ch => char.IsWhiteSpace(ch) || ch is ',' or '\'') ? "\"" + family.Replace("\"", "") + "\"" : family;

        static bool IsPage(string instruction) {
            var text = instruction.ToUpperInvariant();
            return text.Contains("PAGE") && !text.Contains("NUMPAGES");
        }

        static string? HyperlinkInstruction(string instruction) {
            var match = Regex.Match(instruction, "HYPERLINK\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : null;
        }

        static OpenXmlElement? Child(OpenXmlElement? parent, string name) =>
            parent?.ChildElements.FirstOrDefault(e => e.LocalName == name);

        static string? Attr(OpenXmlElement? element, string name) {
            if (element is null) return null;
            foreach (var attribute in element.GetAttributes()) {
                if (attribute.LocalName == name && !string.IsNullOrEmpty(attribute.Value)) return attribute.Value;
            }
            return null;
        }

        static int? IntAttr(OpenXmlElement? element, string name) =>
            element is not null && int.TryParse(Attr(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value : null;
    }

    sealed class Props {
        public string? StyleId;
        public string? Font;
        public int? SizeHalf;
        public bool? Bold;
        public bool? Italic;
        public bool? Underline;
        public string? Color;
        public string? RunShading;
        public int? LetterTwentieths;
        public string? Align;
        public int? BeforeTwips;
        public int? AfterTwips;
        public int? LineTwips;
        public string? LineRule;
        public int? LeftTwips;
        public int? RightTwips;
        public int? FirstTwips;
        public int? HangingTwips;
        public int? Outline;
        public string? BorderTop;
        public string? BorderBottom;
        public string? BorderLeft;
        public string? BorderRight;
        public string? Shading;
        public string? Tabs;

        public Props Copy() => (Props)MemberwiseClone();
    }
}
