using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace ResumeBuilder;

// ---------------------------------------------------------------------------
// Style system regression harness: the style system, the two renderers, and the normalization and
// validation paths the style work touches. Compiles the real source files, writes only into a
// temporary folder, and restores the live prepared-request files it has to touch.
//
//   dotnet run --project tests\ResumeStyleTests
// ---------------------------------------------------------------------------

static class Program {
    static readonly List<string> Failures = new();
    static int Passed;
    static string TempRoot = "";

    static int Main() {
        TempRoot = Path.Combine(Path.GetTempPath(), "ResumeBuilderStyleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TempRoot);

        // The harness must not append to the user's real diagnostics.log.
        PerfLog.Enabled = false;
        Console.WriteLine("ResumeBuilder — style system regression tests");
        Console.WriteLine("Working folder: " + TempRoot);
        Console.WriteLine();

        try {
            Console.WriteLine("Backward compatibility");
            Test("a profile with no style object renders with the promV4.12 preset", OldJsonUsesDefaultStyle);
            Test("existing profile JSON still passes strict validation", OldJsonStillValidates);
            Test("plain descriptionLines strings still render as bullets", PlainBulletsStillWork);

            Console.WriteLine();
            Console.WriteLine("Style normalization");
            Test("overrides deep merge onto the requested preset", OverridesMergeOntoPreset);
            Test("an overridden palette recolours the headings that follow it", PaletteFlowsToHeadings);
            Test("each preset is complete and within its own limits", PresetsAreValid);
            Test("out-of-range values are clamped, never thrown", InvalidStyleIsClamped);
            Test("unsupported style properties are ignored with a warning", UnsupportedFieldsIgnored);
            Test("normalization is idempotent", NormalizationIsIdempotent);

            Console.WriteLine();
            Console.WriteLine("Strict style validation");
            Test("invalid font sizes fail validation", InvalidFontSizesFail);
            Test("invalid colors fail validation", InvalidColorsFail);
            Test("line spacing cannot fall below 1.0", LineSpacingFloorFails);
            Test("unsupported style properties fail validation", UnsupportedFieldsFail);
            Test("a valid style produces no validation errors", ValidStylePasses);

            Console.WriteLine();
            Console.WriteLine("Content rules");
            Test("the company heading is built from structured data", CompanyHeadingIsConstructed);
            Test("missing optional fields never leave a dangling separator", NoDanglingSeparators);
            Test("bullet emphasis is applied to the requested segments", BulletEmphasis);
            Test("a bullet whose every segment is bold renders regular", FullyBoldBulletDemoted);
            Test("the summary ignores requested inline bold", SummaryIgnoresBold);
            Test("individual skills are never bold", SkillsIgnoreBold);
            Test("education renders as ordinary body text", EducationHasNoEmphasis);

            Console.WriteLine();
            Console.WriteLine("DOCX rendering");
            Test("section and company headings use real Word styles", RealWordStyles);
            Test("the generated DOCX opens and is schema-valid", DocxOpensAndValidates);
            Test("style values reach the DOCX", StyleReachesDocx);
            Test("rendering the same JSON twice is byte-identical", RenderingIsDeterministic);

            Console.WriteLine();
            Console.WriteLine("PDF rendering and parity");
            Test("DOCX and PDF carry identical content", DocxPdfParity);
            Test("a PDF is produced for every preset font", PdfRendersForPresets);

            Console.WriteLine();
            Console.WriteLine("Output folder organization");
            Test("output lands in ResumeRoot\\<date>\\<Company> - <Role>", OutputFolderStructure);
            Test("jobs generated on the same day share one date folder", SameDayFolderIsReused);
            Test("regenerating the same job never overwrites", RepeatRunsNeverOverwrite);
            Test("invalid Windows characters are sanitized", InvalidFolderCharactersAreHandled);
            Test("resume-info.json records the job id and URL", MetadataContents);

            Console.WriteLine();
            Console.WriteLine("Generation workflow");
            Test("the effective style is written next to the documents", EffectiveStyleIsWritten);
            Test("a disabled document type is reported as a skip, not a failure", DisabledDocumentsAreSkipped);

            Console.WriteLine();
            Console.WriteLine("Profile pipeline regressions");
            Test("candidate profile normalization handles AI output variations", NormalizationVariations);
            Test("date ranges are split into startDate/endDate", DateNormalization);
            Test("unsupported schema fields are removed and reported", UnsupportedSchemaFieldsRemoved);
            Test("experience count and order are never changed", ExperienceOrderPreserved);
            Test("normalize -> strict validate -> save still works", NormalizeValidateSave);
            Test("a style block survives normalization, clamped", StyleSurvivesNormalization);
            Test("segmented bullets survive normalization", SegmentsSurviveNormalization);
            Test("strict validation rejects a malformed segmented bullet", MalformedSegmentsRejected);
            Test("prepared job payload generation still works", PreparedPayload);
            Test("canonical JSON serialization round-trips", JsonSerialization);

            Console.WriteLine();
            Console.WriteLine("Job application tracking");
            Test("a newly extracted task starts as Viewed", NewTaskStartsViewed);
            Test("a generated resume moves the job to Ready", ResumeGenerationMarksReady);
            Test("the user can change the status by hand", ManualStatusChanges);
            Test("stage timestamps follow the status", StatusTimestamps);
            Test("status survives saving and reloading", StatusSurvivesRestart);
            Test("tasks saved before tracking existed still load", LegacyTasksLoad);
            Test("statistics counts and rates are correct", StatisticsAreCorrect);
            Test("filtering by status returns the right tasks", FilteringByStatus);
            Test("only http(s) job links are ever opened", JobUrlIsValidated);
            Test("queue status and application status stay independent", StatusesStayIndependent);

            Console.WriteLine();
            Console.WriteLine("Tracking dashboard");
            Test("today / monthly / total application counts", ActivityCounts);
            Test("daily activity aggregation drives the chart", DailyAggregation);
            Test("the pipeline always shows all five stages", PipelineCounts);
            Test("adjacent-pair percentages are A / (A + B)", PairSharePercentages);
            Test("search and status filters combine", SearchAndFilters);
            Test("date filtering uses each job's tracking date", DateFiltering);
            Test("an exact calendar date filters to that day", ExactDateFiltering);
            Test("search spans company, role and job id together", SearchAcrossFields);
            Test("resume actions are safe when no resume exists", ResumeActionsAreSafe);
            Test("a resume already on disk is relinked to its job", ResumeRelinking);

            Console.WriteLine();
            Console.WriteLine("Job browser");
            Test("the job browser runs on its own WebView2 profile", JobBrowserProfileIsSeparate);
            Test("home is the site itself and logs carry no query strings", JobBrowserHomeAndLogging);
            Test("the extraction boundary returns data, not a JobTask", JobImportDataIsNotATask);

            Console.WriteLine();
            Console.WriteLine("One-job import contract");
            Test("job URLs normalize to one comparable form", JobUrlNormalization);
            Test("one job becomes one Viewed task", ImportOneCreatesOneViewedTask);
            Test("new tasks get a collision-safe internal id", InternalJobIdIsGenerated);
            Test("the internal id and company URL survive a reload", InternalIdAndCompanyUrlPersist);
            Test("duplicates are found by normalized job URL", DuplicatesAreFoundByJobUrl);
            Test("company, title, job URL and description are required", RequiredFieldsAreEnforced);
            Test("an input file holds exactly one job", IncomingReadsExactlyOneJob);
            Test("the Incoming folder imports one job per file", IncomingFolderImportsOneJobPerFile);
            Test("tasks saved before this contract still load", OldTasksStillLoad);
            Test("the Development sample jobs use the new contract", SampleJobsUseTheNewContract);

            Console.WriteLine();
            Console.WriteLine("Job browser: import current job");
            Test("the extractor reads one job from the page", ExtractorReadsOneJob);
            Test("it still works when the page publishes no JSON-LD", ExtractorWithoutJsonLd);
            Test("the description becomes clean plain text", ExtractorHtmlToText);
            Test("job pages are recognised and the canonical URL used", ExtractorRecognisesJobPages);
            Test("stale or missing page data is refused clearly", ExtractorRefusesBadPages);
            Test("script failures never surface as raw errors", ExtractorNeverRaisesRawScriptErrors);
            Test("the extractor cannot reach tasks or storage", ExtractorStaysOutOfStorage);

            Console.WriteLine();
            Console.WriteLine("Sample output");
            Test("a sample resume is generated from the documented contract", GenerateSample);
        } finally {
            Console.WriteLine();
            Console.WriteLine(new string('-', 70));
            Console.WriteLine($"{Passed} passed, {Failures.Count} failed");
            foreach (var failure in Failures) Console.WriteLine("  FAILED: " + failure);
            TryCleanup();
        }

        return Failures.Count == 0 ? 0 : 1;
    }

    // ---------- backward compatibility ----------

    static void OldJsonUsesDefaultStyle() {
        var resume = ResumeDocument.FromProfileFile(Fixture("resume-basic.json"));
        Equal("promV4.12", resume.Style.Preset, "preset");
        Equal(0, resume.StyleWarnings.Count, "style warnings");
        Equal("Arial", resume.Style.Fonts.Family, "font family");
        Equal(11.0, resume.Style.Body.FontSize, "body font size");
        Equal(12.5, resume.Style.SectionHeading.FontSize, "section heading font size");
        Equal("#1F4E79", resume.Style.SectionHeading.Color, "section heading colour");
        Check(resume.Style.SectionHeading.Uppercase, "the default section heading should be uppercase");
    }

    static void OldJsonStillValidates() {
        var errors = CandidateProfileStore.ValidateResumeJson(File.ReadAllText(Fixture("resume-basic.json")));
        Check(errors.Count == 0, "expected no errors, got: " + string.Join(" | ", errors));
    }

    static void PlainBulletsStillWork() {
        var resume = ResumeDocument.FromProfileFile(Fixture("resume-basic.json"));
        var first = resume.Experience[0];
        Equal(2, first.Lines.Count, "bullet count");
        Check(!first.Lines[0].HasEmphasis, "a plain string bullet must carry no emphasis");
        Equal("Architected production AI services handling 40 million daily inference requests.",
              first.Lines[0].Text, "bullet text");
    }

    // ---------- style normalization ----------

    static void OverridesMergeOntoPreset() {
        var style = StyleOf("resume-custom-style.json");
        Equal("promV4.12", style.Preset, "preset");
        Equal("#17365D", style.Colors.Primary, "overridden primary colour");
        Equal("Calibri", style.Fonts.Family, "overridden font family");
        Equal(0.75, style.Page.MarginLeft, "overridden left margin");
        Equal(0.45, style.Page.MarginTop, "untouched top margin keeps the preset value");
        Equal(0.25, style.Bullet.LeftIndent, "overridden bullet indent");
        Equal(0.14, style.Bullet.HangingIndent, "untouched hanging indent keeps the preset value");
        Check(!style.SectionHeading.Uppercase, "uppercase was overridden to false");
        Check(!style.SectionHeading.BottomBorder, "bottomBorder was overridden to false");
        Equal(12.5, style.SectionHeading.FontSize, "untouched heading size keeps the preset value");
    }

    static void PaletteFlowsToHeadings() {
        var style = StyleOf("resume-custom-style.json");
        Equal("#17365D", style.SectionHeading.Color, "section heading follows the primary colour");
        Equal("#17365D", style.CompanyHeading.Color, "company heading follows the primary colour");
        Equal("#17365D", style.SkillCategory.Color, "skill category follows the primary colour");
        Equal("#000000", style.Body.Color, "body keeps the body colour");

        // An explicit section colour still wins over the palette.
        var explicitColor = StyleNormalizer.Normalize(JsonNode.Parse(
            """{ "colors": { "primary": "#17365D" }, "sectionHeading": { "color": "#AA0000" } }""")).Style;
        Equal("#AA0000", explicitColor.SectionHeading.Color, "an explicit section colour");
    }

    static void PresetsAreValid() {
        foreach (var name in StylePresets.Names) {
            var style = StylePresets.Get(name);
            Equal(name, style.Preset, "preset name");
            Check(style.Body.FontSize >= StyleLimits.MinBodyFontSize, $"{name}: body font size below the floor");
            Check(style.Bullet.FontSize >= StyleLimits.MinBodyFontSize, $"{name}: bullet font size below the floor");
            Check(style.SkillValues.FontSize >= StyleLimits.MinSkillValueFontSize, $"{name}: skill values below the floor");
            Check(StyleNormalizer.IsHexColor(style.Colors.Primary), $"{name}: invalid primary colour");
            Check(StyleLimits.FontFamilies.Contains(style.Fonts.Family), $"{name}: font family not allowed");

            foreach (var section in StyleSchema.Sections) {
                var text = style.Section(section);
                Check(text.LineSpacing >= StyleLimits.MinLineSpacing, $"{name}.{section}: line spacing below 1.0");
                Check(text.FontSize is >= StyleLimits.MinFontSize and <= StyleLimits.MaxFontSize, $"{name}.{section}: font size out of range");
                Check(StyleNormalizer.IsHexColor(text.Color), $"{name}.{section}: invalid colour");
            }

            // A preset must survive its own strict validation.
            var errors = StyleValidator.Validate(style.ToJson());
            Check(errors.Count == 0, $"{name}: {string.Join(" | ", errors)}");
        }
    }

    static void InvalidStyleIsClamped() {
        var resume = ResumeDocument.FromProfileFile(Fixture("resume-invalid-style.json"));
        var style = resume.Style;

        Equal("promV4.12", style.Preset, "an unknown preset falls back to the default");
        Equal(11.0, style.Body.FontSize, "9 pt body text was raised to the floor");
        Equal(24.0, style.Name.FontSize, "40 pt name was lowered to the maximum");
        Equal(10.5, style.SkillValues.FontSize, "8 pt skill values were raised to the floor");
        Equal(1.0, style.Bullet.LineSpacing, "0.8 line spacing was raised to 1.0");
        Equal(1.25, style.Page.MarginLeft, "a 2 inch margin was lowered to the maximum");
        Equal("#1F4E79", style.Colors.Primary, "an invalid colour keeps the preset value");
        Equal("LETTER", style.Page.Size, "an unsupported page size keeps the preset value");
        Equal("Arial", style.Fonts.Family, "a disallowed font keeps the preset value");
        Equal("center", style.Name.Alignment, "an invalid alignment keeps the preset value");
        Check(!style.SkillValues.Bold, "skill values must never be bold");
        Check(resume.StyleWarnings.Count >= 10, "every correction should be reported, got " + resume.StyleWarnings.Count);
    }

    static void UnsupportedFieldsIgnored() {
        var resume = ResumeDocument.FromProfileFile(Fixture("resume-invalid-style.json"));
        Check(resume.StyleWarnings.Any(w => w.Contains("style.sidebar")), "an unknown section should be reported");
        Check(resume.StyleWarnings.Any(w => w.Contains("dropShadow")), "an unknown property should be reported");
        var json = resume.Style.ToJson();
        Check(json["sidebar"] is null, "an unknown section must not reach the effective style");
        Check(json["sectionHeading"]!.AsObject()["dropShadow"] is null, "an unknown property must not reach the effective style");
    }

    static void NormalizationIsIdempotent() {
        foreach (var fixture in new[] { "resume-prom-v4.12.json", "resume-custom-style.json", "resume-invalid-style.json" }) {
            var once = StyleOf(fixture);
            var twice = StyleNormalizer.Normalize(once.ToJson()).Style;
            Equal(once.ToJsonString(), twice.ToJsonString(), fixture + ": normalizing an effective style must change nothing");
        }
    }

    // ---------- strict style validation ----------

    static void InvalidFontSizesFail() {
        var errors = StyleErrors("resume-invalid-style.json");
        Check(errors.Contains("style.body.fontSize must be >= 11 pt"), "expected the body font size error, got: " + Join(errors));
        Check(errors.Contains("style.name.fontSize must be <= 24 pt"), "expected the name font size error, got: " + Join(errors));
        Check(errors.Contains("style.skillValues.fontSize must be >= 10.5 pt"), "expected the skill values error, got: " + Join(errors));
    }

    static void InvalidColorsFail() {
        var errors = StyleErrors("resume-invalid-style.json");
        Check(errors.Contains("style.colors.primary is not a valid hex color (#RRGGBB)"), "expected the primary colour error, got: " + Join(errors));
        Check(errors.Contains("style.colors.body is not a valid hex color (#RRGGBB)"), "expected the three-digit colour to be rejected, got: " + Join(errors));
    }

    static void LineSpacingFloorFails() {
        var errors = StyleErrors("resume-invalid-style.json");
        Check(errors.Contains("style.bullet.lineSpacing must be >= 1"), "expected the line spacing error, got: " + Join(errors));
    }

    static void UnsupportedFieldsFail() {
        var errors = StyleErrors("resume-invalid-style.json");
        Check(errors.Contains("style.sidebar is not a supported style property"), "expected the unknown section error, got: " + Join(errors));
        Check(errors.Contains("style.sectionHeading.dropShadow is not a supported style property on sectionHeading"),
              "expected the unknown property error, got: " + Join(errors));
        Check(errors.Any(e => e.StartsWith("style.preset must be one of")), "expected the preset error, got: " + Join(errors));
        Check(errors.Any(e => e.StartsWith("style.page.size must be one of")), "expected the page size error, got: " + Join(errors));
        Check(errors.Any(e => e.StartsWith("style.fonts.family must be one of")), "expected the font family error, got: " + Join(errors));
        Check(errors.Any(e => e.StartsWith("style.name.alignment must be one of")), "expected the alignment error, got: " + Join(errors));
        Check(errors.Contains("style.skillValues.bold must be false — individual skills are always regular weight"),
              "expected the bold rule error, got: " + Join(errors));
    }

    static void ValidStylePasses() {
        foreach (var fixture in new[] { "resume-basic.json", "resume-prom-v4.12.json", "resume-custom-style.json", "resume-rich-bullets.json" }) {
            var errors = CandidateProfileStore.ValidateResumeJson(File.ReadAllText(Fixture(fixture)));
            Check(errors.Count == 0, fixture + ": " + Join(errors));
        }
    }

    // ---------- content rules ----------

    static void CompanyHeadingIsConstructed() {
        var resume = ResumeDocument.FromProfileFile(Fixture("resume-prom-v4.12.json"));
        Equal("Caterpillar Inc. | Senior AI Software Engineer | Sep 2025 - Present",
              resume.Experience[0].Heading, "company heading");
        Equal("Netflix | Software Engineer | Mar 2021 - Aug 2025",
              resume.Experience[1].Heading, "company heading");
        Equal("Peoria, IL | Full-time | Hybrid", resume.Experience[0].Metadata, "metadata line");

        // The rendered document must contain exactly that string.
        var paragraphs = ReadDocx(Render("resume-prom-v4.12.json", "heading"));
        Check(paragraphs.Any(p => p.Text == "Caterpillar Inc. | Senior AI Software Engineer | Sep 2025 - Present"),
              "the constructed heading should appear in the DOCX");
    }

    static void NoDanglingSeparators() {
        var resume = ResumeDocument.FromProfileFile(Fixture("resume-custom-style.json"));
        Equal("California | Full-time", resume.Experience[0].Metadata, "metadata with one empty value");
        Equal("", resume.Experience[1].Metadata, "metadata with every value empty");
        Equal("Netflix | Software Engineer | Mar 2021 - Aug 2025", resume.Experience[1].Heading, "heading is unaffected");

        foreach (var line in resume.ContentLines()) {
            Check(!line.Contains("| |"), "empty value left an empty column: " + line);
            Check(!line.TrimEnd().EndsWith("|"), "line ends with a separator: " + line);
            Check(!line.TrimStart().StartsWith("|"), "line starts with a separator: " + line);
        }

        var paragraphs = ReadDocx(Render("resume-custom-style.json", "separators"));
        Check(paragraphs.All(p => !p.Text.Contains("| |") && !p.Text.TrimEnd().EndsWith("|")),
              "a rendered paragraph carries a dangling separator");
    }

    static void BulletEmphasis() {
        var resume = ResumeDocument.FromProfileFile(Fixture("resume-rich-bullets.json"));
        var bullet = resume.Experience[0].Lines[0];
        Equal(3, bullet.Segments.Count, "segment count");
        Equal("Architected production Generative AI and RAG services using Python and AWS, serving 40 million daily requests.",
              bullet.Text, "reassembled bullet text");
        Check(!bullet.Segments[0].Bold && bullet.Segments[1].Bold && !bullet.Segments[2].Bold, "sparse emphasis");

        var paragraphs = ReadDocx(Render("resume-rich-bullets.json", "emphasis"));
        var rendered = paragraphs.First(p => p.Text.StartsWith("• Architected production"));
        var bolded = rendered.Runs.Where(r => r.Bold).Select(r => r.Text).ToList();
        Equal(1, bolded.Count, "one bold run");
        Equal("Generative AI and RAG services", bolded[0], "the bold run's text");
        Check(rendered.Runs.First().Text == ResumeDocument.Bullet && !rendered.Runs.First().Bold,
              "the bullet character itself is never bold");
    }

    static void FullyBoldBulletDemoted() {
        var resume = ResumeDocument.FromProfileFile(Fixture("resume-rich-bullets.json"));
        var bullet = resume.Experience[0].Lines[2];
        Check(!bullet.HasEmphasis, "a fully bold bullet must render regular");

        var paragraphs = ReadDocx(Render("resume-rich-bullets.json", "allbold"));
        var rendered = paragraphs.First(p => p.Text.Contains("Every segment of this line"));
        Check(rendered.Runs.All(r => !r.Bold), "no run of a fully bold bullet may stay bold");
    }

    static void SummaryIgnoresBold() {
        var path = WithStyle("resume-basic.json", """{ "preset": "promV4.12", "body": { "bold": true } }""", "summary-bold.json");
        var resume = ResumeDocument.FromProfileFile(path);
        Check(!resume.Style.Body.Bold, "body bold must be forced off");
        Check(resume.StyleWarnings.Any(w => w.Contains("style.body.bold")), "the correction must be reported");

        var summary = ReadDocx(RenderFile(path, "summary")).First(p => p.Text.StartsWith("Senior engineer with twelve years"));
        Check(summary.Runs.All(r => !r.Bold), "the summary must contain no bold run");
    }

    static void SkillsIgnoreBold() {
        var path = WithStyle("resume-basic.json", """{ "preset": "promV4.12", "skillValues": { "bold": true } }""", "skills-bold.json");
        var resume = ResumeDocument.FromProfileFile(path);
        Check(!resume.Style.SkillValues.Bold, "skill values bold must be forced off");

        var paragraphs = ReadDocx(RenderFile(path, "skills"));
        var values = paragraphs.First(p => p.Text.StartsWith("Python, Go, TypeScript"));
        Check(values.Runs.All(r => !r.Bold), "skill values must contain no bold run");

        var category = paragraphs.First(p => p.Text == "Languages");
        Check(category.Runs.Any(r => r.Bold), "the skill category itself may stay bold");
    }

    static void EducationHasNoEmphasis() {
        var paragraphs = ReadDocx(Render("resume-prom-v4.12.json", "education"));
        var index = paragraphs.FindIndex(p => p.Text == ResumeDocument.EducationHeading.ToUpperInvariant());
        Check(index >= 0, "the education heading should be present");
        foreach (var paragraph in paragraphs.Skip(index + 1))
            Check(paragraph.Runs.All(r => !r.Bold), "education must carry no emphasis: " + paragraph.Text);
    }

    // ---------- DOCX ----------

    static void RealWordStyles() {
        var path = Render("resume-prom-v4.12.json", "wordstyles");

        using (var word = WordprocessingDocument.Open(path, false)) {
            var styles = word.MainDocumentPart!.StyleDefinitionsPart!.Styles!;
            foreach (var (id, name, outline) in new[] { ("Heading1", "heading 1", 0), ("Heading2", "heading 2", 1), ("Heading3", "heading 3", 2) }) {
                var style = styles.Elements<W.Style>().FirstOrDefault(s => s.StyleId == id)
                            ?? throw new Exception(id + " is missing from the styles part");
                Equal(name, style.StyleName!.Val!.Value, id + " style name");
                Equal(outline, style.StyleParagraphProperties?.OutlineLevel?.Val?.Value, id + " outline level");
            }
            Check(styles.Elements<W.Style>().Any(s => s.StyleId == "Normal" && s.Default?.Value == true), "Normal must be the default style");
        }

        var paragraphs = ReadDocx(path);
        foreach (var heading in new[] { ResumeDocument.SummaryHeading, ResumeDocument.SkillsHeading, ResumeDocument.ExperienceHeading, ResumeDocument.EducationHeading })
            Equal("Heading1", paragraphs.First(p => p.Text == heading.ToUpperInvariant()).StyleId, heading + " style");

        Equal("Heading2", paragraphs.First(p => p.Text.StartsWith("Caterpillar Inc. |")).StyleId, "company heading style");
        Equal("Heading2", paragraphs.First(p => p.Text == "Languages").StyleId, "skill category style");
        Equal("Heading3", paragraphs.First(p => p.Text == "Predictive Maintenance Platform").StyleId, "subtitle style");
    }

    static void DocxOpensAndValidates() {
        foreach (var fixture in new[] { "resume-basic.json", "resume-prom-v4.12.json", "resume-custom-style.json", "resume-rich-bullets.json", "resume-invalid-style.json" }) {
            var path = Render(fixture, "valid");
            using var word = WordprocessingDocument.Open(path, false);
            var errors = new OpenXmlValidator(FileFormatVersions.Office2019).Validate(word).ToList();
            Check(errors.Count == 0, fixture + ": " + string.Join(" | ", errors.Take(3).Select(e => e.Description)));
            Check(word.MainDocumentPart!.Document!.Body!.Elements<W.Paragraph>().Any(), fixture + ": the document is empty");
        }
    }

    static void StyleReachesDocx() {
        var paragraphs = ReadDocx(Render("resume-prom-v4.12.json", "values"));

        var name = paragraphs[0];
        Equal(17.0, name.FontSize, "name font size");
        Equal("center", name.Alignment.ToLowerInvariant(), "name alignment");
        Equal("Arial", name.FontFamily, "name font family");

        var heading = paragraphs.First(p => p.Text == ResumeDocument.SummaryHeading.ToUpperInvariant());
        Equal(12.5, heading.FontSize, "section heading font size");
        Equal("1F4E79", heading.Color, "section heading colour");
        Check(heading.BottomBorder, "the section heading should carry a bottom border");

        var bullet = paragraphs.First(p => p.Text.StartsWith("• Architected"));
        Equal(11.0, bullet.FontSize, "bullet font size");
        Equal(0.18, Math.Round(bullet.LeftIndentInches, 2), "bullet left indent");

        // The overriding fixture must differ in exactly the ways it asked to.
        var custom = ReadDocx(Render("resume-custom-style.json", "values-custom"));
        Equal("Calibri", custom[0].FontFamily, "overridden font family reaches the DOCX");

        // uppercase was switched off, so the heading keeps its title case.
        var customHeading = custom.First(p => p.Text == ResumeDocument.SummaryHeading);
        Equal("17365D", customHeading.Color, "overridden colour reaches the DOCX");
        Check(!customHeading.BottomBorder, "the border was switched off");
    }

    static void RenderingIsDeterministic() {
        var resume = ResumeDocument.FromProfileFile(Fixture("resume-prom-v4.12.json"));
        var first = Path.Combine(TempRoot, "deterministic-1.docx");
        var second = Path.Combine(TempRoot, "deterministic-2.docx");
        DocxWriter.Write(resume, first);
        DocxWriter.Write(resume, second);

        foreach (var part in new[] { "word/document.xml", "word/styles.xml" })
            Equal(Convert.ToBase64String(PartBytes(first, part)), Convert.ToBase64String(PartBytes(second, part)),
                  part + " must be identical across renders");

        // And the same must hold when the document model is rebuilt from the same file.
        var rebuilt = Path.Combine(TempRoot, "deterministic-3.docx");
        DocxWriter.Write(ResumeDocument.FromProfileFile(Fixture("resume-prom-v4.12.json")), rebuilt);
        Equal(Convert.ToBase64String(PartBytes(first, "word/document.xml")),
              Convert.ToBase64String(PartBytes(rebuilt, "word/document.xml")), "a rebuilt model renders identically");
    }

    // ---------- PDF ----------

    static void DocxPdfParity() {
        foreach (var fixture in new[] { "resume-basic.json", "resume-prom-v4.12.json", "resume-custom-style.json", "resume-rich-bullets.json" }) {
            var resume = ResumeDocument.FromProfileFile(Fixture(fixture));
            var expected = resume.ContentLines().ToList();

            var docx = ReadDocx(Render(fixture, "parity")).Select(p => p.Text).Where(t => t.Length > 0).ToList();
            Equal(string.Join("\n", expected), string.Join("\n", docx), fixture + ": DOCX content");

            var pdf = PdfWriter.ExtractText(PdfWriter.BuildDocument(resume));
            Equal(string.Join("\n", expected), string.Join("\n", pdf), fixture + ": PDF content");
        }
    }

    static void PdfRendersForPresets() {
        foreach (var preset in StylePresets.Names) {
            var path = WithStyle("resume-prom-v4.12.json", $$"""{ "preset": "{{preset}}" }""", "preset-" + preset + ".json");
            var resume = ResumeDocument.FromProfileFile(path);
            Equal(preset, resume.Style.Preset, "preset applied");

            var pdf = Path.Combine(TempRoot, "preset-" + preset.Replace(".", "") + ".pdf");
            PdfWriter.Write(resume, pdf);
            Check(File.Exists(pdf) && new FileInfo(pdf).Length > 1000, preset + ": no usable PDF was produced");
        }
    }

    // ---------- generation workflow ----------

    static void OutputFolderStructure() {
        var root = NewDir("structure");
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var settings = new AppSettings { ResumeRootFolder = root, Docx = true, Pdf = true };

        var result = ResumeGenerator.Generate("Caterpillar Inc.", "Senior AI Software Engineer",
                                              Fixture("resume-prom-v4.12.json"), settings, null,
                                              "STRESS-114209-001", "https://example.com/job/123");

        Check(!result.AnyFailure, "the documents should generate: " + result.Describe());

        var expectedFolder = Path.Combine(root, today, "Caterpillar Inc - Senior AI Software Engineer");
        Equal(expectedFolder, result.OutputFolder, "job folder");
        Equal(Path.Combine(expectedFolder, "Resume.docx"), result.DocxPath, "DOCX path");
        Equal(Path.Combine(expectedFolder, "Resume.pdf"), result.PdfPath, "PDF path");
        Equal(Path.Combine(expectedFolder, "resume-info.json"), result.MetadataPath, "metadata path");

        Check(File.Exists(result.DocxPath!), "Resume.docx should exist");
        Check(File.Exists(result.PdfPath!), "Resume.pdf should exist");
        Check(File.Exists(result.MetadataPath!), "resume-info.json should exist");
        Equal(0, Directory.GetFiles(root).Length, "nothing may be written loose in the Resume Root");
        Equal(1, Directory.GetDirectories(root).Length, "exactly one date folder");
    }

    static void SameDayFolderIsReused() {
        var root = NewDir("sameday");
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var settings = new AppSettings { ResumeRootFolder = root, Docx = true, Pdf = false };

        ResumeGenerator.Generate("Caterpillar Inc.", "Senior AI Software Engineer", Fixture("resume-basic.json"), settings, null, "JOB-1");
        ResumeGenerator.Generate("Tesla", "Machine Learning Engineer", Fixture("resume-basic.json"), settings, null, "JOB-2");
        ResumeGenerator.Generate("Netflix", "Software Engineer", Fixture("resume-basic.json"), settings, null, "JOB-3");

        Equal(1, Directory.GetDirectories(root).Length, "three jobs on one day share one date folder");
        var dateFolder = Path.Combine(root, today);
        Check(Directory.Exists(dateFolder), "the date folder should be named " + today);
        Equal(3, Directory.GetDirectories(dateFolder).Length, "one job folder each");
        Check(Directory.Exists(Path.Combine(dateFolder, "Tesla - Machine Learning Engineer")), "the Tesla folder should exist");

        // A different day is a different folder, and the same job folder name is reused inside it.
        var yesterday = ResumeOutputManager.GetJobFolder(root, "Tesla", "Machine Learning Engineer", DateTime.Now.AddDays(-1));
        Equal(Path.Combine(root, DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd"), "Tesla - Machine Learning Engineer"),
              yesterday, "a previous day resolves to its own folder");
    }

    static void RepeatRunsNeverOverwrite() {
        var root = NewDir("norewrite");
        var settings = new AppSettings { ResumeRootFolder = root, Docx = true, Pdf = true };

        var first = ResumeGenerator.Generate("Caterpillar Inc.", "Senior AI Software Engineer", Fixture("resume-basic.json"), settings, null, "JOB-1");
        var second = ResumeGenerator.Generate("Caterpillar Inc.", "Senior AI Software Engineer", Fixture("resume-basic.json"), settings, null, "JOB-1");
        var third = ResumeGenerator.Generate("Caterpillar Inc.", "Senior AI Software Engineer", Fixture("resume-basic.json"), settings, null, "JOB-1");

        Equal(first.OutputFolder, second.OutputFolder, "the same job reuses its folder");
        Equal("Resume.docx", Path.GetFileName(first.DocxPath!), "first run");
        Equal("Resume (2).docx", Path.GetFileName(second.DocxPath!), "second run");
        Equal("Resume (3).docx", Path.GetFileName(third.DocxPath!), "third run");

        // The whole set moves together, so a pair is never split across revisions.
        Equal("Resume (2).pdf", Path.GetFileName(second.PdfPath!), "second run PDF");
        Equal("resume-info (2).json", Path.GetFileName(second.MetadataPath!), "second run metadata");

        var files = Directory.GetFiles(first.OutputFolder!);
        Equal(9, files.Length, "three runs leave three complete sets");
        foreach (var path in new[] { first.DocxPath!, second.DocxPath!, third.DocxPath! })
            Check(new FileInfo(path).Length > 0, "every revision should be a real file: " + path);
    }

    static void InvalidFolderCharactersAreHandled() {
        Equal("Acme Corp", ResumeOutputManager.SanitizeFolderName("Acme/Corp"), "slash");
        // Only the invalid set is touched: "!" is legal on Windows, the trailing dot is not.
        Equal("Yahoo! Inc", ResumeOutputManager.SanitizeFolderName("Yahoo! Inc."), "trailing dot removed, legal punctuation kept");
        Equal("A B", ResumeOutputManager.SanitizeFolderName("A\\B"), "backslash");
        Equal("Q A", ResumeOutputManager.SanitizeFolderName("Q:A"), "colon");
        Equal("x y", ResumeOutputManager.SanitizeFolderName("x*?\"<>|y"), "the full invalid set collapses to one space");
        Equal("", ResumeOutputManager.SanitizeFolderName("   "), "whitespace only");
        Equal("", ResumeOutputManager.SanitizeFolderName(null), "null");

        Equal("Acme Corp - Engineer", ResumeOutputManager.JobFolderName("Acme/Corp", "Engineer"), "job folder name");
        Equal("Resume", ResumeOutputManager.JobFolderName("", ""), "empty company and role");
        Equal("Engineer", ResumeOutputManager.JobFolderName("", "Engineer"), "company only missing");
        Check(ResumeOutputManager.JobFolderName(new string('C', 200), "Engineer").Length <= 120, "long names are bounded");

        // And it survives a real generation with a hostile company name.
        var root = NewDir("hostile");
        var settings = new AppSettings { ResumeRootFolder = root, Docx = true, Pdf = false };
        var result = ResumeGenerator.Generate("A/B:C*D?E\"F<G>H|I", "Senior Engineer.", Fixture("resume-basic.json"), settings, null, "JOB-X");

        Check(!result.AnyFailure, "a hostile name must still generate: " + result.Describe());
        Equal("A B C D E F G H I - Senior Engineer", Path.GetFileName(result.OutputFolder!), "sanitized folder");
        Check(File.Exists(result.DocxPath!), "the document should exist on disk");
    }

    static void MetadataContents() {
        var root = NewDir("metadata");
        var settings = new AppSettings { ResumeRootFolder = root, Docx = true, Pdf = true };

        var result = ResumeGenerator.Generate("Caterpillar Inc.", "Senior AI Software Engineer",
                                              Fixture("resume-basic.json"), settings, null,
                                              "STRESS-114209-001", "https://example.com/job/123");

        var metadata = JsonNode.Parse(File.ReadAllText(result.MetadataPath!))!.AsObject();
        Equal("STRESS-114209-001", metadata["jobId"]!.GetValue<string>(), "jobId");
        Equal("https://example.com/job/123", metadata["jobUrl"]!.GetValue<string>(), "jobUrl");
        Equal("Caterpillar Inc.", metadata["company"]!.GetValue<string>(), "company keeps its original punctuation");
        Equal("Senior AI Software Engineer", metadata["role"]!.GetValue<string>(), "role");
        Equal("Resume.docx", metadata["docxFile"]!.GetValue<string>(), "docxFile");
        Equal("Resume.pdf", metadata["pdfFile"]!.GetValue<string>(), "pdfFile");

        var generatedAt = metadata["generatedAt"]!.GetValue<string>();
        Check(DateTime.TryParse(generatedAt, out _), "generatedAt should be a timestamp: " + generatedAt);
        Equal(19, generatedAt.Length, "generatedAt format yyyy-MM-ddTHH:mm:ss");

        // A disabled document type is recorded as null rather than a filename that does not exist.
        var docxOnly = new AppSettings { ResumeRootFolder = NewDir("metadata-docx"), Docx = true, Pdf = false };
        var second = ResumeGenerator.Generate("Tesla", "ML Engineer", Fixture("resume-basic.json"), docxOnly, null, "JOB-2", "");
        var record = JsonNode.Parse(File.ReadAllText(second.MetadataPath!))!.AsObject();
        Equal("Resume.docx", record["docxFile"]!.GetValue<string>(), "docxFile");
        Check(record["pdfFile"] is null, "a document that was not requested has no filename");
    }

    static void EffectiveStyleIsWritten() {
        var root = NewDir("effective");
        var stylePath = Path.Combine(root, "effective-style.json");
        var settings = new AppSettings { ResumeRootFolder = root, Docx = true, Pdf = false };

        var result = ResumeGenerator.Generate("Caterpillar Inc.", "Senior AI Software Engineer",
                                              Fixture("resume-invalid-style.json"), settings, stylePath);
        Check(result.DocxGenerated, "a bad style must never stop the document: " + result.Describe());
        Equal(stylePath, result.EffectiveStylePath, "effective style path");
        Check(File.Exists(stylePath), "effective-style.json should exist");
        Check(result.StyleWarnings.Count > 0, "the corrections should be reported");
        Check(result.Describe().Contains("style value"), "the status line should mention the adjustments");

        var written = JsonNode.Parse(File.ReadAllText(stylePath))!.AsObject();
        Equal("promV4.12", written["preset"]!.GetValue<string>(), "written preset");
        Equal(11.0, written["body"]!["fontSize"]!.GetValue<double>(), "the written style carries the clamped value");
        Check(StyleValidator.Validate(written).Count == 0, "the effective style must itself be valid");
    }

    static void DisabledDocumentsAreSkipped() {
        var settings = new AppSettings { ResumeRootFolder = NewDir("disabled"), Docx = false, Pdf = false };
        var result = ResumeGenerator.Generate("Acme", "Engineer", Fixture("resume-basic.json"), settings);
        Check(result.Disabled, "both types off means disabled");
        Check(!result.AnyFailure, "disabled is not a failure");
        Check(result.Describe().Contains("disabled"), "the status line should say so");
    }

    // ---------- profile pipeline ----------

    static void NormalizationVariations() {
        var report = ProfileNormalizer.Normalize(ProfileNormalizer.Parse("""
        {
          "contact": { "fullName": "Billy Lin", "headline": "Engineer", "mail": "[billy@example.com](mailto:billy@example.com)" },
          "professionalSummary": ["Half a sentence.", "And the rest."],
          "technicalSkills": { "Languages": ["Python", "Go"] },
          "workExperience": [
            { "position": "Engineer", "employer": "Acme", "dates": "Sep 2025 - Present",
              "bullets": ["Did the thing."], "employment_type": "Full-time", "work_mode": "Remote" }
          ],
          "education": [ { "qualification": "B.S.", "institution": "UIUC", "graduated": "2013" } ]
        }
        """));

        var profile = report.Profile;
        Equal("Billy Lin", profile["info"]!["name"]!.GetValue<string>(), "name alias");
        Equal("billy@example.com", profile["info"]!["email"]!.GetValue<string>(), "Markdown email link cleaned");
        Equal("Half a sentence. And the rest.", profile["summary"]!.GetValue<string>(), "summary array joined");

        var skills = profile["skills"]!.AsArray();
        Equal(1, skills.Count, "skills category count");
        Equal("Languages", skills[0]!["category"]!.GetValue<string>(), "skills object converted to an array");

        var job = profile["experience"]!.AsArray()[0]!;
        Equal("Engineer", job["title"]!.GetValue<string>(), "title alias");
        Equal("Acme", job["company"]!.GetValue<string>(), "company alias");
        Equal("Sep 2025", job["startDate"]!.GetValue<string>(), "start date from a combined range");
        Equal("Present", job["endDate"]!.GetValue<string>(), "end date from a combined range");
        Equal("Full-time", job["employmentType"]!.GetValue<string>(), "employment_type mapped to employmentType");
        Equal("Remote", job["workArrangement"]!.GetValue<string>(), "work_mode mapped to workArrangement");
        Equal("Did the thing.", job["descriptionLines"]!.AsArray()[0]!.GetValue<string>(), "bullets mapped to descriptionLines");

        var school = profile["education"]!.AsArray()[0]!;
        Equal("UIUC", school["school"]!.GetValue<string>(), "institution mapped to school");
        Equal("2013", school["endDate"]!.GetValue<string>(), "graduated mapped to endDate");

        CandidateProfileStore.Validate(JsonDocument.Parse(ProfileNormalizer.ToCanonicalJson(profile)).RootElement);

        // A canonical profile wrapped in an envelope is unwrapped rather than rejected.
        var wrapped = ProfileNormalizer.Normalize(ProfileNormalizer.Parse("""
        { "profile": { "info": { "name": "Billy Lin" }, "summary": "Wrapped.", "skills": [], "experience": [], "certifications": [], "education": [] } }
        """));
        Equal("Billy Lin", wrapped.Profile["info"]!["name"]!.GetValue<string>(), "a wrapped profile is unwrapped");
        Check(wrapped.Changes.Any(c => c.Contains("unwrapped")), "the unwrap should be reported");
    }

    static void DateNormalization() {
        foreach (var (raw, start, end) in new[] {
            ("Sep 2025 – Apr 2026", "Sep 2025", "Apr 2026"),
            ("Sep 2025 - Present", "Sep 2025", "Present"),
            ("Sep 2025 to Present", "Sep 2025", "Present"),
            ("Sep 2025-Apr 2026", "Sep 2025", "Apr 2026"),
            ("2013", "2013", "")
        }) {
            var split = ProfileNormalizer.SplitDateRange(raw);
            Equal(start, split.Start, raw + " start");
            Equal(end, split.End, raw + " end");
        }
    }

    static void UnsupportedSchemaFieldsRemoved() {
        var report = ProfileNormalizer.Normalize(ProfileNormalizer.Parse("""
        {
          "info": { "name": "Billy Lin", "twitter": "@billy" },
          "summary": "A summary.",
          "skills": [],
          "experience": [ { "title": "Engineer", "company": "Acme", "startDate": "2025", "endDate": "Present", "salary": "lots" } ],
          "certifications": [],
          "education": [],
          "coverLetter": "Dear hiring manager"
        }
        """));

        Check(report.Dropped.Contains("coverLetter"), "a non-schema top-level field should be dropped");
        Check(report.Dropped.Contains("info.twitter"), "a non-schema info field should be dropped");
        Check(report.Dropped.Contains("experience[0].salary"), "a non-schema experience field should be dropped");
        Check(report.Profile["coverLetter"] is null, "the dropped field must not survive");
    }

    static void ExperienceOrderPreserved() {
        var report = ProfileNormalizer.Normalize(ProfileNormalizer.Parse("""
        {
          "info": {}, "summary": "", "skills": [], "certifications": [], "education": [],
          "experience": [
            { "company": "First", "title": "A" },
            { "company": "Second", "title": "B" },
            { "company": "Third", "title": "C" }
          ]
        }
        """));

        var experience = report.Profile["experience"]!.AsArray();
        Equal(3, experience.Count, "experience count");
        Equal("First", experience[0]!["company"]!.GetValue<string>(), "first entry");
        Equal("Second", experience[1]!["company"]!.GetValue<string>(), "second entry");
        Equal("Third", experience[2]!["company"]!.GetValue<string>(), "third entry");
    }

    static void NormalizeValidateSave() {
        var target = Path.Combine(NewDir("pipeline"), "result.json");
        var answer = "Here you go:\n\n```json\n" + File.ReadAllText(Fixture("resume-prom-v4.12.json")) + "\n```\n";

        var report = CandidateProfileStore.NormalizeAndSaveTo(answer, target);
        Check(File.Exists(target), "the profile should have been saved");

        var saved = JsonNode.Parse(File.ReadAllText(target))!.AsObject();
        foreach (var field in new[] { "info", "summary", "skills", "experience", "certifications", "education" })
            Check(saved[field] is not null, "the saved profile is missing " + field);

        CandidateProfileStore.Validate(JsonDocument.Parse(File.ReadAllText(target)).RootElement);
        Check(report is not null, "a report should be returned");

        // A refused answer must leave nothing behind. Normalization is deliberately tolerant of AI
        // output variations, so what it refuses is text that is not a profile object at all.
        foreach (var bad in new[] { "no JSON here at all", "```json\n[ 1, 2, 3 ]\n```", "```json\n{ \"info\": { \n```" }) {
            var badTarget = Path.Combine(NewDir("pipeline-bad-" + Math.Abs(bad.GetHashCode())), "result.json");
            var threw = false;
            try { CandidateProfileStore.NormalizeAndSaveTo(bad, badTarget); }
            catch (InvalidDataException) { threw = true; }
            Check(threw, "a malformed answer must be refused: " + bad);
            Check(!File.Exists(badTarget), "a refused answer must save nothing");
        }
    }

    static void StyleSurvivesNormalization() {
        var target = Path.Combine(NewDir("style-pipeline"), "result.json");
        CandidateProfileStore.NormalizeAndSaveTo(File.ReadAllText(Fixture("resume-invalid-style.json")), target);

        var saved = JsonNode.Parse(File.ReadAllText(target))!.AsObject();
        var style = saved["style"]!.AsObject();
        Equal("promV4.12", style["preset"]!.GetValue<string>(), "the preset was corrected");
        Equal(11.0, style["body"]!["fontSize"]!.GetValue<double>(), "the body size was clamped");
        Check(style["sidebar"] is null, "an unsupported section must not be saved");
        Check(StyleValidator.Validate(style).Count == 0, "the saved style must be valid");

        // And the six canonical fields still come first.
        var order = saved.Select(p => p.Key).Take(6).ToArray();
        Equal("info,summary,skills,experience,certifications,education", string.Join(",", order), "top-level field order");
    }

    static void SegmentsSurviveNormalization() {
        var target = Path.Combine(NewDir("segments"), "result.json");
        CandidateProfileStore.NormalizeAndSaveTo(File.ReadAllText(Fixture("resume-rich-bullets.json")), target);

        var lines = JsonNode.Parse(File.ReadAllText(target))!["experience"]![0]!["descriptionLines"]!.AsArray();
        Equal(3, lines.Count, "line count");

        var segments = lines[0]!["segments"]!.AsArray();
        Equal(3, segments.Count, "segment count");
        Equal("Architected production ", segments[0]!["text"]!.GetValue<string>(), "segment spacing is preserved");
        Check(segments[1]!["bold"]!.GetValue<bool>(), "the emphasised segment stays bold");

        Check(lines[1]!.GetValueKind() == JsonValueKind.String, "a plain line stays a plain string");

        // A segmented line with no emphasis collapses back to plain text.
        var collapsed = ProfileNormalizer.Normalize(ProfileNormalizer.Parse("""
        {
          "info": {}, "summary": "", "skills": [], "certifications": [], "education": [],
          "experience": [ { "company": "Acme", "descriptionLines": [
            { "segments": [ { "text": "One ", "bold": false }, { "text": "line.", "bold": false } ] } ] } ]
        }
        """)).Profile;
        var line = collapsed["experience"]![0]!["descriptionLines"]![0]!;
        Equal(JsonValueKind.String, line.GetValueKind(), "an unemphasised segmented line collapses");
        Equal("One line.", line.GetValue<string>(), "the collapsed text");
    }

    static void MalformedSegmentsRejected() {
        var errors = CandidateProfileStore.ValidateResumeJson("""
        {
          "info": { "name": "Billy" }, "summary": "", "skills": [], "certifications": [], "education": [],
          "experience": [ { "title": "", "company": "", "startDate": "", "endDate": "", "location": "",
            "descriptionLines": [ { "segments": [ { "bold": true } ] }, { "text": "no segments" }, 42 ] } ]
        }
        """);
        Check(errors.Any(e => e.Contains("segments[0].text is missing")), "a segment without text should be rejected: " + Join(errors));
        Check(errors.Any(e => e.Contains("descriptionLines[1].segments is missing")), "an object without segments should be rejected: " + Join(errors));
        Check(errors.Any(e => e.Contains("descriptionLines[2] must be a string")), "a number should be rejected: " + Join(errors));
    }

    static void PreparedPayload() {
        // RequestPreparation writes into the live app data folder, so the real files are restored.
        var live = new[] { RequestPreparation.PreparedPath, RequestPreparation.PreparedTextPath };
        var backup = live.ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null);

        try {
            var dir = NewDir("prepare");
            var masterPrompt = Path.Combine(dir, "master-prompt.txt");
            File.WriteAllText(masterPrompt, "RESUME MASTER PROMPT v2\nTailor the profile to the job.");

            var profile = Path.Combine(dir, "candidate-profile.json");
            File.Copy(Fixture("resume-prom-v4.12.json"), profile);

            var settings = new AppSettings { MasterPrompt = masterPrompt, CandidateProfile = profile };
            var job = new JobTask { JobId = "TEST-1", Company = "Caterpillar Inc.", Title = "Senior AI Software Engineer", Jd = "Build AI services." };

            var prepared = RequestPreparation.Prepare(job, settings);

            Check(prepared.Text.Contains("RESUME MASTER PROMPT v2"), "the external master prompt should be loaded");
            Check(prepared.Text.Contains("===== COMPLETE JOB PAYLOAD ====="), "the payload marker should be present");
            Check(prepared.Text.Contains("Build AI services."), "the job description should be included");
            Equal("TEST-1", prepared.JobId, "job id");

            var payload = prepared.Text.Substring(prepared.Text.IndexOf('{'));
            payload = payload.Substring(0, payload.LastIndexOf('}') + 1);
            var parsed = JsonNode.Parse(payload)!.AsObject();
            Equal("Caterpillar Inc.", parsed["company"]!.GetValue<string>(), "payload company");
            Check(parsed["profile"]!["experience"]!.AsArray().Count == 2, "the profile should be embedded whole");
            Check(parsed["profile"]!["style"] is not null, "the style block should reach the payload");

            Check(File.Exists(RequestPreparation.PreparedPath), "the prepared request should be persisted");
            Equal(prepared.Text, File.ReadAllText(RequestPreparation.PreparedTextPath), "the text sidecar should match");
        } finally {
            foreach (var pair in backup) {
                if (pair.Value is byte[] content) File.WriteAllBytes(pair.Key, content);
                else if (File.Exists(pair.Key)) File.Delete(pair.Key);
            }
        }
    }

    static void JsonSerialization() {
        var original = File.ReadAllText(Fixture("resume-prom-v4.12.json"));
        var canonical = ProfileNormalizer.ToCanonicalJson(ProfileNormalizer.Normalize(ProfileNormalizer.Parse(original)).Profile);

        var reparsed = ProfileNormalizer.Parse(canonical);
        Equal(canonical, ProfileNormalizer.ToCanonicalJson(reparsed), "canonical JSON should round-trip unchanged");

        using var document = JsonDocument.Parse(canonical);
        CandidateProfileStore.Validate(document.RootElement);
        Check(canonical.Contains("\n"), "canonical JSON should be indented");
    }

    // ---------- job application tracking ----------

    static JobTask Job(string id, string company = "Acme", string title = "Engineer", string link = "") =>
        new() { JobId = id, Company = company, Title = title, Link = link, Jd = "..." };

    static void NewTaskStartsViewed() {
        var job = Job("NEW-1");
        Equal(ApplicationStatus.Viewed, job.ApplicationStatus, "a new task's application status");
        Equal("Queued", job.Status, "a new task's queue status");
        Check(!job.ResumeGenerated, "a new task has no resume yet");
        Check(job.CreatedAt > DateTime.Now.AddMinutes(-1), "createdAt should be stamped");
        Check(job.AppliedAt is null && job.InterviewAt is null && job.DoneAt is null, "no stage timestamps yet");

        // An unknown or empty status from any source is Viewed, never left as it was.
        Equal(ApplicationStatus.Viewed, ApplicationStatus.Normalize(""), "empty");
        Equal(ApplicationStatus.Viewed, ApplicationStatus.Normalize(null), "null");
        Equal(ApplicationStatus.Viewed, ApplicationStatus.Normalize("Rejected"), "unknown status");
        Equal(ApplicationStatus.Applied, ApplicationStatus.Normalize("applied"), "case-insensitive");
    }

    static void ResumeGenerationMarksReady() {
        var job = Job("READY-1");
        Check(JobTracker.MarkResumeReady(job, @"C:\Resumes\2026-09-17\Acme - Engineer\Resume.docx"), "the first generation should change the job");
        Equal(ApplicationStatus.Ready, job.ApplicationStatus, "status after generation");
        Check(job.ResumeGenerated, "the resume path should be recorded");
        Equal("Ready", job.ResumeStateDisplay, "the table column");

        // Regenerating changes nothing else.
        Check(!JobTracker.MarkResumeReady(job, @"C:\Resumes\2026-09-17\Acme - Engineer\Resume.docx"), "an identical regeneration is not a change");

        // And it must never walk a job backwards once the user has moved it on.
        JobTracker.UpdateStatus(job, ApplicationStatus.Applied);
        JobTracker.MarkResumeReady(job, @"C:\Resumes\2026-09-18\Acme - Engineer\Resume.docx");
        Equal(ApplicationStatus.Applied, job.ApplicationStatus, "an applied job stays applied");
        Equal(@"C:\Resumes\2026-09-18\Acme - Engineer\Resume.docx", job.ResumePath, "but the newer resume is recorded");

        // Nothing is automated past Ready.
        var untouched = Job("READY-2");
        JobTracker.MarkResumeReady(untouched, null);
        Equal(ApplicationStatus.Ready, untouched.ApplicationStatus, "Ready is the only automatic transition");
    }

    static void ManualStatusChanges() {
        var job = Job("MANUAL-1");
        foreach (var status in new[] { ApplicationStatus.Ready, ApplicationStatus.Applied, ApplicationStatus.Interview, ApplicationStatus.Done }) {
            Check(JobTracker.UpdateStatus(job, status), "moving to " + status + " should report a change");
            Equal(status, job.ApplicationStatus, "status");
        }

        Check(!JobTracker.UpdateStatus(job, ApplicationStatus.Done), "setting the same status again is not a change");

        // An unknown status is refused by being normalized, not by throwing.
        JobTracker.UpdateStatus(job, "Ghosted");
        Equal(ApplicationStatus.Viewed, job.ApplicationStatus, "an unknown status falls back to Viewed");
    }

    static void StatusTimestamps() {
        var job = Job("TIME-1");
        Check(job.ViewedAt is not null, "a new task is already Viewed, so viewedAt is stamped");
        Check(job.ReadyAt is null && job.AppliedAt is null, "no later stage is stamped yet");

        var ready = new DateTime(2026, 9, 17, 10, 0, 0);
        JobTracker.UpdateStatus(job, ApplicationStatus.Ready, ready);
        Equal(ready, job.ReadyAt, "readyAt");

        // Skipping a stage backfills it, so a job that jumped ahead still has a complete history.
        var skipper = Job("TIME-2");
        var interviewOnly = new DateTime(2026, 9, 18, 12, 0, 0);
        JobTracker.UpdateStatus(skipper, ApplicationStatus.Interview, interviewOnly);
        Equal(interviewOnly, skipper.AppliedAt, "a job jumped to Interview was certainly applied for");
        Equal(interviewOnly, skipper.ReadyAt, "and its resume was ready");
        Check(skipper.DoneAt is null, "but nothing later is invented");

        var applied = new DateTime(2026, 9, 17, 11, 0, 0);
        JobTracker.UpdateStatus(job, ApplicationStatus.Applied, applied);
        Equal(applied, job.AppliedAt, "appliedAt");
        Equal(applied, job.UpdatedAt, "updatedAt");
        Check(job.InterviewAt is null && job.DoneAt is null, "later stages are not stamped yet");

        var interview = new DateTime(2026, 9, 20, 9, 30, 0);
        JobTracker.UpdateStatus(job, ApplicationStatus.Interview, interview);
        Equal(applied, job.AppliedAt, "the original appliedAt is kept");
        Equal(interview, job.InterviewAt, "interviewAt");

        var done = new DateTime(2026, 9, 25, 16, 0, 0);
        JobTracker.UpdateStatus(job, ApplicationStatus.Done, done);
        Equal(done, job.DoneAt, "doneAt");

        // A stage that was genuinely reached stays on the record even if the user corrects the status,
        // and coming back to it does not restamp it with a later time.
        JobTracker.UpdateStatus(job, ApplicationStatus.Ready, new DateTime(2026, 9, 26, 8, 0, 0));
        Equal(applied, job.AppliedAt, "appliedAt survives moving back");
        Equal(interview, job.InterviewAt, "interviewAt survives moving back");
        Equal(done, job.DoneAt, "doneAt survives moving back");
        Equal(ready, job.ReadyAt, "the original readyAt is not overwritten");
    }

    static void ActivityCounts() {
        var now = new DateTime(2026, 9, 17, 14, 0, 0);
        var tasks = new List<JobTask>();

        void Applied(string id, DateTime at, string status = ApplicationStatus.Applied) {
            var job = Job(id);
            JobTracker.UpdateStatus(job, status, at);
            tasks.Add(job);
        }

        Applied("A-1", now.Date.AddHours(9));                    // today
        Applied("A-2", now.Date.AddHours(11));                   // today
        Applied("A-3", now.Date.AddDays(-5));                    // this month
        Applied("A-4", now.Date.AddDays(-29));                   // edge of the 30-day window
        Applied("A-5", now.Date.AddDays(-45));                   // older than 30 days
        Applied("A-6", now.Date.AddDays(-2), ApplicationStatus.Interview);  // applied, now interviewing
        tasks.Add(Job("A-7"));                                   // never applied for

        var activity = JobTracker.GetApplicationActivity(tasks, now);
        Equal(2, activity.Today, "today");
        Equal(5, activity.Last30Days, "last 30 days");
        Equal(6, activity.Total, "total applications (Applied, Interview and Done)");

        // An Interview job carries its AppliedAt, so it is counted once, not twice.
        var stats = JobTracker.GetStatistics(tasks);
        Equal(1, stats.Interview, "interview count");
        Equal(5, stats.Applied, "applied count");
        Equal(6, stats.AppliedOrLater, "applied or later");

        var empty = JobTracker.GetApplicationActivity(new List<JobTask>(), now);
        Equal(0, empty.Today, "empty today");
        Equal(0, empty.Total, "empty total");

        // A job marked Applied before timestamps existed still counts in the total.
        var legacy = Job("A-8");
        legacy.ApplicationStatus = ApplicationStatus.Applied;     // no AppliedAt
        var withLegacy = JobTracker.GetApplicationActivity(new[] { legacy }, now);
        Equal(1, withLegacy.Total, "a status-only Applied job counts in the total");
        Equal(0, withLegacy.Today, "but contributes nothing to dated activity");
    }

    static void DailyAggregation() {
        var now = new DateTime(2026, 9, 17, 14, 0, 0);
        var tasks = new List<JobTask>();

        void Applied(string id, int daysAgo) {
            var job = Job(id);
            JobTracker.UpdateStatus(job, ApplicationStatus.Applied, now.Date.AddDays(-daysAgo).AddHours(10));
            tasks.Add(job);
        }

        Applied("D-1", 0); Applied("D-2", 0); Applied("D-3", 0);   // 3 today
        Applied("D-4", 1);                                          // 1 yesterday
        Applied("D-5", 3); Applied("D-6", 3);                       // 2 three days ago
        Applied("D-7", 40);                                         // outside both windows

        var week = JobTracker.GetDailyApplicationCounts(tasks, 7, now);
        Equal(7, week.Count, "a 7-day window has 7 bars");
        Equal(now.Date, week[^1].Date, "the last bar is today");
        Equal(3, week[^1].Count, "today's count");
        Equal(1, week[^2].Count, "yesterday's count");
        Equal(2, week[^4].Count, "three days ago");
        Equal(0, week[^3].Count, "a day with no applications is a zero bar");
        Equal("Sep 17", week[^1].Label, "bar label");

        var month = JobTracker.GetDailyApplicationCounts(tasks, 30, now);
        Equal(30, month.Count, "a 30-day window has 30 bars");
        Equal(6, month.Sum(d => d.Count), "the 40-day-old application is outside the window");

        // All time lists only the days that have applications, so a long history stays readable.
        var all = JobTracker.GetDailyApplicationCounts(tasks, 0, now);
        Equal(4, all.Count, "all time has one bar per active day");
        Equal(7, all.Sum(d => d.Count), "every application is counted");
        Check(all[0].Date < all[^1].Date, "oldest first");

        Equal(0, JobTracker.GetDailyApplicationCounts(new List<JobTask>(), 7, now).Sum(d => d.Count), "no data, no counts");
    }

    static void PipelineCounts() {
        var tasks = new List<JobTask>();
        void Add(int count, string status) {
            for (var i = 0; i < count; i++) {
                var job = Job($"{status}-{i}");
                JobTracker.UpdateStatus(job, status);
                tasks.Add(job);
            }
        }
        Add(4, ApplicationStatus.Viewed);
        Add(3, ApplicationStatus.Ready);
        Add(2, ApplicationStatus.Applied);
        Add(1, ApplicationStatus.Interview);

        var pipeline = JobTracker.GetPipelineCounts(tasks);
        Equal(5, pipeline.Count, "five stages, always");
        Equal("Viewed,Ready,Applied,Interview,Done", string.Join(",", pipeline.Select(p => p.Status)), "flow order");
        Equal(4, pipeline[0].Count, "viewed");
        Equal(3, pipeline[1].Count, "ready");
        Equal(2, pipeline[2].Count, "applied");
        Equal(1, pipeline[3].Count, "interview");
        Equal(0, pipeline[4].Count, "an empty stage still appears");
    }

    static void SearchAndFilters() {
        var google = Job("JOB-001", "Google", "Senior AI Engineer");
        var stripe = Job("JOB-002", "Stripe", "Backend Engineer");
        JobTracker.UpdateStatus(stripe, ApplicationStatus.Applied);
        var tesla = Job("TES-003", "Tesla", "Machine Learning Engineer");
        JobTracker.UpdateStatus(tesla, ApplicationStatus.Ready);
        var tasks = new[] { google, stripe, tesla };

        Equal(3, JobTracker.ApplyFilters(tasks, "", null, null).Count, "no filters");
        Equal(3, JobTracker.ApplyFilters(tasks, "   ", ApplicationStatus.All, DateFilter.AllDates).Count, "explicit no filters");

        // Search covers company, role and job id, case-insensitively.
        Equal("JOB-001", JobTracker.ApplyFilters(tasks, "google", null, null)[0].JobId, "by company");
        Equal("JOB-002", JobTracker.ApplyFilters(tasks, "backend", null, null)[0].JobId, "by role");
        Equal("TES-003", JobTracker.ApplyFilters(tasks, "tes-0", null, null)[0].JobId, "by job id");
        Equal(3, JobTracker.ApplyFilters(tasks, "engineer", null, null).Count, "a partial role match");
        Equal(0, JobTracker.ApplyFilters(tasks, "nothing here", null, null).Count, "no match");

        // Search and status filter combine.
        Equal(1, JobTracker.ApplyFilters(tasks, "engineer", ApplicationStatus.Applied, null).Count, "search plus status");
        Equal(0, JobTracker.ApplyFilters(tasks, "google", ApplicationStatus.Applied, null).Count, "search excludes the status match");
    }

    static void DateFiltering() {
        var now = new DateTime(2026, 9, 17, 14, 0, 0);

        var today = Job("DF-1");
        JobTracker.UpdateStatus(today, ApplicationStatus.Applied, now.Date.AddHours(9));
        var lastWeek = Job("DF-2");
        JobTracker.UpdateStatus(lastWeek, ApplicationStatus.Applied, now.Date.AddDays(-4));
        var lastMonth = Job("DF-3");
        JobTracker.UpdateStatus(lastMonth, ApplicationStatus.Applied, now.Date.AddDays(-20));
        var ancient = Job("DF-4");
        JobTracker.UpdateStatus(ancient, ApplicationStatus.Applied, now.Date.AddDays(-60));
        var tasks = new[] { today, lastWeek, lastMonth, ancient };

        Equal(4, JobTracker.ApplyFilters(tasks, null, null, DateFilter.AllDates, now).Count, "all dates");
        Equal(1, JobTracker.ApplyFilters(tasks, null, null, DateFilter.Today, now).Count, "today");
        Equal(2, JobTracker.ApplyFilters(tasks, null, null, DateFilter.Last7, now).Count, "last 7 days");
        Equal(3, JobTracker.ApplyFilters(tasks, null, null, DateFilter.Last30, now).Count, "last 30 days");

        // A job that was never applied for falls back to when its resume became ready, then to created.
        var neverApplied = Job("DF-5");
        JobTracker.UpdateStatus(neverApplied, ApplicationStatus.Ready, now.Date.AddHours(8));
        Equal(now.Date.AddHours(8), neverApplied.TrackingDate, "tracking date falls back to readyAt");
        Equal(1, JobTracker.ApplyFilters(new[] { neverApplied }, null, null, DateFilter.Today, now).Count, "and the date filter uses it");

        Equal(6, DateFilter.Options.Length, "six date choices");
        Check(DateFilter.Since(DateFilter.AllDates, now) is null, "any date has no lower bound");

        // The two ranges the calendar popup added. The jobs sit at 0, 4, 20 and 60 days back.
        Equal(1, JobTracker.ApplyFilters(tasks, null, null, DateFilter.Last3, now).Count, "last 3 days");
        Equal(2, JobTracker.ApplyFilters(tasks, null, null, DateFilter.Last15, now).Count, "last 15 days");
        Equal(now.Date.AddDays(-2), DateFilter.Since(DateFilter.Last3, now), "last 3 days starts 2 days back");
        Equal(now.Date, DateFilter.Since(DateFilter.Today, now), "today starts today");
    }

    static void ExactDateFiltering() {
        var now = new DateTime(2026, 9, 17, 14, 0, 0);

        JobTask Applied(string id, DateTime at) {
            var job = Job(id);
            JobTracker.UpdateStatus(job, ApplicationStatus.Applied, at);
            return job;
        }

        var today = Applied("X-1", now.Date.AddHours(9));
        var alsoToday = Applied("X-2", now.Date.AddHours(20));   // same day, later hour
        var yesterday = Applied("X-3", now.Date.AddDays(-1).AddHours(11));
        var lastWeek = Applied("X-4", now.Date.AddDays(-7));
        var tasks = new[] { today, alsoToday, yesterday, lastWeek };

        var onToday = JobTracker.ApplyFilters(tasks, null, null, DateFilter.AllDates, now, now.Date);
        Equal(2, onToday.Count, "both of today's jobs, whatever the hour");

        Equal(1, JobTracker.ApplyFilters(tasks, null, null, DateFilter.AllDates, now, now.Date.AddDays(-1)).Count, "yesterday only");
        Equal(0, JobTracker.ApplyFilters(tasks, null, null, DateFilter.AllDates, now, now.Date.AddDays(-3)).Count, "a day with nothing on it");

        // An exact date wins over whatever quick range is still selected.
        Equal(1, JobTracker.ApplyFilters(tasks, null, null, DateFilter.Today, now, now.Date.AddDays(-7)).Count,
              "the exact date overrides the quick range");

        // And it still combines with search and status.
        Equal(0, JobTracker.ApplyFilters(tasks, "nothing", null, null, now, now.Date).Count, "exact date plus search");
        Equal(2, JobTracker.ApplyFilters(tasks, null, ApplicationStatus.Applied, null, now, now.Date).Count, "exact date plus status");
    }

    static void SearchAcrossFields() {
        var caterpillar = Job("STRESS-114209-001", "Caterpillar Inc", "Senior AI Software Engineer");
        var google = Job("JOB-002", "Google", "Cloud Engineer");
        var tasks = new[] { caterpillar, google };

        // Company, role and job id each match on their own.
        Equal(1, JobTracker.Search(tasks, "Caterpillar").Count, "by company");
        Equal(1, JobTracker.Search(tasks, "AI Software").Count, "by a phrase inside the role");
        Equal(1, JobTracker.Search(tasks, "114209").Count, "by part of the job id");

        // Case does not matter.
        Equal(1, JobTracker.Search(tasks, "caterpillar").Count, "lower case");
        Equal(1, JobTracker.Search(tasks, "CATERPILLAR").Count, "upper case");
        Equal(1, JobTracker.Search(tasks, "sEnIoR aI").Count, "mixed case");

        // The words may be spread across company and role, in any order.
        Equal("STRESS-114209-001", JobTracker.Search(tasks, "Caterpillar AI")[0].JobId, "company word plus role word");
        Equal(1, JobTracker.Search(tasks, "AI Caterpillar").Count, "word order does not matter");
        Equal(1, JobTracker.Search(tasks, "  Caterpillar   Engineer  ").Count, "extra spaces are ignored");
        Equal(1, JobTracker.Search(tasks, "114209 Engineer").Count, "job id word plus role word");

        // Every word has to match something, so an unrelated word rules the job out.
        Equal(0, JobTracker.Search(tasks, "Caterpillar Nurse").Count, "one unmatched word excludes the job");
        Equal(2, JobTracker.Search(tasks, "Engineer").Count, "a word both jobs share");
        Equal(2, JobTracker.Search(tasks, "").Count, "empty search returns everything");
        Equal(2, JobTracker.Search(tasks, "   ").Count, "whitespace search returns everything");
        Equal(2, JobTracker.Search(tasks, null).Count, "null search returns everything");

        // The list and the board are filtered by the same call, so they cannot disagree.
        var filtered = JobTracker.ApplyFilters(tasks, "Caterpillar AI", ApplicationStatus.All, DateFilter.AllDates);
        Equal(1, filtered.Count, "ApplyFilters uses the same search");
        Equal(1, JobTracker.GetTasksByStatus(filtered, ApplicationStatus.Viewed).Count, "the board sees the same result");
    }

    static void ResumeRelinking() {
        var root = NewDir("relink");
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var yesterday = DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd");

        string MakeResume(string date, string folder, string file) {
            var dir = Path.Combine(root, date, folder);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, file);
            File.WriteAllText(path, "not really a docx");
            return path;
        }

        // A document generated before tracking existed: on disk, but no path on the task.
        var expected = MakeResume(today, "Caterpillar Inc - Senior AI Software Engineer", "Resume.docx");
        MakeResume(yesterday, "Caterpillar Inc - Senior AI Software Engineer", "Resume.docx");   // older, must lose

        var job = Job("R-1", "Caterpillar Inc.", "Senior AI Software Engineer");
        Check(!job.ResumeGenerated, "the job starts with no resume path");
        Equal(expected, JobTracker.FindExistingResume(job, root), "the newest dated folder wins");

        // The newest revision inside that folder wins too.
        var second = MakeResume(today, "Caterpillar Inc - Senior AI Software Engineer", "Resume (2).docx");
        Equal(second, JobTracker.FindExistingResume(job, root), "Resume (2).docx beats Resume.docx");

        var noMatch = Job("R-2", "Nowhere Ltd", "Engineer");
        Check(JobTracker.FindExistingResume(noMatch, root) is null, "a job with no folder finds nothing");
        Check(JobTracker.FindExistingResume(job, null) is null, "no resume root, no search");
        Check(JobTracker.FindExistingResume(job, Path.Combine(root, "missing")) is null, "a missing root is safe");

        // The repair pass fills the gap and reports how many it fixed.
        var tasks = new List<JobTask> { job, noMatch };
        Equal(1, JobTracker.RelinkResumes(tasks, root), "one job was repaired");
        Equal(second, job.ResumePath, "the found document was stored");
        Check(job.ResumeGenerated, "so the Resume actions are enabled");
        Check(!noMatch.ResumeGenerated, "the job with no document is untouched");

        Equal(0, JobTracker.RelinkResumes(tasks, root), "a second pass changes nothing");

        // A stale path is repaired; a valid one is left exactly as it is.
        job.ResumePath = Path.Combine(root, "gone", "Resume.docx");
        Equal(1, JobTracker.RelinkResumes(tasks, root), "a stale path is repaired");
        Equal(second, job.ResumePath, "back to the real document");

        // And the actions now resolve to the right places.
        Equal(Path.Combine(root, today, "Caterpillar Inc - Senior AI Software Engineer"),
              Path.GetDirectoryName(job.ResumePath), "the folder action targets the job folder, not the root");
    }

