using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ResumeBuilder;

/// <summary>A normalized style plus everything that had to be corrected to produce it.</summary>
public sealed class StyleNormalizationResult {
    public ResumeStyle Style { get; init; } = StylePresets.PromV412();
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// preset -> deep merge of the AI's overrides -> range clamping -> hard rule enforcement.
///
/// Normalization never fails: an unusable value is corrected and reported, so a styling mistake in an
/// AI answer can never fail a job whose resume content is perfectly good. Callers that need a hard
/// yes/no (the JSON contract, the tests) use <see cref="StyleValidator"/> instead, which rejects the
/// same values with a clear message.
/// </summary>
public static class StyleNormalizer {
    static readonly Regex HexColor = new(@"^#[0-9A-Fa-f]{6}$", RegexOptions.Compiled);

    /// <summary>Which palette colour a section takes when it does not name its own.</summary>
    static readonly Dictionary<string, string> ColorRole = new(StringComparer.Ordinal) {
        ["name"] = "body", ["headline"] = "body", ["contact"] = "body",
        ["sectionHeading"] = "primary", ["skillCategory"] = "primary", ["companyHeading"] = "primary",
        ["skillValues"] = "body", ["subtitle"] = "secondary", ["metadata"] = "secondary",
        ["body"] = "body", ["bullet"] = "body", ["education"] = "body"
    };

    public static bool IsHexColor(string? value) => value is not null && HexColor.IsMatch(value);

    /// <summary>The style a profile with no "style" object renders with.</summary>
    public static ResumeStyle Default() => StylePresets.Get(StylePresets.Default);

    public static StyleNormalizationResult Normalize(JsonNode? style) {
        var warnings = new List<string>();

        if (style is null) return new StyleNormalizationResult { Style = Default() };

        if (style is not JsonObject o) {
            warnings.Add("style is not an object; the " + StylePresets.Default + " preset was used instead.");
            var fallback = new StyleNormalizationResult { Style = Default() };
            fallback.Warnings.AddRange(warnings);
            return fallback;
        }

        var presetName = ReadString(o, "preset");
        if (presetName is not null && StylePresets.Canonical(presetName) is null)
            warnings.Add($"style.preset \"{presetName}\" is not a known preset; {StylePresets.Default} was used " +
                         $"(known presets: {string.Join(", ", StylePresets.Names)}).");

        var result = StylePresets.Get(presetName);
        result.Preset = StylePresets.Canonical(presetName) ?? StylePresets.Default;

        foreach (var pair in o) {
            if (StyleSchema.TopLevel.Contains(pair.Key, StringComparer.Ordinal)) continue;
            if (StyleSchema.IsSection(pair.Key)) continue;
            warnings.Add($"style.{pair.Key} is not a supported style property; it was ignored.");
        }

        ApplyPage(result.Page, o["page"], warnings);
        ApplyColors(result.Colors, o["colors"], warnings);
        ApplyFonts(result.Fonts, o["fonts"], warnings);

        foreach (var section in StyleSchema.Sections)
            ApplySection(result, section, o[section], warnings);

        Enforce(result, warnings);

        var normalized = new StyleNormalizationResult { Style = result };
        normalized.Warnings.AddRange(warnings);
        return normalized;
    }

    // ---------- page / colours / fonts ----------

