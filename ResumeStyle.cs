using System.Globalization;
using System.Text.Json.Nodes;

namespace ResumeBuilder;

// ---------------------------------------------------------------------------
// The resume style system.
//
// Styling used to be hard-coded inside the two renderers. It now lives here as a typed model with a
// fixed set of safe tokens, so the AI can return an OPTIONAL "style" object in the profile JSON and
// have it applied without the renderer ever interpreting code, HTML, CSS, OOXML or Word instructions.
//
// The AI decides values (sizes, colours, spacing). The renderer decides mechanics (twips, half-points,
// Word styles, MigraDoc units). Anything outside the tokens below cannot reach a document.
// ---------------------------------------------------------------------------

/// <summary>Allowed ranges for every style value. Shared by the normalizer and the strict validator.</summary>
public static class StyleLimits {
    public const double MinFontSize = 8, MaxFontSize = 24;

    /// <summary>
    /// Floor for body, bullet and education text. Lowered from 11 to 9 so a prompt can ask for the
    /// compact 9 pt body standard; the presets still use 11, so a style that does not ask for 9 pt
    /// renders exactly as before.
    /// </summary>
    public const double MinBodyFontSize = 9;

    /// <summary>Floor for the technical-skills values — the same 9 pt body standard (was 10.5).</summary>
    public const double MinSkillValueFontSize = 9;

    public const double MinLineSpacing = 1.0, MaxLineSpacing = 3.0;
    public const double MinMargin = 0.3, MaxMargin = 1.25;      // inches
    public const double MinSpacing = 0, MaxSpacing = 48;        // points
    public const double MinIndent = 0, MaxIndent = 1.5;         // inches

    public static readonly string[] Alignments = { "left", "center", "right", "justify" };
    public static readonly string[] PageSizes = { "LETTER", "A4" };

    /// <summary>Only fonts that exist on a stock Windows install, so DOCX and PDF agree.</summary>
    public static readonly string[] FontFamilies = {
        "Arial", "Calibri", "Cambria", "Garamond", "Georgia", "Helvetica", "Tahoma", "Times New Roman", "Verdana"
    };
}

/// <summary>Page size and margins. Margins are inches.</summary>
public sealed class PageStyle {
    public string Size = "LETTER";
    public double MarginTop = 0.45, MarginBottom = 0.45, MarginLeft = 0.55, MarginRight = 0.55;

    public PageStyle Clone() => (PageStyle)MemberwiseClone();

    public JsonObject ToJson() => new() {
        ["size"] = Size,
        ["marginTop"] = MarginTop,
        ["marginBottom"] = MarginBottom,
        ["marginLeft"] = MarginLeft,
        ["marginRight"] = MarginRight
    };
}

/// <summary>The three document colours, each a validated #RRGGBB string.</summary>
public sealed class ColorStyle {
    public string Primary = "#1F4E79", Body = "#000000", Secondary = "#4A4A4A";

    public ColorStyle Clone() => (ColorStyle)MemberwiseClone();

    public JsonObject ToJson() => new() { ["primary"] = Primary, ["body"] = Body, ["secondary"] = Secondary };
}

/// <summary>The document font. One family for the whole resume — single-column, no mixed faces.</summary>
public sealed class FontSpec {
    public string Family = "Arial";

    public FontSpec Clone() => (FontSpec)MemberwiseClone();

    public JsonObject ToJson() => new() { ["family"] = Family };
}

/// <summary>
/// One styled block of text. Not every property is accepted by every section — see
/// <see cref="StyleSchema"/> — which is what keeps, for example, a border off a body paragraph.
/// Sizes and spacing are points; indents are inches; line spacing is a multiple.
/// </summary>
public sealed class TextStyle {
    public double FontSize = 11;
    public bool Bold;
    public bool Italic;
    public string Color = "#000000";
    public string Alignment = "left";
    public bool Uppercase;
    public double SpaceBefore;
    public double SpaceAfter;
    public double LineSpacing = 1.0;
    public double LeftIndent;
    public double HangingIndent;
    public bool BottomBorder;
    public bool KeepWithNext;

    public TextStyle Clone() => (TextStyle)MemberwiseClone();