    static void ResumeActionsAreSafe() {
        var noResume = Job("R-1");
        Check(!JobTracker.OpenResume(noResume), "a job with no resume opens nothing");
        Check(!JobTracker.OpenResumeFolder(noResume), "and has no folder to open");
        Equal("No resume", noResume.ResumeStateDisplay, "the column says so");

        var missing = Job("R-2");
        missing.ResumePath = Path.Combine(TempRoot, "does-not-exist", "Resume.docx");
        Check(!JobTracker.OpenResume(missing), "a deleted resume opens nothing rather than throwing");
        Check(!JobTracker.OpenResumeFolder(missing), "a missing folder opens nothing");
        Equal("Ready", missing.ResumeStateDisplay, "the path is still recorded");

        Check(!JobTracker.OpenResume(null), "null is safe");
        Check(!JobTracker.OpenResumeFolder(null), "null is safe");
    }

    // ---------- sample ----------

    static void StatusSurvivesRestart() {
        // Storage writes the live tasks.json, so the real file is restored afterwards.
        var live = Storage.TasksPath;
        var backup = File.Exists(live) ? File.ReadAllBytes(live) : null;

        try {
            var applied = new DateTime(2026, 9, 17, 11, 0, 0);
            var saved = Job("RESTART-1", "Tesla", "ML Engineer", "https://example.com/job/1");
            JobTracker.UpdateStatus(saved, ApplicationStatus.Applied, applied);
            saved.ResumePath = @"C:\Resumes\Resume.docx";
            saved.Status = "Completed";

            JobTracker.SaveTrackingData(new[] { saved, Job("RESTART-2") });

            var reloaded = Storage.LoadTasks();
            Equal(2, reloaded.Count, "task count");

            var first = reloaded[0];
            Equal(ApplicationStatus.Applied, first.ApplicationStatus, "application status survived");
            Equal("Completed", first.Status, "queue status survived");
            Equal(applied, first.AppliedAt, "appliedAt survived");
            Equal(@"C:\Resumes\Resume.docx", first.ResumePath, "resume path survived");
            Equal("https://example.com/job/1", first.Link, "job link survived");
            Equal(ApplicationStatus.Viewed, reloaded[1].ApplicationStatus, "an untouched task stays Viewed");
        } finally {
            if (backup is byte[] content) File.WriteAllBytes(live, content);
            else if (File.Exists(live)) File.Delete(live);
        }
    }