    static void ApplyPage(PageStyle page, JsonNode? node, List<string> warnings) {
        if (node is null) return;
        if (node is not JsonObject o) { warnings.Add("style.page is not an object; it was ignored."); return; }

        foreach (var pair in o)
            if (pair.Key is not ("size" or "marginTop" or "marginBottom" or "marginLeft" or "marginRight"))
                warnings.Add($"style.page.{pair.Key} is not a supported page property; it was ignored.");

        if (ReadString(o, "size") is string size) {
            var match = StyleLimits.PageSizes.FirstOrDefault(p => p.Equals(size, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                warnings.Add($"style.page.size \"{size}\" is not supported; {page.Size} was kept " +
                             $"(supported: {string.Join(", ", StyleLimits.PageSizes)}).");
            else page.Size = match;
        }

        page.MarginTop = Margin(o, "marginTop", page.MarginTop, warnings);
        page.MarginBottom = Margin(o, "marginBottom", page.MarginBottom, warnings);
        page.MarginLeft = Margin(o, "marginLeft", page.MarginLeft, warnings);
        page.MarginRight = Margin(o, "marginRight", page.MarginRight, warnings);
    }

    static double Margin(JsonObject o, string key, double current, List<string> warnings) =>
        Number(o, key, current, StyleLimits.MinMargin, StyleLimits.MaxMargin, "style.page." + key, "in", warnings);

    static void ApplyColors(ColorStyle colors, JsonNode? node, List<string> warnings) {
        if (node is null) return;
        if (node is not JsonObject o) { warnings.Add("style.colors is not an object; it was ignored."); return; }

        foreach (var pair in o)
            if (pair.Key is not ("primary" or "body" or "secondary"))
                warnings.Add($"style.colors.{pair.Key} is not a supported colour; it was ignored.");

        colors.Primary = Color(o, "primary", colors.Primary, "style.colors.primary", warnings);
        colors.Body = Color(o, "body", colors.Body, "style.colors.body", warnings);
        colors.Secondary = Color(o, "secondary", colors.Secondary, "style.colors.secondary", warnings);
    }

    static string Color(JsonObject o, string key, string current, string path, List<string> warnings) {
        if (!o.ContainsKey(key)) return current;
        var value = ReadString(o, key);
        if (IsHexColor(value)) return value!.ToUpperInvariant();
        warnings.Add($"{path} is not a valid #RRGGBB colour; {current} was kept.");
        return current;
    }

    static void ApplyFonts(FontSpec fonts, JsonNode? node, List<string> warnings) {
        if (node is null) return;
        if (node is not JsonObject o) { warnings.Add("style.fonts is not an object; it was ignored."); return; }

        foreach (var pair in o)
            if (pair.Key != "family")
                warnings.Add($"style.fonts.{pair.Key} is not a supported font property; it was ignored.");

        if (!o.ContainsKey("family")) return;
        var family = ReadString(o, "family");
        var match = StyleLimits.FontFamilies.FirstOrDefault(f => f.Equals(family, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            warnings.Add($"style.fonts.family \"{family}\" is not an allowed font; {fonts.Family} was kept " +
                         $"(allowed: {string.Join(", ", StyleLimits.FontFamilies)}).");
        else fonts.Family = match;
    }

    // ---------- text sections ----------

    static void ApplySection(ResumeStyle style, string section, JsonNode? node, List<string> warnings) {
        var target = style.Section(section);
        var o = node as JsonObject;

        if (node is not null && o is null)
            warnings.Add($"style.{section} is not an object; it was ignored.");

        var allowed = StyleSchema.Allowed(section);
        var path = "style." + section + ".";

        if (o is not null) {
            foreach (var pair in o) {
                if (allowed.Contains(pair.Key, StringComparer.Ordinal)) continue;
                warnings.Add(StyleSchema.AllTextKeys.Contains(pair.Key, StringComparer.Ordinal)
                    ? $"{path}{pair.Key} is not supported on this section; it was ignored."
                    : $"{path}{pair.Key} is not a supported style property; it was ignored.");
            }

            foreach (var key in allowed) {
                if (!o.ContainsKey(key)) continue;
                switch (key) {
                    case "fontSize":
                        target.FontSize = Number(o, key, target.FontSize, StyleLimits.MinFontSize, StyleLimits.MaxFontSize, path + key, "pt", warnings);
                        break;
                    case "spaceBefore":
                        target.SpaceBefore = Number(o, key, target.SpaceBefore, StyleLimits.MinSpacing, StyleLimits.MaxSpacing, path + key, "pt", warnings);
                        break;
                    case "spaceAfter":
                        target.SpaceAfter = Number(o, key, target.SpaceAfter, StyleLimits.MinSpacing, StyleLimits.MaxSpacing, path + key, "pt", warnings);
                        break;
                    case "lineSpacing":
                        target.LineSpacing = Number(o, key, target.LineSpacing, StyleLimits.MinLineSpacing, StyleLimits.MaxLineSpacing, path + key, "", warnings);
                        break;
                    case "leftIndent":
                        target.LeftIndent = Number(o, key, target.LeftIndent, StyleLimits.MinIndent, StyleLimits.MaxIndent, path + key, "in", warnings);
                        break;
                    case "hangingIndent":
                        target.HangingIndent = Number(o, key, target.HangingIndent, StyleLimits.MinIndent, StyleLimits.MaxIndent, path + key, "in", warnings);
                        break;
                    case "bold": target.Bold = Flag(o, key, target.Bold, path + key, warnings); break;
                    case "italic": target.Italic = Flag(o, key, target.Italic, path + key, warnings); break;
                    case "uppercase": target.Uppercase = Flag(o, key, target.Uppercase, path + key, warnings); break;
                    case "bottomBorder": target.BottomBorder = Flag(o, key, target.BottomBorder, path + key, warnings); break;
                    case "keepWithNext": target.KeepWithNext = Flag(o, key, target.KeepWithNext, path + key, warnings); break;
                    case "color": target.Color = Color(o, key, target.Color, path + key, warnings); break;
                    case "alignment": target.Alignment = Alignment(o, key, target.Alignment, path + key, warnings); break;
                }
            }
        }

        // A section that names no colour of its own follows the palette, so overriding colors.primary
        // recolours every heading without the AI having to repeat itself.
        var explicitColor = o is not null && o.ContainsKey("color") && allowed.Contains("color", StringComparer.Ordinal);
        if (!explicitColor && ColorRole.TryGetValue(section, out var role))
            target.Color = role switch {
                "primary" => style.Colors.Primary,
                "secondary" => style.Colors.Secondary,
                _ => style.Colors.Body
            };
    }

    static string Alignment(JsonObject o, string key, string current, string path, List<string> warnings) {
        var value = ReadString(o, key);
        var match = StyleLimits.Alignments.FirstOrDefault(a => a.Equals(value, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match;
        warnings.Add($"{path} \"{value}\" is not a supported alignment; {current} was kept " +
                     $"(supported: {string.Join(", ", StyleLimits.Alignments)}).");
        return current;
    }

    static bool Flag(JsonObject o, string key, bool current, string path, List<string> warnings) {
        if (o[key] is JsonValue v && v.TryGetValue<bool>(out var b)) return b;
        warnings.Add($"{path} must be true or false; {current.ToString().ToLowerInvariant()} was kept.");
        return current;
    }

    static double Number(JsonObject o, string key, double current, double min, double max, string path, string unit, List<string> warnings) {
        if (!o.ContainsKey(key)) return current;
        if (o[key] is not JsonValue v || !v.TryGetValue<double>(out var value) || double.IsNaN(value) || double.IsInfinity(value)) {
            warnings.Add($"{path} must be a number; {Fmt(current)}{Suffix(unit)} was kept.");
            return current;
        }
        if (value < min) {
            warnings.Add($"{path} {Fmt(value)}{Suffix(unit)} is below the {Fmt(min)}{Suffix(unit)} minimum; it was raised to {Fmt(min)}{Suffix(unit)}.");
            return min;
        }
        if (value > max) {
            warnings.Add($"{path} {Fmt(value)}{Suffix(unit)} is above the {Fmt(max)}{Suffix(unit)} maximum; it was lowered to {Fmt(max)}{Suffix(unit)}.");
            return max;
        }
        return value;
    }

    static string Suffix(string unit) => unit.Length == 0 ? "" : " " + unit;

    internal static string Fmt(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    static string? ReadString(JsonObject o, string key) =>
        o.TryGetPropertyValue(key, out var node) && node is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null;

    // ---------- hard rules ----------

    /// <summary>
    /// The rules that hold whatever the AI asked for: ATS-safe typography, and the weight rules that
    /// keep a resume readable — summary, skill values and education never render bold.
    /// </summary>
    static void Enforce(ResumeStyle style, List<string> warnings) {
        Floor(style.Body, StyleLimits.MinBodyFontSize, "style.body.fontSize", warnings);
        Floor(style.Bullet, StyleLimits.MinBodyFontSize, "style.bullet.fontSize", warnings);
        Floor(style.SkillValues, StyleLimits.MinSkillValueFontSize, "style.skillValues.fontSize", warnings);
        Floor(style.Education, StyleLimits.MinBodyFontSize, "style.education.fontSize", warnings);

        foreach (var section in StyleSchema.Sections) {
            var text = style.Section(section);
            if (text.LineSpacing >= StyleLimits.MinLineSpacing) continue;
            warnings.Add($"style.{section}.lineSpacing was raised to the {Fmt(StyleLimits.MinLineSpacing)} minimum.");
            text.LineSpacing = StyleLimits.MinLineSpacing;
        }

        Regular(style.Body, "style.body.bold", "the professional summary is always regular weight", warnings);
        Regular(style.SkillValues, "style.skillValues.bold", "individual skills are always regular weight", warnings);
        Regular(style.Education, "style.education.bold", "education is always regular weight", warnings);

        // A fully bold bullet is the one emphasis rule the renderer cannot fix per line.
        Regular(style.Bullet, "style.bullet.bold", "bullets carry emphasis per segment, not as a whole", warnings);
    }

    static void Floor(TextStyle text, double minimum, string path, List<string> warnings) {
        if (text.FontSize >= minimum) return;
        warnings.Add($"{path} was raised to the {Fmt(minimum)} pt minimum.");
        text.FontSize = minimum;
    }

    static void Regular(TextStyle text, string path, string because, List<string> warnings) {
        if (!text.Bold) return;
        warnings.Add($"{path} was turned off — {because}.");
        text.Bold = false;
    }
}

/// <summary>
/// The strict counterpart to <see cref="StyleNormalizer"/>: the same rules, reported as errors instead
/// of being corrected. Used by the documented JSON contract check and by the tests, so a style mistake
/// is visible rather than silently repaired.
/// </summary>
public static class StyleValidator {
    public static List<string> Validate(JsonNode? style, string path = "style") {
        var errors = new List<string>();
        if (style is null) return errors;

        if (style is not JsonObject o) { errors.Add($"{path} must be an object."); return errors; }

        if (o.ContainsKey("preset")) {
            var preset = o["preset"] is JsonValue pv && pv.TryGetValue<string>(out var p) ? p : null;
            if (preset is null || StylePresets.Canonical(preset) is null)
                errors.Add($"{path}.preset must be one of: {string.Join(", ", StylePresets.Names)}");
        }

        foreach (var pair in o)
            if (!StyleSchema.TopLevel.Contains(pair.Key, StringComparer.Ordinal) && !StyleSchema.IsSection(pair.Key))
                errors.Add($"{path}.{pair.Key} is not a supported style property");

        ValidatePage(o["page"], path + ".page", errors);
        ValidateColors(o["colors"], path + ".colors", errors);
        ValidateFonts(o["fonts"], path + ".fonts", errors);

        foreach (var section in StyleSchema.Sections)
            ValidateSection(o[section], section, path + "." + section, errors);

        return errors;
    }

    static void ValidatePage(JsonNode? node, string path, List<string> errors) {
        if (node is null) return;
        if (node is not JsonObject o) { errors.Add($"{path} must be an object"); return; }

        foreach (var pair in o)
            if (pair.Key is not ("size" or "marginTop" or "marginBottom" or "marginLeft" or "marginRight"))
                errors.Add($"{path}.{pair.Key} is not a supported page property");

        if (o.ContainsKey("size")) {
            var size = o["size"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            if (size is null || !StyleLimits.PageSizes.Any(p => p.Equals(size, StringComparison.OrdinalIgnoreCase)))
                errors.Add($"{path}.size must be one of: {string.Join(", ", StyleLimits.PageSizes)}");
        }

        foreach (var key in new[] { "marginTop", "marginBottom", "marginLeft", "marginRight" })
            Range(o, key, path + "." + key, StyleLimits.MinMargin, StyleLimits.MaxMargin, "in", errors);
    }

    static void ValidateColors(JsonNode? node, string path, List<string> errors) {
        if (node is null) return;
        if (node is not JsonObject o) { errors.Add($"{path} must be an object"); return; }

        foreach (var pair in o) {
            if (pair.Key is not ("primary" or "body" or "secondary")) {
                errors.Add($"{path}.{pair.Key} is not a supported colour");
                continue;
            }
            var value = pair.Value is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            if (!StyleNormalizer.IsHexColor(value)) errors.Add($"{path}.{pair.Key} is not a valid hex color (#RRGGBB)");
        }
    }

    static void ValidateFonts(JsonNode? node, string path, List<string> errors) {
        if (node is null) return;
        if (node is not JsonObject o) { errors.Add($"{path} must be an object"); return; }

        foreach (var pair in o)
            if (pair.Key != "family") errors.Add($"{path}.{pair.Key} is not a supported font property");

        if (!o.ContainsKey("family")) return;
        var family = o["family"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        if (family is null || !StyleLimits.FontFamilies.Any(f => f.Equals(family, StringComparison.OrdinalIgnoreCase)))
            errors.Add($"{path}.family must be one of: {string.Join(", ", StyleLimits.FontFamilies)}");
    }

    static void ValidateSection(JsonNode? node, string section, string path, List<string> errors) {
        if (node is null) return;
        if (node is not JsonObject o) { errors.Add($"{path} must be an object"); return; }

        var allowed = StyleSchema.Allowed(section);
        foreach (var pair in o)
            if (!allowed.Contains(pair.Key, StringComparer.Ordinal))
                errors.Add($"{path}.{pair.Key} is not a supported style property on {section}");

        var floor = section switch {
            "body" or "bullet" or "education" => StyleLimits.MinBodyFontSize,
            "skillValues" => StyleLimits.MinSkillValueFontSize,
            _ => StyleLimits.MinFontSize
        };
        Range(o, "fontSize", path + ".fontSize", floor, StyleLimits.MaxFontSize, "pt", errors);
        Range(o, "spaceBefore", path + ".spaceBefore", StyleLimits.MinSpacing, StyleLimits.MaxSpacing, "pt", errors);
        Range(o, "spaceAfter", path + ".spaceAfter", StyleLimits.MinSpacing, StyleLimits.MaxSpacing, "pt", errors);
        Range(o, "lineSpacing", path + ".lineSpacing", StyleLimits.MinLineSpacing, StyleLimits.MaxLineSpacing, "", errors);
        Range(o, "leftIndent", path + ".leftIndent", StyleLimits.MinIndent, StyleLimits.MaxIndent, "in", errors);
        Range(o, "hangingIndent", path + ".hangingIndent", StyleLimits.MinIndent, StyleLimits.MaxIndent, "in", errors);

        foreach (var key in new[] { "bold", "italic", "uppercase", "bottomBorder", "keepWithNext" }) {
            if (!o.ContainsKey(key) || !allowed.Contains(key, StringComparer.Ordinal)) continue;
            if (o[key] is not JsonValue v || !v.TryGetValue<bool>(out _)) errors.Add($"{path}.{key} must be true or false");
        }

        if (o.ContainsKey("color") && allowed.Contains("color", StringComparer.Ordinal)) {
            var color = o["color"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            if (!StyleNormalizer.IsHexColor(color)) errors.Add($"{path}.color is not a valid hex color (#RRGGBB)");
        }

        if (o.ContainsKey("alignment") && allowed.Contains("alignment", StringComparer.Ordinal)) {
            var alignment = o["alignment"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            if (alignment is null || !StyleLimits.Alignments.Any(a => a.Equals(alignment, StringComparison.OrdinalIgnoreCase)))
                errors.Add($"{path}.alignment must be one of: {string.Join(", ", StyleLimits.Alignments)}");
        }

        // The weight rules are not negotiable, so asking for them is an error, not a preference.
        if (section is "body" or "skillValues" or "education" or "bullet"
            && o["bold"] is JsonValue bold && bold.TryGetValue<bool>(out var isBold) && isBold)
            errors.Add($"{path}.bold must be false — {Because(section)}");
    }

    static string Because(string section) => section switch {
        "body" => "the professional summary is always regular weight",
        "skillValues" => "individual skills are always regular weight",
        "education" => "education is always regular weight",
        _ => "bullets carry emphasis per segment, not as a whole"
    };

    static void Range(JsonObject o, string key, string path, double min, double max, string unit, List<string> errors) {
        if (!o.ContainsKey(key)) return;
        if (o[key] is not JsonValue v || !v.TryGetValue<double>(out var value) || double.IsNaN(value) || double.IsInfinity(value)) {
            errors.Add($"{path} must be a number");
            return;
        }
        var suffix = unit.Length == 0 ? "" : " " + unit;
        if (value < min) errors.Add($"{path} must be >= {StyleNormalizer.Fmt(min)}{suffix}");
        else if (value > max) errors.Add($"{path} must be <= {StyleNormalizer.Fmt(max)}{suffix}");
    }
}