    /// <summary>Emits only the properties this section supports, always in the same order.</summary>
    public JsonObject ToJson(string section) {
        var allowed = StyleSchema.Allowed(section);
        var o = new JsonObject();
        foreach (var key in StyleSchema.AllTextKeys) {
            if (!allowed.Contains(key)) continue;
            o[key] = key switch {
                "fontSize" => FontSize,
                "bold" => Bold,
                "italic" => Italic,
                "color" => Color,
                "alignment" => Alignment,
                "uppercase" => Uppercase,
                "spaceBefore" => SpaceBefore,
                "spaceAfter" => SpaceAfter,
                "lineSpacing" => LineSpacing,
                "leftIndent" => LeftIndent,
                "hangingIndent" => HangingIndent,
                "bottomBorder" => BottomBorder,
                "keepWithNext" => KeepWithNext,
                _ => (JsonNode?)null
            };
        }
        return o;
    }
}

/// <summary>Which style properties each section accepts. Anything else is an unsupported field.</summary>
public static class StyleSchema {
    /// <summary>Every text property, in the order effective-style.json emits them.</summary>
    public static readonly string[] AllTextKeys = {
        "fontSize", "bold", "italic", "color", "alignment", "uppercase",
        "spaceBefore", "spaceAfter", "lineSpacing", "leftIndent", "hangingIndent",
        "bottomBorder", "keepWithNext"
    };

    /// <summary>The text sections, in document order.</summary>
    public static readonly string[] Sections = {
        "name", "headline", "contact", "sectionHeading", "skillCategory", "skillValues",
        "companyHeading", "subtitle", "metadata", "body", "bullet", "education"
    };

    /// <summary>The top-level style properties.</summary>
    public static readonly string[] TopLevel = { "preset", "page", "colors", "fonts" };

    /// <summary>The keys every text section accepts. Public so the GPT contract is generated from it.</summary>
    public static readonly string[] Common = { "fontSize", "bold", "italic", "color", "alignment", "spaceBefore", "spaceAfter", "lineSpacing" };

    /// <summary>The sections whose weight is always regular: bold is forced off and rejected strictly.</summary>
    public static readonly string[] AlwaysRegular = { "skillValues", "body", "bullet", "education" };

    static readonly Dictionary<string, string[]> BySection = new(StringComparer.OrdinalIgnoreCase) {
        ["name"] = Common,
        ["headline"] = Common,
        ["contact"] = Common,
        ["sectionHeading"] = Common.Concat(new[] { "uppercase", "bottomBorder", "keepWithNext" }).ToArray(),
        ["skillCategory"] = Common.Concat(new[] { "uppercase", "keepWithNext" }).ToArray(),
        ["skillValues"] = Common.Concat(new[] { "leftIndent" }).ToArray(),
        ["companyHeading"] = Common.Concat(new[] { "uppercase", "keepWithNext" }).ToArray(),
        ["subtitle"] = Common.Concat(new[] { "keepWithNext" }).ToArray(),
        ["metadata"] = Common,
        ["body"] = Common,
        ["bullet"] = Common.Concat(new[] { "leftIndent", "hangingIndent" }).ToArray(),
        ["education"] = Common
    };

    public static string[] Allowed(string section) =>
        BySection.TryGetValue(section, out var keys) ? keys : Array.Empty<string>();

    public static bool IsSection(string name) => BySection.ContainsKey(name);
}

/// <summary>
/// One complete, already-validated resume style. Renderers only ever receive an instance produced by
/// <see cref="StyleNormalizer"/>, never raw AI JSON.
/// </summary>
public sealed class ResumeStyle {
    public string Preset = StylePresets.Default;
    public PageStyle Page = new();
    public ColorStyle Colors = new();
    public FontSpec Fonts = new();

    public TextStyle Name = new(), Headline = new(), Contact = new(), SectionHeading = new(),
                     SkillCategory = new(), SkillValues = new(), CompanyHeading = new(), Subtitle = new(),
                     Metadata = new(), Body = new(), Bullet = new(), Education = new();