    static void LegacyTasksLoad() {
        // Exactly the shape tasks.json had before tracking existed.
        var legacy = """
        [
          { "JobId": "OLD-1", "Source": "linkedin", "Company": "Google", "Title": "Cloud Engineer",
            "Location": "", "Jd": "...", "Link": "https://example.com/job/9", "About": "", "Status": "Completed" },
          { "JobId": "OLD-2", "Company": "Stripe", "Title": "Backend Engineer", "Jd": "...", "Status": "Queued" }
        ]
        """;

        var tasks = JsonSerializer.Deserialize<List<JobTask>>(legacy, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Equal(2, tasks.Count, "legacy task count");

        foreach (var task in tasks) {
            Equal(ApplicationStatus.Viewed, task.ApplicationStatus, task.JobId + " defaults to Viewed");
            Check(!task.ResumeGenerated, task.JobId + " has no resume recorded");
            Check(task.CreatedAt != default, task.JobId + " gets a createdAt stamp");
        }

        Equal("Completed", tasks[0].Status, "the queue status is untouched");
        Equal("Google", tasks[0].Company, "the rest of the task is untouched");

        // A task carrying an unreadable status is also safe.
        var odd = JsonSerializer.Deserialize<List<JobTask>>(
            """[ { "JobId": "ODD-1", "ApplicationStatus": "Whatever" } ]""",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Equal(ApplicationStatus.Viewed, odd[0].ApplicationStatus, "an unreadable status becomes Viewed");
    }

    static void StatisticsAreCorrect() {
        var tasks = new List<JobTask>();
        void Add(int count, string status) {
            for (var i = 0; i < count; i++) {
                var job = Job($"{status}-{i}");
                JobTracker.UpdateStatus(job, status);
                tasks.Add(job);
            }
        }

        Add(80, ApplicationStatus.Viewed);
        Add(45, ApplicationStatus.Ready);
        Add(90, ApplicationStatus.Applied);
        Add(25, ApplicationStatus.Interview);
        Add(10, ApplicationStatus.Done);

        var stats = JobTracker.GetStatistics(tasks);
        Equal(250, stats.Total, "total");
        Equal(80, stats.Viewed, "viewed");
        Equal(45, stats.Ready, "ready");
        Equal(90, stats.Applied, "applied");
        Equal(25, stats.Interview, "interview");
        Equal(10, stats.Done, "done");

        // A job at Interview was applied for, so the rates count it.
        Equal(125, stats.AppliedOrLater, "applied or later");
        Equal(35, stats.InterviewOrLater, "interview or later");
        Equal("50%", JobStatistics.Percent(stats.AppliedRate), "applied rate");
        Equal("28%", JobStatistics.Percent(stats.InterviewRate), "interview rate");
        Equal("28.6%", JobStatistics.Percent(stats.CompletionRate), "completion rate");

        // No jobs must not divide by zero.
        var empty = JobTracker.GetStatistics(new List<JobTask>());
        Equal(0, empty.Total, "empty total");
        Equal("0%", JobStatistics.Percent(empty.AppliedRate), "empty applied rate");
        Equal("0%", JobStatistics.Percent(empty.CompletionRate), "empty completion rate");
    }

    static void PairSharePercentages() {
        // The dashboard's adjacent-pair percentage is A / (A + B), not B / A.
        Equal("60%", JobStatistics.WholePercent(JobStatistics.PairShare(30, 20)), "A=30 B=20");
        Equal("40%", JobStatistics.WholePercent(JobStatistics.PairShare(20, 30)), "A=20 B=30");
        Equal("0%", JobStatistics.WholePercent(JobStatistics.PairShare(0, 10)), "A=0 B=10");
        Equal("100%", JobStatistics.WholePercent(JobStatistics.PairShare(10, 0)), "A=10 B=0");
        Equal("0%", JobStatistics.WholePercent(JobStatistics.PairShare(0, 0)), "A=0 B=0 divides safely");

        Equal(0.6, Math.Round(JobStatistics.PairShare(30, 20), 4), "the raw share");
        Equal(1.0, Math.Round(JobStatistics.PairShare(30, 20) + JobStatistics.PairShare(20, 30), 4),
              "the two sides of a pair add up to 1");

        // Whole percentages, and the split text names both sides so the number cannot be misread.
        Equal("33%", JobStatistics.WholePercent(JobStatistics.PairShare(1, 2)), "rounded to whole");
        Equal("33% Ready vs 67% Applied", JobStatistics.PairSplit("Ready", 1, "Applied", 2), "pair split text");
        Equal("0% Ready vs 0% Applied", JobStatistics.PairSplit("Ready", 0, "Applied", 0), "an empty pair");

        // The funnel rates are a different, separately specified statistic and are unchanged.
        var stats = JobTracker.GetStatistics(new List<JobTask>());
        Equal("0%", JobStatistics.Percent(stats.AppliedRate), "an empty funnel still reports 0%");
    }

    static void FilteringByStatus() {
        var viewed = Job("F-1");
        var ready = Job("F-2");
        JobTracker.UpdateStatus(ready, ApplicationStatus.Ready);
        var applied = Job("F-3");
        JobTracker.UpdateStatus(applied, ApplicationStatus.Applied);
        var tasks = new[] { viewed, ready, applied };

        Equal(3, JobTracker.GetTasksByStatus(tasks, ApplicationStatus.All).Count, "All");
        Equal(3, JobTracker.GetTasksByStatus(tasks, null).Count, "no filter");
        Equal(1, JobTracker.GetTasksByStatus(tasks, ApplicationStatus.Viewed).Count, "Viewed");
        Equal("F-2", JobTracker.GetTasksByStatus(tasks, ApplicationStatus.Ready)[0].JobId, "Ready");
        Equal(0, JobTracker.GetTasksByStatus(tasks, ApplicationStatus.Done).Count, "Done");
        Equal(6, ApplicationStatus.Filters.Length, "the filter list is All plus the five statuses");
    }

    static void JobUrlIsValidated() {
        Check(JobTracker.IsOpenableUrl("https://example.com/job/123"), "https");
        Check(JobTracker.IsOpenableUrl("http://example.com/job/123"), "http");

        // Job payloads are third-party data: nothing but a web link may reach the shell.
        foreach (var hostile in new[] { "file:///C:/Windows/System32/cmd.exe", @"C:\Windows\System32\cmd.exe",
                                        "javascript:alert(1)", "ms-settings:", "", "   ", "not a url", "ftp://example.com" })
            Check(!JobTracker.IsOpenableUrl(hostile), "should be refused: " + hostile);

        Check(!JobTracker.IsOpenableUrl(null), "null");
        Check(!JobTracker.OpenJobUrl(Job("URL-1")), "a job with no link opens nothing");
        Check(!JobTracker.OpenJobUrl(Job("URL-2", link: @"C:\Windows\System32\cmd.exe")), "a local path is never launched");
    }

    static void StatusesStayIndependent() {
        var job = Job("BOTH-1");

        job.Status = "Completed";
        JobTracker.MarkResumeReady(job, @"C:\Resumes\Resume.docx");
        Equal("Completed", job.Status, "queue status");
        Equal(ApplicationStatus.Ready, job.ApplicationStatus, "application status");

        // Re-queueing a job for a fresh answer must not reset the application's progress.
        JobTracker.UpdateStatus(job, ApplicationStatus.Applied);
        job.Status = "Queued";
        Equal(ApplicationStatus.Applied, job.ApplicationStatus, "the application keeps its own state");

        job.Status = "Failed";
        Equal(ApplicationStatus.Applied, job.ApplicationStatus, "a failed job is still an application that was sent");
    }

    // ---------- sample ----------

    static void JobBrowserProfileIsSeparate() {
        // The whole point of the second browser: its own profile, so ChatGPT recycling cannot touch
        // it and its sign-in is independent.
        Check(JobBrowser.IsSeparateProfileFrom(Storage.WebViewUserDataFolder),
              "the job browser profile must be separate from the ChatGPT one");

        Equal("JobBrowserWebView2", Path.GetFileName(JobBrowser.UserDataFolder), "profile folder name");
        Equal(Path.GetFullPath(Storage.DataDir), Path.GetFullPath(Path.GetDirectoryName(JobBrowser.UserDataFolder)!),
              "it sits beside the other app data, not inside the ChatGPT profile");

        Check(!Path.GetFullPath(JobBrowser.UserDataFolder)
                   .StartsWith(Path.GetFullPath(Storage.WebViewUserDataFolder), StringComparison.OrdinalIgnoreCase),
              "the job browser profile must not be nested inside the ChatGPT profile");
        Check(!Path.GetFullPath(Storage.WebViewUserDataFolder)
                   .StartsWith(Path.GetFullPath(JobBrowser.UserDataFolder), StringComparison.OrdinalIgnoreCase),
              "the ChatGPT profile must not be nested inside the job browser profile");

        // The guard itself has to be able to say no.
        Check(!JobBrowser.IsSeparateProfileFrom(JobBrowser.UserDataFolder), "the same folder is not separate");
        Check(!JobBrowser.IsSeparateProfileFrom(Storage.DataDir), "a parent folder is not separate");
        Check(!JobBrowser.IsSeparateProfileFrom(Path.Combine(JobBrowser.UserDataFolder, "Default")),
              "a child folder is not separate");
    }

    static void JobBrowserHomeAndLogging() {
        Check(JobBrowser.HomeUrl.StartsWith("https://", StringComparison.Ordinal), "home is https");
        Check(Uri.TryCreate(JobBrowser.HomeUrl, UriKind.Absolute, out _), "home is an absolute url");

        // Diagnostics never carry a query string: it can hold a session token or a search term.
        Equal("https://jobright.ai/jobs/recommend",
              JobBrowser.SafeForLog("https://jobright.ai/jobs/recommend?token=secret&q=engineer"), "query stripped");
        Equal("https://jobright.ai/jobs/1", JobBrowser.SafeForLog("https://jobright.ai/jobs/1#section"), "fragment stripped");
        Equal("https://jobright.ai/", JobBrowser.SafeForLog(JobBrowser.HomeUrl), "the home page");
        Equal("(none)", JobBrowser.SafeForLog(""), "empty");
        Equal("(none)", JobBrowser.SafeForLog(null), "null");
        Equal("(not a url)", JobBrowser.SafeForLog("nonsense"), "not a url");
        Equal("file:", JobBrowser.SafeForLog(@"file:///C:/secret.txt"), "a non-web scheme logs no path");

        var token = JobBrowser.SafeForLog("https://jobright.ai/callback?code=abc123&state=xyz");
        Check(!token.Contains("abc123") && !token.Contains("xyz"), "no token reaches the log: " + token);
    }

    static void JobImportDataIsNotATask() {
        // The canonical input: five fields, no external id, no location. It is not a JobTask, so a
        // file or a page reader can never write to storage or the queue itself.
        var names = typeof(JobImportData).GetProperties().Select(p => p.Name).OrderBy(n => n).ToArray();
        Equal("Company,CompanyUrl,Description,JobUrl,Title", string.Join(",", names), "the five canonical fields");

        var data = new JobImportData();
        Equal("", data.Company, "company defaults empty");
        Check(data.CompanyUrl is null, "companyUrl defaults to none");
        Check(!typeof(JobTask).IsAssignableFrom(typeof(JobImportData)), "JobImportData must not be a JobTask");
        Check(typeof(IJobPageExtractor).GetMethods().Length == 1, "the extractor interface stays minimal");

        // The JSON names are exactly the contract's.
        var json = JsonSerializer.Serialize(new JobImportData { Company = "c", Title = "t", JobUrl = "u", CompanyUrl = "w", Description = "d" });
        foreach (var key in new[] { "\"company\"", "\"title\"", "\"jobUrl\"", "\"companyUrl\"", "\"description\"" })
            Check(json.Contains(key), "serialized name " + key);
    }

    // ---------- job URL normalization ----------

    static void JobUrlNormalization() {
        const string canonical = "https://jobright.ai/jobs/info/6aac7fec95c707f49dff195f";

        // Everything that is the same job collapses to one form.
        foreach (var url in new[] {
            canonical,
            "  " + canonical + "  ",
            canonical + "/",
            canonical + "#apply",
            canonical + "?utm_source=1146",
            canonical + "?utm_source=1146&utm_campaign=remote&gclid=abc",
            "HTTPS://JobRight.AI/jobs/info/6aac7fec95c707f49dff195f",
            "https://jobright.ai:443/jobs/info/6aac7fec95c707f49dff195f"
        })
            Equal(canonical, JobUrls.Normalize(url), "normalized form of " + url);

        // A parameter that is not known tracking is kept — elsewhere it may be what names the job —
        // and parameter order does not matter.
        Equal("https://jobs.example.com/view?id=42&team=ml",
              JobUrls.Normalize("https://jobs.example.com/view?team=ml&utm_source=x&id=42"), "non-tracking parameters kept, sorted");
        Check(JobUrls.Normalize("https://jobs.example.com/view?id=42") != JobUrls.Normalize("https://jobs.example.com/view?id=43"),
              "different job parameters are different jobs");

        // Path case is kept: on the web it can be significant.
        Check(JobUrls.Normalize("https://example.com/Jobs/1") != JobUrls.Normalize("https://example.com/jobs/1"), "path case kept");
        Equal("https://example.com/", JobUrls.Normalize("https://example.com"), "the root keeps its slash");

        foreach (var bad in new[] { null, "", "   ", "not a url", "/jobs/info/1", "ftp://example.com/job",
                                    "javascript:alert(1)", "file:///C:/job.html", "mailto:jobs@example.com" })
            Check(JobUrls.Normalize(bad) is null, "not a web address: " + bad);
    }

    // ---------- the extractor ----------

    /// <summary>A fixture as ExecuteScriptAsync delivers it: a JSON-encoded string.</summary>
    static string ScriptResult(string fixture = "jobright-page-payload.json") =>
        JsonSerializer.Serialize(File.ReadAllText(Fixture(fixture)));

    /// <summary>The main fixture with some of its parts replaced, for the edge cases.</summary>
    static string ScriptResultWith(Action<JsonObject> change) {
        var payload = JsonNode.Parse(File.ReadAllText(Fixture("jobright-page-payload.json")))!.AsObject();
        change(payload);
        return JsonSerializer.Serialize(payload.ToJsonString());
    }

    static void ExtractorReadsOneJob() {
        var data = JobrightPageExtractor.Parse(ScriptResult());

        Equal("ClearlyRated", data.Company, "company");
        Equal("Backend Software Engineer", data.Title, "title as the page shows it (from the page data)");
        Equal("https://jobright.ai/jobs/info/6aac7fec95c707f49dff195f", data.JobUrl, "canonical job URL, no tracking");
        Equal("https://www.clearlyrated.com", data.CompanyUrl, "the company's own website");

        // The full original posting is preferred when the page publishes it.
        Check(data.Description.StartsWith("Note: The job is a remote job"), "full description: " + data.Description[..40]);
        Check(data.Description.Contains("• Design, build and operate backend services in Python"), "bullets kept");
        Check(data.Description.Contains("understand & improve"), "entities decoded");
        Check(!data.Description.Contains("<") && !data.Description.Contains("alert(1)"), "no markup or script text");
    }

    static void ExtractorWithoutJsonLd() {
        // What Jobright serves most of the time: no JSON-LD, only the page's own data.
        var data = JobrightPageExtractor.Parse(ScriptResult("jobright-page-payload-no-jsonld.json"));

        Equal("ClearlyRated", data.Company, "company");
        Equal("Backend Software Engineer", data.Title, "title");
        Equal("https://jobright.ai/jobs/info/6aac7fec95c707f49dff195f", data.JobUrl, "job URL");
        Equal("https://www.clearlyrated.com", data.CompanyUrl, "company URL");

        // The description is this job's own sections, in the order the page shows them.
        var d = data.Description;
        Check(d.StartsWith("ClearlyRated is a B2B SaaS platform"), "summary first");
        Check(d.Contains("Responsibilities\n• System, Database and API design as a first-class skill.\n• Own reliability & observability."),
              "responsibilities as bullets: " + d);
        Check(d.Contains("Required qualifications\n• 5+ years of backend development in Python\n• Strong SQL"), "required qualifications");
        Check(d.Contains("Preferred qualifications\n• Experience with event-driven systems"), "preferred qualifications");
        Check(d.IndexOf("Responsibilities") < d.IndexOf("Required") && d.IndexOf("Required") < d.IndexOf("Preferred"), "section order");
    }

    static void ExtractorHtmlToText() {
        Equal("One\n\nTwo", JobrightPageExtractor.HtmlToText("<p>One</p><p></p><p></p><p>Two</p>"), "blank paragraphs collapse");
        Equal("a\nb", JobrightPageExtractor.HtmlToText("a<br/>b"), "line breaks");
        Equal("• x\n• y", JobrightPageExtractor.HtmlToText("<ul><li>x</li><li>y</li></ul>"), "bullets");
        Equal("Tom & Jerry", JobrightPageExtractor.HtmlToText("Tom &amp; Jerry"), "entities");
        Equal("", JobrightPageExtractor.HtmlToText(""), "empty");
        Equal("kept", JobrightPageExtractor.HtmlToText("<style>p{}</style>kept"), "style blocks dropped");
    }

    static void ExtractorRecognisesJobPages() {
        const string id = "6aac7fec95c707f49dff195f";
        foreach (var url in new[] {
            "https://jobright.ai/jobs/info/" + id, "https://jobright.ai/jobs/info/" + id + "?utm_source=1146",
            "https://jobright.ai/jobs/info/" + id + "/", "https://www.jobright.ai/jobs/info/" + id + "#apply" })
            Check(JobrightPageExtractor.IsJobPage(url), "should be a job page: " + url);

        foreach (var url in new[] {
            "http://jobright.ai/jobs/info/" + id, "https://evil.example/jobs/info/" + id,
            "https://jobright.ai.evil.example/jobs/info/" + id, "https://jobright.ai/jobs/recommend",
            "https://jobright.ai/jobs/info/12345", "https://jobright.ai/jobs/info/" + id + "/apply", "about:blank", "", null })
            Check(!JobrightPageExtractor.IsJobPage(url), "should not be a job page: " + url);

        // Without a canonical link the current address is used — its tracking query dropped.
        var data = JobrightPageExtractor.Parse(ScriptResultWith(p => p["canonical"] = null));
        Equal("https://jobright.ai/jobs/info/" + id, data.JobUrl, "job URL from the address");

        // A canonical link naming a different job is ignored rather than trusted.
        var other = JobrightPageExtractor.Parse(ScriptResultWith(p =>
            p["canonical"] = "https://jobright.ai/jobs/info/aaaaaaaaaaaaaaaaaaaaaaaa"));
        Equal("https://jobright.ai/jobs/info/" + id, other.JobUrl, "a mismatched canonical link is not used");
    }

    static void ExtractorRefusesBadPages() {
        void Refused(string scriptResult, string expectedWords, string what) {
            try { JobrightPageExtractor.Parse(scriptResult); }
            catch (JobExtractionException ex) {
                Check(ex.Message.Contains(expectedWords, StringComparison.OrdinalIgnoreCase), what + ": unexpected reason: " + ex.Message);
                return;
            }
            throw new Exception(what + ": the page should have been refused");
        }

        Refused(ScriptResultWith(p => { p["ld"] = new JsonArray(); p["next"] = null; }), "no job details", "no data at all");
        Refused(ScriptResultWith(p => p["href"] = "https://jobright.ai/jobs/recommend"), "not a Jobright job page", "a list page");
        Refused("\"garbage\"", "nothing readable", "a script result that is not JSON");

        // The single-page-app guard: data describing a different job is dropped, never imported.
        Refused(ScriptResultWith(p => {
            p["next"]!["jobId"] = "aaaaaaaaaaaaaaaaaaaaaaaa";          // page data still on the previous job
            p["ld"] = new JsonArray();
        }), "not finished switching", "stale page data");

        Refused(ScriptResultWith(p => {
            p["next"] = null;
            p["ld"] = new JsonArray((JsonNode?)"{\"@type\":\"JobPosting\",\"title\":\"Old\",\"hiringOrganization\":{\"name\":\"Old Co\"},\"description\":\"x\",\"identifier\":{\"value\":\"aaaaaaaaaaaaaaaaaaaaaaaa\"}}");
        }), "not finished switching", "stale JSON-LD");

        // One stale source is dropped and the fresh one still used.
        var mixed = JobrightPageExtractor.Parse(ScriptResultWith(p =>
            p["ld"] = new JsonArray((JsonNode?)"{\"@type\":\"JobPosting\",\"title\":\"Old\",\"hiringOrganization\":{\"name\":\"Old Co\"},\"description\":\"old text\",\"identifier\":{\"value\":\"aaaaaaaaaaaaaaaaaaaaaaaa\"}}")));
        Equal("ClearlyRated", mixed.Company, "the fresh page data wins over stale JSON-LD");
        Check(!mixed.Description.Contains("old text"), "stale description not used");

        // No company website stated: none is invented, and the job still extracts.
        var noSite = JobrightPageExtractor.Parse(ScriptResultWith(p => p["next"]!["companyUrl"] = null));
        Check(noSite.CompanyUrl is null, "no company URL when the page gives none: " + noSite.CompanyUrl);
        var badSite = JobrightPageExtractor.Parse(ScriptResultWith(p => p["next"]!["companyUrl"] = "javascript:alert(1)"));
        Check(badSite.CompanyUrl is null, "a non-web company URL is dropped");
    }

    static void ExtractorNeverRaisesRawScriptErrors() {
        var failing = new JobrightPageExtractor(
            _ => throw new InvalidOperationException("SyntaxError: Unexpected token <"),
            () => "https://jobright.ai/jobs/info/6aac7fec95c707f49dff195f");
        try {
            failing.ExtractCurrentJobAsync().GetAwaiter().GetResult();
            throw new Exception("the failure should have been reported");
        } catch (JobExtractionException ex) {
            Check(!ex.Message.Contains("SyntaxError"), "a raw script error reached the message: " + ex.Message);
        }

        // Off a job page the extractor does not even run the script.
        var ran = false;
        var offPage = new JobrightPageExtractor(_ => { ran = true; return Task.FromResult(""); }, () => "https://jobright.ai/jobs/recommend");
        Check(offPage.ExtractCurrentJobAsync().GetAwaiter().GetResult() is null, "not a job page returns null");
        Check(!ran, "no script is sent to a page that is not a job");
    }

    static void ExtractorStaysOutOfStorage() {
        var type = typeof(JobrightPageExtractor);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static |
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;

        var touched = type.GetFields(flags).Select(f => f.FieldType)
            .Concat(type.GetConstructors(flags).SelectMany(c => c.GetParameters()).Select(p => p.ParameterType))
            .Concat(type.GetMethods(flags).Where(m => m.DeclaringType == type).Select(m => m.ReturnType))
            .ToList();
        Check(!touched.Any(t => t == typeof(JobTask) || t == typeof(List<JobTask>) || t.Name.Contains("Storage")),
              "the extractor must not reference JobTask or storage");
        Check(typeof(IJobPageExtractor).IsAssignableFrom(type), "it implements the boundary interface");

        // The one script it sends reads the address, the canonical link, JSON-LD and named page-data
        // fields — nothing of the user's.
        var script = JobrightPageExtractor.ReadScript;
        foreach (var forbidden in new[] { "cookie", "localStorage", "sessionStorage", "indexedDB", "innerHTML",
                                          "innerText", "document.body", "fetch(", "XMLHttpRequest", "click(", "token" })
            Check(!script.Contains(forbidden, StringComparison.OrdinalIgnoreCase), "the read script must not touch " + forbidden);
        Check(script.Contains("application/ld+json") && script.Contains("__NEXT_DATA__") && script.Contains("canonical"),
              "it reads JSON-LD, the page data and the canonical link");
    }

    // ---------- the importer ----------

    static JobImportData CanonicalJob(string url = "https://jobright.ai/jobs/info/6aac7fec95c707f49dff195f") => new() {
        Company = "Caterpillar Inc", Title = "Senior AI Software Engineer", JobUrl = url,
        CompanyUrl = "https://www.caterpillar.com/", Description = "Build AI services."
    };

    /// <summary>Runs a body with the live tasks.json backed up and put back afterwards.</summary>
    static void WithTasksFileRestored(Action body) {
        var live = Storage.TasksPath;
        var backup = File.Exists(live) ? File.ReadAllBytes(live) : null;
        try { body(); }
        finally {
            if (backup is byte[] content) File.WriteAllBytes(live, content);
            else if (File.Exists(live)) File.Delete(live);
        }
    }

    static void ImportOneCreatesOneViewedTask() => WithTasksFileRestored(() => {
        var tasks = new List<JobTask> { Job("OLD-TASK-1") };
        var outcome = JobImporter.ImportOne(CanonicalJob(), JobImporter.BrowserSource, tasks);

        Equal(JobImportKind.Imported, outcome.Kind, "imported");
        Equal(2, tasks.Count, "exactly one task added");

        var task = tasks[^1];
        Equal("Caterpillar Inc", task.Company, "company");
        Equal("Senior AI Software Engineer", task.Title, "title");
        Equal("https://jobright.ai/jobs/info/6aac7fec95c707f49dff195f", task.Link, "jobUrl stored as the task's link");
        Equal("https://www.caterpillar.com/", task.CompanyUrl, "companyUrl stored");
        Equal("Build AI services.", task.Jd, "description stored");
        Equal("", task.Location, "location is not invented");
        Equal("Queued", task.Status, "queue status");
        Equal(ApplicationStatus.Viewed, task.ApplicationStatus, "starts Viewed");
        Check(task.ReadyAt is null && task.AppliedAt is null, "nothing past Viewed is stamped");
        Equal(JobImporter.BrowserSource, task.Source, "source");
        Equal(task.JobId, outcome.JobId, "the outcome names the new task");
        Equal(ApplicationStatus.Viewed, outcome.ApplicationStatus, "the outcome reports Viewed");
    });

    static void InternalJobIdIsGenerated() => WithTasksFileRestored(() => {
        var tasks = new List<JobTask>();
        JobImporter.ImportOne(CanonicalJob("https://example.com/jobs/1"), JobImporter.IncomingSource, tasks);
        JobImporter.ImportOne(CanonicalJob("https://example.com/jobs/2"), JobImporter.IncomingSource, tasks);

        foreach (var task in tasks)
            Check(System.Text.RegularExpressions.Regex.IsMatch(task.JobId, @"^RB-\d{8}-\d{6}-[0-9a-f]{8}$"),
                  "internal id format: " + task.JobId);
        Check(tasks[0].JobId != tasks[1].JobId, "every task gets its own id");

        // Collision-safe even if the clock repeats: the generator checks what is already taken.
        var crowded = Enumerable.Range(0, 500).Select(_ => JobImporter.NewInternalId(tasks)).ToList();
        Equal(500, crowded.Distinct().Count(), "500 ids in a row are all distinct");
        Check(!crowded.Contains(tasks[0].JobId), "never an id already in the queue");
    });

    static void InternalIdAndCompanyUrlPersist() => WithTasksFileRestored(() => {
        var tasks = new List<JobTask>();
        JobImporter.ImportOne(CanonicalJob(), JobImporter.BrowserSource, tasks);
        var original = tasks[0];

        var reloaded = Storage.LoadTasks().Single();
        Equal(original.JobId, reloaded.JobId, "the internal id survives save and reload unchanged");
        Equal("https://www.caterpillar.com/", reloaded.CompanyUrl, "companyUrl persisted");
        Equal(original.Link, reloaded.Link, "job URL persisted");
        Equal(ApplicationStatus.Viewed, reloaded.ApplicationStatus, "status persisted");

        // Importing the same job again after the reload finds it by URL and keeps its id.
        var again = JobImporter.ImportOne(CanonicalJob(), JobImporter.BrowserSource, Storage.LoadTasks());
        Equal(JobImportKind.Duplicate, again.Kind, "still a duplicate after reload");
        Equal(original.JobId, again.JobId, "the duplicate names the original task, not a new id");
    });

    static void DuplicatesAreFoundByJobUrl() => WithTasksFileRestored(() => {
        var tasks = new List<JobTask>();
        const string url = "https://jobright.ai/jobs/info/6aac7fec95c707f49dff195f";
        var first = JobImporter.ImportOne(CanonicalJob(url), JobImporter.BrowserSource, tasks);

        foreach (var same in new[] {
            url,                                                 // raw-identical
            url + "?utm_source=1146",                            // tracking
            url + "/#apply",                                     // slash and fragment
            "HTTPS://JOBRIGHT.AI/jobs/info/6aac7fec95c707f49dff195f" }) {
            var outcome = JobImporter.ImportOne(CanonicalJob(same), JobImporter.BrowserSource, tasks);
            Equal(JobImportKind.Duplicate, outcome.Kind, "duplicate: " + same);
            Equal(first.JobId, outcome.JobId, "reports the existing task for " + same);
            Equal("Senior AI Software Engineer", outcome.Title, "reports the existing title");
            Equal("Caterpillar Inc", outcome.Company, "reports the existing company");
        }
        Equal(1, tasks.Count, "no second task, no second id");

        // A different job at the same company is a different job.
        var other = JobImporter.ImportOne(CanonicalJob("https://jobright.ai/jobs/info/bbbbbbbbbbbbbbbbbbbbbbbb"),
                                          JobImporter.BrowserSource, tasks);
        Equal(JobImportKind.Imported, other.Kind, "two job URLs at the same company are both allowed");
        Equal(2, tasks.Count, "second job added");

        // The duplicate key is the job URL, not the id: an old task with a matching link is found.
        var legacy = new List<JobTask> { new() { JobId = "STRESS-1-001", Company = "X", Title = "Y", Link = url + "?utm_source=old" } };
        Equal(JobImportKind.Duplicate, JobImporter.ImportOne(CanonicalJob(url), JobImporter.BrowserSource, legacy).Kind,
              "an older task with the same job URL is recognised");
    });

    static void RequiredFieldsAreEnforced() => WithTasksFileRestored(() => {
        var tasks = new List<JobTask>();

        foreach (var (change, reason) in new (Action<JobImportData>, string)[] {
            (d => d.Company = "  ", "Company is required."),
            (d => d.Title = "", "Job title is required."),
            (d => d.JobUrl = "", "Job URL is required."),
            (d => d.JobUrl = "not a url", "Job URL is not a valid web address."),
            (d => d.JobUrl = "ftp://example.com/job", "Job URL is not a valid web address."),
            (d => d.JobUrl = "javascript:alert(1)", "Job URL is not a valid web address."),
            (d => d.Description = "", "Job description is required.")
        }) {
            var data = CanonicalJob();
            change(data);
            var outcome = JobImporter.ImportOne(data, JobImporter.IncomingSource, tasks);
            Equal(JobImportKind.Invalid, outcome.Kind, reason);
            Equal(reason, outcome.Reason, "reason");
            Equal(0, tasks.Count, "nothing added for: " + reason);
        }

        // companyUrl is optional: missing, empty or unusable, the job still imports.
        foreach (var companyUrl in new string?[] { null, "", "not a url" }) {
            var data = CanonicalJob("https://example.com/jobs/" + Guid.NewGuid().ToString("N"));
            data.CompanyUrl = companyUrl;
            Equal(JobImportKind.Imported, JobImporter.ImportOne(data, JobImporter.IncomingSource, tasks).Kind, "companyUrl " + (companyUrl ?? "null"));
            Equal("", tasks[^1].CompanyUrl, "no company URL stored");
        }
    });

    static void IncomingReadsExactlyOneJob() {
        // The canonical file, with no jobId and no location.
        var data = JobImporter.ReadSingleJob("""
            { "company": "Caterpillar Inc", "title": "Senior AI Software Engineer",
              "jobUrl": "https://jobright.ai/jobs/info/6aac7fec95c707f49dff195f",
              "companyUrl": "https://www.caterpillar.com/", "description": "Build AI services." }
            """);
        Equal("Caterpillar Inc", data.Company, "company");
        Equal("https://www.caterpillar.com/", data.CompanyUrl, "companyUrl");

        var minimal = JobImporter.ReadSingleJob("""{ "company": "A", "title": "B", "jobUrl": "https://x.example/1", "description": "D" }""");
        Check(minimal.CompanyUrl is null, "companyUrl may be missing");

        void Rejected(string json, string words, string what) {
            try { JobImporter.ReadSingleJob(json); }
            catch (InvalidDataException ex) {
                Check(ex.Message.Contains(words), what + ": " + ex.Message);
                return;
            }
            throw new Exception(what + " should have been rejected");
        }

        Rejected("""[ { "company": "A" }, { "company": "B" } ]""", JobImporter.OneJobOnly, "an array of jobs");
        Rejected("""[ { "company": "A", "title": "B", "jobUrl": "https://x.example/1", "description": "D" } ]""",
                 JobImporter.OneJobOnly, "an array of ONE job is still an array — never quietly unwrapped");
        Rejected("""{ "schemaVersion": "1.0", "jobs": [ { "jobId": "1" } ] }""", JobImporter.OneJobOnly, "the old batch format");
        Rejected("not json", "not valid JSON", "garbage");
        Rejected("\"just a string\"", "one job object", "a bare value");
    }

    static void IncomingFolderImportsOneJobPerFile() => WithTasksFileRestored(() => {
        var incoming = NewDir("incoming-" + Guid.NewGuid().ToString("N")[..6]);
        var imported = NewDir("imported-" + Guid.NewGuid().ToString("N")[..6]);
        var settings = new AppSettings { IncomingFolder = incoming, ImportedFolder = imported };

        File.WriteAllText(Path.Combine(incoming, "one.json"), JsonSerializer.Serialize(CanonicalJob("https://example.com/jobs/one")));
        File.WriteAllText(Path.Combine(incoming, "batch.json"),
            JsonSerializer.Serialize(new[] { CanonicalJob("https://example.com/jobs/a"), CanonicalJob("https://example.com/jobs/b") }));
        File.WriteAllText(Path.Combine(incoming, "invalid.json"),
            JsonSerializer.Serialize(new JobImportData { Company = "No title", JobUrl = "https://example.com/jobs/x", Description = "d" }));

        // The browser already has one of these jobs.
        var tasks = new List<JobTask>();
        JobImporter.ImportOne(CanonicalJob("https://example.com/jobs/one?utm_source=email"), JobImporter.BrowserSource, tasks);

        var result = JobImporter.Import(settings, tasks);

        Equal(1, result.FilesImported, "only the valid single-job file is consumed");
        Equal(0, result.JobsQueued, "its job was already imported by the browser");
        Equal(1, result.JobsExisting, "recognised as the same job across both sources");
        Equal(1, tasks.Count, "no second task for the same job");
        Check(result.Errors.Any(e => e.StartsWith("batch.json") && e.Contains(JobImporter.OneJobOnly)), "batch refused: " + string.Join(" | ", result.Errors));
        Check(result.Errors.Any(e => e.StartsWith("invalid.json") && e.Contains("Job title is required.")), "invalid refused");
        Check(File.Exists(Path.Combine(incoming, "batch.json")) && File.Exists(Path.Combine(incoming, "invalid.json")),
              "refused files stay in Incoming to be fixed");
        Check(File.Exists(Path.Combine(imported, "one.json")), "the consumed file moved to Imported");

        // A fresh single job imports as exactly one task.
        File.WriteAllText(Path.Combine(incoming, "two.json"), JsonSerializer.Serialize(CanonicalJob("https://example.com/jobs/two")));
        File.Delete(Path.Combine(incoming, "batch.json"));
        File.Delete(Path.Combine(incoming, "invalid.json"));
        var second = JobImporter.Import(settings, tasks);
        Equal(1, second.JobsQueued, "one object, one task");
        Equal(2, tasks.Count, "two tasks in total");
        Equal(ApplicationStatus.Viewed, tasks[^1].ApplicationStatus, "file imports start Viewed");
    });

    static void OldTasksStillLoad() {
        // Exactly what tasks.json held before this contract: an external-style id, a location, a
        // link, no CompanyUrl.
        var legacy = """
        [ { "JobId": "STRESS-114209-010", "Source": "stress-sample", "Company": "Lucerne Mobility",
            "Title": "Cloud Infrastructure Engineer", "Location": "Denver, CO", "Jd": "...",
            "Link": "https://example.com/sample/10", "About": "", "Status": "Completed",
            "ApplicationStatus": "Applied", "ResumePath": "C:\\Resumes\\Resume.docx" } ]
        """;
        var task = JsonSerializer.Deserialize<List<JobTask>>(legacy, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!.Single();

        Equal("STRESS-114209-010", task.JobId, "the old id is kept as the task's id");
        Equal("Denver, CO", task.Location, "an old location is kept");
        Equal("", task.CompanyUrl, "no company URL, and no crash");
        Equal(ApplicationStatus.Applied, task.ApplicationStatus, "status kept");
        Equal("https://example.com/sample/10", task.Link, "link kept");
        Check(SampleJobs.IsTestJob(task), "an old stress job is still recognised as test data");
    }

    static void SampleJobsUseTheNewContract() {
        var jobs = SampleJobs.CreateStressJobs(5, "run1");
        Equal(5, jobs.Count, "count");
        Check(jobs.All(j => j.JobUrl.StartsWith(SampleJobs.SampleUrlRoot + "run1/")), "sample URLs are per run");
        Check(jobs.Select(j => j.JobUrl).Distinct().Count() == 5, "distinct URLs");
        Check(SampleJobs.CreateStressJobs(1, "run2")[0].JobUrl != jobs[0].JobUrl, "a second run is not a duplicate of the first");
        Check(SampleJobs.IsTestJob(new JobTask { JobId = "RB-x", Link = jobs[0].JobUrl }), "new-style test jobs are recognised by URL");
        Check(!SampleJobs.IsTestJob(new JobTask { JobId = "RB-x", Link = "https://jobright.ai/jobs/info/6aac7fec95c707f49dff195f" }),
              "a real job is never mistaken for test data");
    }

    static void GenerateSample() {
        var outputDir = Path.Combine(Root(), "output", "sample-promv412");
        // Rebuilt from scratch, so the sample always shows the current folder structure.
        if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        Directory.CreateDirectory(outputDir);

        var input = Path.Combine(outputDir, "input.json");
        File.Copy(Fixture("resume-prom-v4.12.json"), input, overwrite: true);

        var settings = new AppSettings { ResumeRootFolder = outputDir, Docx = true, Pdf = true };
        var result = ResumeGenerator.Generate("Caterpillar Inc.", "Senior AI Software Engineer", input, settings,
                                              Path.Combine(outputDir, "effective-style.json"));

        Check(!result.AnyFailure, "the sample should generate cleanly: " + result.Describe());
        Check(File.Exists(result.DocxPath!), "the sample DOCX should exist");
        Check(File.Exists(result.PdfPath!), "the sample PDF should exist");
        Check(File.Exists(Path.Combine(outputDir, "effective-style.json")), "the effective style should exist");

        using var word = WordprocessingDocument.Open(result.DocxPath!, false);
        Check(new OpenXmlValidator(FileFormatVersions.Office2019).Validate(word).Any() == false, "the sample DOCX should be schema-valid");

        Console.WriteLine("        sample: " + result.DocxPath);
        Console.WriteLine("        sample: " + result.PdfPath);
    }

    // ---------- helpers ----------

    sealed class Para {
        public string StyleId = "";
        public string Text = "";
        public List<(string Text, bool Bold)> Runs = new();
        public double FontSize;
        public string Color = "";
        public string Alignment = "";
        public string FontFamily = "";
        public bool BottomBorder;
        public double LeftIndentInches;
    }

    static List<Para> ReadDocx(string path) {
        using var word = WordprocessingDocument.Open(path, false);
        var body = word.MainDocumentPart!.Document!.Body!;
        var paragraphs = new List<Para>();

        foreach (var element in body.Elements<W.Paragraph>()) {
            var para = new Para();
            var props = element.ParagraphProperties;
            para.StyleId = props?.ParagraphStyleId?.Val?.Value ?? "";
            para.Alignment = props?.Justification?.Val?.ToString() ?? "";
            para.BottomBorder = props?.ParagraphBorders?.BottomBorder is not null;
            if (props?.Indentation?.Left?.Value is string left)
                para.LeftIndentInches = int.Parse(left, CultureInfo.InvariantCulture) / 1440.0;

            foreach (var run in element.Elements<W.Run>()) {
                var text = string.Concat(run.Elements<W.Text>().Select(t => t.Text));
                para.Runs.Add((text, run.RunProperties?.Bold is not null));
                if (para.FontSize == 0) {
                    if (run.RunProperties?.FontSize?.Val?.Value is string size)
                        para.FontSize = double.Parse(size, CultureInfo.InvariantCulture) / 2.0;
                    para.Color = run.RunProperties?.Color?.Val?.Value ?? "";
                    para.FontFamily = run.RunProperties?.RunFonts?.Ascii?.Value ?? "";
                }
            }

            para.Text = string.Concat(para.Runs.Select(r => r.Text));
            paragraphs.Add(para);
        }

        return paragraphs;
    }

    static byte[] PartBytes(string docx, string partUri) {
        using var archive = System.IO.Compression.ZipFile.OpenRead(docx);
        var entry = archive.GetEntry(partUri) ?? throw new FileNotFoundException(partUri + " is missing from " + docx);
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    static string Render(string fixture, string label) => RenderFile(Fixture(fixture), label);

    static string RenderFile(string profilePath, string label) {
        var path = Path.Combine(TempRoot, label + "-" + Path.GetFileNameWithoutExtension(profilePath) + ".docx");
        DocxWriter.Write(ResumeDocument.FromProfileFile(profilePath), path);
        return path;
    }

    static ResumeStyle StyleOf(string fixture) => ResumeDocument.FromProfileFile(Fixture(fixture)).Style;

    static List<string> StyleErrors(string fixture) {
        var style = JsonNode.Parse(File.ReadAllText(Fixture(fixture)))!["style"];
        return StyleValidator.Validate(style);
    }

    /// <summary>A copy of a fixture with a different style block, written to the temp folder.</summary>
    static string WithStyle(string fixture, string styleJson, string fileName) {
        var profile = JsonNode.Parse(File.ReadAllText(Fixture(fixture)))!.AsObject();
        profile["style"] = JsonNode.Parse(styleJson);
        var path = Path.Combine(TempRoot, fileName);
        File.WriteAllText(path, profile.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    static string Root() {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ResumeBuilder.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate the ResumeBuilder project folder.");
    }

    static string Fixture(string name) => Path.Combine(Root(), "tests", "fixtures", name);

    static string NewDir(string name) {
        var path = Path.Combine(TempRoot, name);
        Directory.CreateDirectory(path);
        return path;
    }

    static string Join(IEnumerable<string> values) => string.Join(" | ", values);

    static void Test(string name, Action body) {
        try {
            body();
            Passed++;
            Console.WriteLine("  PASS  " + name);
        } catch (Exception ex) {
            Failures.Add(name);
            Console.WriteLine("  FAIL  " + name);
            Console.WriteLine("        " + ex.Message.Replace("\n", "\n        "));
        }
    }

    static void Check(bool condition, string message) {
        if (!condition) throw new Exception(message);
    }

    static void Equal(object? expected, object? actual, string what) {
        if (Equals(expected, actual)) return;
        throw new Exception($"{what}: expected <{expected}>, got <{actual}>");
    }

    static void TryCleanup() {
        try { Directory.Delete(TempRoot, recursive: true); } catch { /* a locked temp file is not a failure */ }
    }
}