    public TextStyle Section(string name) => name switch {
        "name" => Name,
        "headline" => Headline,
        "contact" => Contact,
        "sectionHeading" => SectionHeading,
        "skillCategory" => SkillCategory,
        "skillValues" => SkillValues,
        "companyHeading" => CompanyHeading,
        "subtitle" => Subtitle,
        "metadata" => Metadata,
        "body" => Body,
        "bullet" => Bullet,
        "education" => Education,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown style section.")
    };

    public void SetSection(string name, TextStyle style) {
        switch (name) {
            case "name": Name = style; break;
            case "headline": Headline = style; break;
            case "contact": Contact = style; break;
            case "sectionHeading": SectionHeading = style; break;
            case "skillCategory": SkillCategory = style; break;
            case "skillValues": SkillValues = style; break;
            case "companyHeading": CompanyHeading = style; break;
            case "subtitle": Subtitle = style; break;
            case "metadata": Metadata = style; break;
            case "body": Body = style; break;
            case "bullet": Bullet = style; break;
            case "education": Education = style; break;
            default: throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown style section.");
        }
    }

    public ResumeStyle Clone() {
        var copy = new ResumeStyle {
            Preset = Preset, Page = Page.Clone(), Colors = Colors.Clone(), Fonts = Fonts.Clone()
        };
        foreach (var section in StyleSchema.Sections) copy.SetSection(section, Section(section).Clone());
        return copy;
    }

    /// <summary>The effective style, written next to a generated document so styling can be debugged.</summary>
    public JsonObject ToJson() {
        var o = new JsonObject {
            ["preset"] = Preset,
            ["page"] = Page.ToJson(),
            ["colors"] = Colors.ToJson(),
            ["fonts"] = Fonts.ToJson()
        };
        foreach (var section in StyleSchema.Sections) o[section] = Section(section).ToJson(section);
        return o;
    }

    public string ToJsonString() => ToJson().ToJsonString(new System.Text.Json.JsonSerializerOptions {
        WriteIndented = true,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    });

    /// <summary>#RRGGBB -> bytes. The value has already been validated, so this cannot fail.</summary>
    public static (byte R, byte G, byte B) Rgb(string hex) {
        var v = hex.TrimStart('#');
        return (byte.Parse(v.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(v.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(v.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// A preset is simply a complete default style. promV4.12 is the default and is what a profile with
/// no "style" object renders as, so every older profile keeps its exact appearance rules.
/// </summary>
public static class StylePresets {
    public const string Default = "promV4.12";

    public static readonly string[] Names = { "promV4.12", "compact", "classic" };

    public static bool Exists(string? name) =>
        name is not null && Names.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Canonical spelling of a known preset name, or null.</summary>
    public static string? Canonical(string? name) =>
        Names.FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static ResumeStyle Get(string? name) => (Canonical(name) ?? Default) switch {
        "compact" => Compact(),
        "classic" => Classic(),
        _ => PromV412()
    };

    /// <summary>The house style: navy headings, Arial, tight single-column layout.</summary>
    public static ResumeStyle PromV412() => new() {
        Preset = "promV4.12",
        Page = new PageStyle { Size = "LETTER", MarginTop = 0.45, MarginBottom = 0.45, MarginLeft = 0.55, MarginRight = 0.55 },
        Colors = new ColorStyle { Primary = "#1F4E79", Body = "#000000", Secondary = "#4A4A4A" },
        Fonts = new FontSpec { Family = "Arial" },
        Name = new TextStyle { FontSize = 17, Bold = true, Alignment = "center", Color = "#000000", SpaceAfter = 0 },
        Headline = new TextStyle { FontSize = 12, Bold = true, Alignment = "center", Color = "#000000", SpaceAfter = 0 },
        Contact = new TextStyle { FontSize = 10, Alignment = "center", Color = "#000000", SpaceAfter = 3 },
        SectionHeading = new TextStyle {
            FontSize = 12.5, Bold = true, Color = "#1F4E79", Uppercase = true,
            SpaceBefore = 4, SpaceAfter = 1, BottomBorder = true, KeepWithNext = true
        },
        SkillCategory = new TextStyle { FontSize = 11.5, Bold = true, Color = "#1F4E79", SpaceBefore = 2, SpaceAfter = 0, KeepWithNext = true },
        SkillValues = new TextStyle { FontSize = 10.5, Bold = false, Color = "#000000", LineSpacing = 1.0, SpaceAfter = 1 },
        CompanyHeading = new TextStyle { FontSize = 12, Bold = true, Color = "#1F4E79", SpaceBefore = 3, SpaceAfter = 0, KeepWithNext = true },
        Subtitle = new TextStyle { FontSize = 11, Bold = true, Color = "#4A4A4A", SpaceBefore = 1, SpaceAfter = 0, KeepWithNext = true },
        Metadata = new TextStyle { FontSize = 10.5, Color = "#4A4A4A", SpaceAfter = 1 },
        Body = new TextStyle { FontSize = 11, Color = "#000000", LineSpacing = 1.0, SpaceAfter = 1 },
        Bullet = new TextStyle { FontSize = 11, Color = "#000000", LineSpacing = 1.0, LeftIndent = 0.18, HangingIndent = 0.14, SpaceAfter = 1 },
        Education = new TextStyle { FontSize = 11, Color = "#000000", LineSpacing = 1.0, SpaceAfter = 0 }
    };

    /// <summary>Same typography floors, less air — for a long history that must stay on two pages.</summary>
    public static ResumeStyle Compact() {
        var s = PromV412();
        s.Preset = "compact";
        s.Page = new PageStyle { Size = "LETTER", MarginTop = 0.35, MarginBottom = 0.35, MarginLeft = 0.45, MarginRight = 0.45 };
        s.Name.FontSize = 15;
        s.Headline.FontSize = 11;
        s.Contact.FontSize = 9.5;
        s.Contact.SpaceAfter = 2;
        s.SectionHeading.FontSize = 11.5;
        s.SectionHeading.SpaceBefore = 3;
        s.SectionHeading.SpaceAfter = 0;
        s.SkillCategory.FontSize = 11;
        s.SkillCategory.SpaceBefore = 1;
        s.SkillValues.FontSize = 10.5;
        s.SkillValues.SpaceAfter = 0;
        s.CompanyHeading.FontSize = 11.5;
        s.CompanyHeading.SpaceBefore = 2;
        s.Metadata.FontSize = 10;
        s.Metadata.SpaceAfter = 0;
        s.Body.SpaceAfter = 0;
        s.Bullet.SpaceAfter = 0;
        return s;
    }

    /// <summary>Black-and-white serif: no colour at all, wider margins, for conservative employers.</summary>
    public static ResumeStyle Classic() {
        var s = PromV412();
        s.Preset = "classic";
        s.Page = new PageStyle { Size = "LETTER", MarginTop = 0.75, MarginBottom = 0.75, MarginLeft = 0.75, MarginRight = 0.75 };
        s.Colors = new ColorStyle { Primary = "#000000", Body = "#000000", Secondary = "#333333" };
        s.Fonts = new FontSpec { Family = "Times New Roman" };
        s.Name.FontSize = 18;
        s.Headline.FontSize = 12;
        s.Headline.Bold = false;
        s.Contact.FontSize = 10.5;
        s.Contact.SpaceAfter = 6;
        s.SectionHeading.FontSize = 12;
        s.SectionHeading.Color = "#000000";
        s.SectionHeading.SpaceBefore = 8;
        s.SectionHeading.SpaceAfter = 2;
        s.SkillCategory.Color = "#000000";
        s.SkillCategory.FontSize = 11;
        s.SkillCategory.SpaceBefore = 3;
        s.SkillValues.FontSize = 11;
        s.SkillValues.SpaceAfter = 2;
        s.CompanyHeading.Color = "#000000";
        s.CompanyHeading.FontSize = 11.5;
        s.CompanyHeading.SpaceBefore = 6;
        s.Subtitle.Color = "#333333";
        s.Metadata.FontSize = 11;
        s.Metadata.Italic = true;
        s.Metadata.Color = "#333333";
        s.Metadata.SpaceAfter = 2;
        s.Body.SpaceAfter = 3;
        s.Bullet.SpaceAfter = 2;
        s.Bullet.LeftIndent = 0.25;
        s.Bullet.HangingIndent = 0.18;
        s.Education.SpaceAfter = 2;
        return s;
    }
}

