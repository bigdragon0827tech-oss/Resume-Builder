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
            Console.WriteLine("ChatGPT retries");
            Test("three sends per job, whatever the failure", RetryBudgetIsThreeSends);
            Test("each failure has a fixed reason, log line and stored reason", RetryReasonsAndLogs);
            Test("no generation within the start budget is NoResponseStart", WatcherNoResponseStart);
            Test("generation that stops progressing is Stalled", WatcherStalled);
            Test("a long answer that keeps generating is never failed", WatcherKeepsWaitingWhileGenerating);
            Test("the ambiguous idle state notifies once and keeps watching", WatcherUnconfirmedKeepsWatching);
            Test("a confirmed finish is still Ready, and cancellation still wins", WatcherReadyAndCancel);
            Test("the ambiguous state notifies without auto-copy; Ready uses background capture", UnconfirmedRequestsCopyOnce);
            Test("background reader extracts last assistant JSON and refuses ambiguity", BackgroundReaderFixtures);
            Test("background reader waits for a stable payload", BackgroundReaderStability);
            Test("background reader script is read-only", BackgroundReaderScriptIsReadOnly);
            Test("a capture after the ambiguous state cancels the 180 s timeout", UnconfirmedCaptureCancelsTimeout);
            Test("silence after the ambiguous state still ends as NoResponseStart", UnconfirmedSilenceStillTimesOut);

            Console.WriteLine();
            Console.WriteLine("Clear job history");
            Test("only this app's own job files are planned", ResetPlansOnlyOwnedFiles);
            Test("BASELINE files and other jobs' files are never planned", ResetNeverPlansBaseline);
            Test("an unsafe ResumePath is ignored; ownership decides", ResetIgnoresUnsafeResumePath);
            Test("a folder is deleted only when resume-info.json matches", ResetFolderNeedsMatchingInfo);
            Test("two jobs with the same company and role are handled", ResetHandlesDuplicateJobFolders);
            Test("documents are untouched when the box is off", ResetWithoutDocuments);
            Test("a locked file is reported, not hidden", ResetReportsFailures);
            Test("settings, profile, prompts and inputs survive byte-for-byte", ResetLeavesEverythingElseAlone);
            Test("a reset is refused while a job is Processing", ResetRefusedWhileProcessing);

            Console.WriteLine();
            Console.WriteLine("Prompt modes");
            Test("the Resume prompt is byte-for-byte what it was", ResumePromptUnchanged);
            Test("a Normal prompt comes first, unchanged, with the same payload", NormalPromptAssembly);
            Test("both modes send the same output contract", BothModesShareTheContract);
            Test("an unknown or missing mode falls back to Resume", PromptModeFallsBackToResume);
            Test("a missing Normal Prompt file is refused clearly", NormalPromptFileIsRequired);
            Test("both modes save the same prepared-request artifacts", BothModesSaveTheSameArtifacts);
            Test("a Normal-mode answer runs the whole pipeline", NormalModeAnswerRunsThePipeline);

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
            Test("no platform selection shows every job", PlatformFilterEmptySelection);
            Test("one selected platform shows only its jobs", PlatformFilterSinglePlatform);
            Test("several selected platforms match with OR", PlatformFilterOrMatching);
            Test("the platform filter combines with search, status and dates", PlatformFilterCombined);
            Test("the platform filter label and choice order", PlatformFilterLabelAndOrder);
            Test("each readiness state filters on its own; none selected shows all", ReadinessFilterSingleStates);
            Test("several readiness states match with OR", ReadinessFilterOrMatching);
            Test("readiness combines with status, search, platforms and dates; Board = List", ReadinessFilterCombined);
            Test("the readiness filter label and choice order", ReadinessFilterLabelAndOrder);
            Test("the Ready to apply card counts exactly what the filter shows", CountReadyToApplyMatchesFilter);
            Test("NeedsAction is ready to apply and not applied for yet", NeedsActionRule);
            Test("the Not applied yet group filters to Viewed and Ready only", NotAppliedYetGroupFilter);
            Test("the card count equals its own two filters", ActionQueueCardMatchesItsFilters);
            Test("Mark Applied removes a job from the action queue only", MarkAppliedLeavesActionQueue);
            Test("resume actions are safe when no resume exists", ResumeActionsAreSafe);
            Test("a resume already on disk is relinked to its job", ResumeRelinking);
            Test("Open Application uses only a usable ApplyUrl, never the job link", OpenApplicationIsSafe);
            Test("readiness: needs resume / needs apply link / ready to apply", ReadinessStates);
            Test("an unusable ApplyUrl never counts as ready", ReadinessRejectsInvalidApplyUrl);
            Test("readiness is derived: never saved and never changes a status", ReadinessIsDisplayOnly);
            Test("Mark Applied from Viewed and Ready, backfilling ReadyAt", MarkAppliedFromViewedAndReady);
            Test("Mark Applied never rewrites AppliedAt or moves a job backwards", MarkAppliedPreservesHistory);
            Test("Mark Applied leaves the queue status and readiness alone", MarkAppliedTouchesNothingElse);
            Test("stage dates and the Board date label", AppliedDateDisplay);

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
            Console.WriteLine("Job browser: application link capture");
            Test("tasks.json written before ApplyUrl existed still loads", ApplyUrlOldTasksLoad);
            Test("ApplyUrl and ApplyUrlCapturedAt survive save and reload", ApplyUrlSurvivesReload);
            Test("only an outside http(s) application address is captured", ApplyUrlCaptureRule);
            Test("the address is recorded on the matching Jobright job only", ApplyUrlMatchesJob);
            Test("the extractor reads applyLink, falling back to originalUrl", ExtractorReadsApplyLink);
            Test("a missing or invalid page link leaves ApplyUrl empty", ExtractorApplyLinkMissingOrInvalid);
            Test("import saves a discovered ApplyUrl with its time and platform", ImportRecordsApplyUrl);
            Test("import without a usable link still succeeds, link empty", ImportWithoutApplyUrl);
            Test("an existing ApplyUrl is never overwritten; an empty one is filled", ImportFillsOnlyEmptyApplyUrl);

            Console.WriteLine();
            Console.WriteLine("Application platform detection");
            Test("each known ATS address maps to its platform", PlatformMatching);
            Test("look-alike domains are not mistaken for an ATS", PlatformLookAlikes);
            Test("embedded Greenhouse / Ashby job links are recognised", PlatformEmbeddedLinks);
            Test("a missing or unusable ApplyUrl is Unknown; an unrecognised site is Other", PlatformMissingOrUnknown);
            Test("an unreadable stored platform loads as Unknown and keeps every task", PlatformTolerantLoading);
            Test("the platform survives save and reload; older tasks load as Unknown", PlatformSaveReload);
            Test("capture and startup refresh derive the platform from ApplyUrl", PlatformDerivedFromApplyUrl);

            Console.WriteLine();
            Console.WriteLine("Critical pipeline (isolated — no live ChatGPT / Jobright)");
            Test("import → prepare → capture → DOCX/PDF → Ready tracking", CriticalPipelineChain);
            Test("capture refuses empty, non-profile and prompt-echo text", CaptureGateRefusals);
            Test("page navigation must not recreate ChatGPT while it is alive", ChatHostSurvivesNavigation);
            Test("queue recovers stale Processing jobs and attributes captures correctly", QueueRunnerCaptureAttribution);
            Test("ChatHost desync (alive without CoreWebView2) is detected", ChatHostDesyncDetection);
            Test("recycle then ensure creates a fresh browser for the next job", ChatHostRecycleThenEnsure);
            Test("copy shortcut VK is OEM_1 (semicolon), not letter I", CopyShortcutIsSemicolon);
            Test("composer readiness waits until the probe reports ready", ComposerReadyWaitsForProbe);
            Test("Start refuses while a queue run is already active", QueueStartRefusesWhileRunning);
            Test("Next never reselects a Failed job after it was marked terminal", QueueNextSkipsFailed);
            Test("clipboard sequence change detection for copy-keystroke verification", ClipboardChangedSinceArm);
            Test("browser generation invalidates delayed focus after dispose", BrowserGenerationInvalidatesStaleOps);
            Test("copy-focus policy skips focus after capture path already focused", CopyFocusPolicyDecisions);
            Test("stale generation cannot touch a replacement browser", StaleGenerationCannotTouchReplacement);

            Console.WriteLine();
            Console.WriteLine("Theme input readability");
            Test("dark and light input ink contrast against Bg.Input", ThemeInputContrast);
            Test("shared control styles bind inputs with DynamicResource", ThemeControlStylesUseDynamicResources);

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

    // ---------- ChatGPT retries ----------

    static void RetryBudgetIsThreeSends() {
        Equal(3, GptAttempts.MaxAttempts, "three sends per job");
        Check(GptAttempts.CanRetry(1) && GptAttempts.CanRetry(2), "attempts 1 and 2 retry");
        Check(!GptAttempts.CanRetry(3), "attempt 3 is the last");
        Equal(AttemptDecision.Fail, GptAttempts.Decide(3), "attempt 3 fails the job");
        Equal(AttemptDecision.Fail, GptAttempts.Decide(4), "and anything beyond it");

        // Every retryable class shares the ONE budget: mixing them cannot exceed three sends.
        var sends = 1;
        foreach (var failure in new[] { GptFailure.SendSide, GptFailure.ResponseStartTimeout,
                                        GptFailure.InvalidOutput, GptFailure.ResponseStalled }) {
            if (!GptAttempts.CanRetry(sends)) break;
            sends++;
        }
        Equal(3, sends, "four failures still mean three sends");

        // The status line the queue shows.
        Equal("Processing", GptAttempts.AttemptStatus(1), "first attempt is plain");
        Equal("Processing — GPT attempt 2/3", GptAttempts.AttemptStatus(2), "second attempt is numbered");
        Equal("Processing — GPT attempt 3/3", GptAttempts.AttemptStatus(3), "third attempt");
    }

    static void RetryReasonsAndLogs() {
        const string jobId = "RB-20260919-101500-abcdef01";
        var expected = new Dictionary<GptFailure, (string Reason, string Stored)> {
            [GptFailure.SendSide] = ("send failed", "GptSendFailed"),
            [GptFailure.ResponseStartTimeout] = ("response-start timeout", "GptNoResponse"),
            [GptFailure.ResponseStalled] = ("response stalled", "GptStalled"),
            [GptFailure.ResponseCeiling] = ("response ceiling reached", "GptStalled"),
            [GptFailure.InvalidOutput] = ("invalid output", "GptInvalidOutput"),
        };

        foreach (var (failure, (reason, stored)) in expected) {
            Equal(reason, GptAttempts.Reason(failure), failure + " reason");
            Equal(stored, GptAttempts.FailureReason(failure), failure + " stored reason");
            var line = GptAttempts.AttemptLog(2, failure, jobId);
            Equal($"GPT attempt 2/3 {reason} {jobId}", line, failure + " log line");
            Check(line.Contains(jobId, StringComparison.Ordinal), "the log names the job");
        }
        Equal($"GPT retries exhausted {jobId}; queue job marked Failed", GptAttempts.ExhaustedLog(jobId), "exhausted log");
        Equal($"GPT capture timeout {jobId}; queue job marked Failed", GptAttempts.CaptureTimeoutLog(jobId), "capture timeout log");

        // A stored reason is a short token, never free text, and CaptureTimeout keeps its own.
        foreach (var failure in expected.Keys)
            Check(!GptAttempts.FailureReason(failure).Contains(' '), failure + " stored reason is a token");
        Check(expected.Values.All(v => v.Stored != JobTask.CaptureTimeoutReason), "none collides with CaptureTimeout");
    }

    /// <summary>A probe driven by a script of states, with a virtual clock: no browser, no waiting.</summary>
    sealed class ScriptedProbe : ICompletionProbe {
        readonly Func<int, string> _state;
        public int Polls;
        public ScriptedProbe(Func<int, string> state) => _state = state;
        public Task<string> GenerationStateAsync() => Task.FromResult(_state(Polls++));
    }

    static (CompletionOutcome Outcome, int Polls, int Unconfirmed) RunWatcher(Func<int, string> state, int? cancelAfterPolls = null) {
        var probe = new ScriptedProbe(state);
        var cancellation = new CancellationTokenSource();
        var unconfirmed = 0;
        Task Delay(int ms, CancellationToken ct) {
            if (cancelAfterPolls is int limit && probe.Polls >= limit) cancellation.Cancel();
            return Task.CompletedTask;                       // virtual clock: the watcher counts the ms itself
        }
        var outcome = ChatCompletionWatcher.WaitForAnswerAsync(probe, cancellation.Token, Delay, () => unconfirmed++)
                          .GetAwaiter().GetResult();
        return (outcome, probe.Polls, unconfirmed);
    }

    static void WatcherNoResponseStart() {
        Equal(180_000, ChatCompletionWatcher.ResponseStartMs, "response-start budget is 180 s");

        // Idle for ever: notified once at the start budget, then failed at 180 s. Never Ready.
        var run = RunWatcher(_ => "idle");
        Equal(CompletionOutcome.NoResponseStart, run.Outcome, "no generation -> NoResponseStart");
        Equal(1, run.Unconfirmed, "the user was told once, at the start budget");
        Equal(ChatCompletionWatcher.ResponseStartMs / ChatCompletionWatcher.PollMs + 1, run.Polls, "it waited the full budget");

        // An unreadable page is treated the same way: it proves nothing.
        Equal(CompletionOutcome.NoResponseStart, RunWatcher(_ => "unknown").Outcome, "unknown -> NoResponseStart");
        Equal(0, RunWatcher(_ => "unknown").Unconfirmed, "and an unreadable page is not announced as ready");
    }

    static void WatcherStalled() {
        Equal(120_000, ChatCompletionWatcher.InactivityMs, "inactivity budget is 120 s");

        // Generates for 10 polls, then goes quiet without ever finishing (unknown, not idle).
        var run = RunWatcher(poll => poll < 10 ? "generating" : "unknown");
        Equal(CompletionOutcome.Stalled, run.Outcome, "started then stalled");
        var expected = 10 + ChatCompletionWatcher.InactivityMs / ChatCompletionWatcher.PollMs;
        Check(Math.Abs(run.Polls - expected) <= 2, $"it failed ~120 s after the last progress (polls {run.Polls}, expected ~{expected})");

        // Stalling is measured from the LAST generating poll, not from the start.
        var late = RunWatcher(poll => poll is < 5 or (> 60 and < 65) ? "generating" : "unknown");
        Equal(CompletionOutcome.Stalled, late.Outcome, "still stalls eventually");
        Check(late.Polls > 60 + ChatCompletionWatcher.InactivityMs / ChatCompletionWatcher.PollMs - 2,
              "the inactivity clock restarts on every sign of progress");
    }

    static void WatcherKeepsWaitingWhileGenerating() {
        // A very long answer: generating almost to the ceiling, then finishing normally.
        var ceiling = ChatCompletionWatcher.MaxWaitMs / ChatCompletionWatcher.PollMs;
        var run = RunWatcher(poll => poll < ceiling - 10 ? "generating" : "idle");
        Equal(CompletionOutcome.Ready, run.Outcome, "a long answer that keeps generating is never failed");

        // Generating for ever: only the absolute ceiling stops it.
        var forever = RunWatcher(_ => "generating");
        Equal(CompletionOutcome.TimedOut, forever.Outcome, "the 20-minute ceiling is the backstop");
        Check(forever.Polls >= ceiling, "and it really waited that long");
    }

    static void WatcherUnconfirmedKeepsWatching() {
        // Idle at first (generation missed), then generation appears and finishes: Ready, not a failure.
        var run = RunWatcher(poll => poll < 40 ? "idle" : poll < 60 ? "generating" : "idle");
        Equal(CompletionOutcome.Ready, run.Outcome, "a late start after the ambiguous state still completes");
        Equal(1, run.Unconfirmed, "the ambiguous state was announced exactly once");
        Check(run.Polls < ChatCompletionWatcher.ResponseStartMs / ChatCompletionWatcher.PollMs,
              "and it finished before the response-start budget");

        // The notification fires once only, at the start budget, never before it.
        var early = RunWatcher(poll => poll < 5 ? "idle" : "generating");
        Equal(0, early.Unconfirmed, "nothing is announced before the start budget");
    }

    static void WatcherReadyAndCancel() {
        // Generation seen, then three idle polls: the confirmed finish that starts the capture watchdog.
        var run = RunWatcher(poll => poll < 3 ? "generating" : "idle");
        Equal(CompletionOutcome.Ready, run.Outcome, "generation then idle = Ready");
        Equal(3 + ChatCompletionWatcher.StablePolls, run.Polls, "it needs three stable idle polls");

        // Two idle polls are not enough.
        Equal(CompletionOutcome.Ready, RunWatcher(poll => poll is 0 or 4 ? "generating" : "idle").Outcome, "a flicker does not finish it early");

        // Cancellation (capture, Stop, Skip, retry) always wins.
        Equal(CompletionOutcome.Cancelled, RunWatcher(_ => "generating", cancelAfterPolls: 5).Outcome, "cancelled while generating");
        Equal(CompletionOutcome.Cancelled, RunWatcher(_ => "idle", cancelAfterPolls: 5).Outcome, "cancelled while idle");
    }

    /// <summary>
    /// Mirrors the new MainWindow policy: unconfirmed notifies only (no auto-copy / no scrape);
    /// confirmed Ready is when background capture would run (counted as CaptureAttempts here).
    /// </summary>
    static (CompletionOutcome Outcome, int CaptureAttempts, int Announcements, int Polls) RunWatcherWithGate(
        Func<int, string> state, int? captureAfterPolls = null) {

        var probe = new ScriptedProbe(state);
        var cancellation = new CancellationTokenSource();
        var captures = 0;
        var announcements = 0;

        Task Delay(int ms, CancellationToken ct) {
            if (captureAfterPolls is int at && probe.Polls >= at) cancellation.Cancel();
            return Task.CompletedTask;
        }

        var outcome = ChatCompletionWatcher.WaitForAnswerAsync(probe, cancellation.Token, Delay, () => {
            announcements++;
            // OnUnconfirmedReady: notify only — no RequestCopyAsync, no page scrape.
        }).GetAwaiter().GetResult();

        if (outcome == CompletionOutcome.Ready)
            captures++;   // TryBackgroundCaptureAsync would run here
        return (outcome, captures, announcements, probe.Polls);
    }

    static void UnconfirmedRequestsCopyOnce() {
        // Ambiguous, then generation appears and finishes: notified once, scrape only at Ready.
        var late = RunWatcherWithGate(poll => poll < 40 ? "idle" : poll < 60 ? "generating" : "idle");
        Equal(CompletionOutcome.Ready, late.Outcome, "it still completes normally");
        Equal(1, late.Announcements, "announced once at the start budget");
        Equal(1, late.CaptureAttempts, "background capture runs once at confirmed Ready");

        // A normal confirmed answer: one background capture, no ambiguous announcement.
        var normal = RunWatcherWithGate(poll => poll < 5 ? "generating" : "idle");
        Equal(CompletionOutcome.Ready, normal.Outcome, "normal answer");
        Equal(0, normal.Announcements, "no ambiguous announcement");
        Equal(1, normal.CaptureAttempts, "one background capture from Ready");
    }

    static void UnconfirmedCaptureCancelsTimeout() {
        // Idle for ever, but a capture (manual or background) lands after the ambiguous announcement.
        var captured = RunWatcherWithGate(_ => "idle", captureAfterPolls: 40);
        Equal(CompletionOutcome.Cancelled, captured.Outcome, "a capture cancels the watch");
        Equal(0, captured.CaptureAttempts, "Ready never fired — no background scrape");
        Check(captured.Polls < ChatCompletionWatcher.ResponseStartMs / ChatCompletionWatcher.PollMs,
              "and it ended well before the response-start budget");

        var fast = RunWatcherWithGate(_ => "idle", captureAfterPolls: 5);
        Equal(CompletionOutcome.Cancelled, fast.Outcome, "cancelled early");
        Equal(0, fast.CaptureAttempts, "no Ready scrape");
    }

    static void BackgroundReaderFixtures() {
        var direct = ChatResponseReader.ParseScriptPayload(
            """{"status":"ok","text":"```json\n{\"info\":{\"fullName\":\"A\"},\"summary\":\"S\",\"skills\":[],\"experience\":[],\"education\":[],\"certifications\":[]}\n```","assistants":1}""");
        Check(direct.Success, "direct object payload ok");
        Check(ResultCapture.LooksLikeProfileResult(direct.Text), "extracted text looks like a profile");

        // ExecuteScriptAsync string-wrap: outer JSON string containing the object JSON.
        var wrapped = ChatResponseReader.ParseScriptPayload(
            JsonSerializer.Serialize(
                """{"status":"ok","text":"```json\n{\"info\":{\"fullName\":\"B\"},\"summary\":\"S\",\"skills\":[],\"experience\":[],\"education\":[],\"certifications\":[]}\n```","assistants":1}"""));
        Check(wrapped.Success, "string-wrapped payload ok");

        var missing = ChatResponseReader.ParseScriptPayload("""{"status":"missing","text":"","assistants":0}""");
        Equal(ChatReadStatus.Missing, missing.Status, "missing");

        var empty = ChatResponseReader.ParseScriptPayload("""{"status":"empty","text":"","assistants":1}""");
        Equal(ChatReadStatus.Empty, empty.Status, "empty");

        var amb = ChatResponseReader.ParseScriptPayload("""{"status":"ambiguous","text":"","assistants":2}""");
        Equal(ChatReadStatus.Ambiguous, amb.Status, "ambiguous refused");

        var prose = ChatResponseReader.ParseScriptPayload(
            """{"status":"ok","text":"Sure, here is a helpful tip about resumes.","assistants":1}""");
        Check(prose.Success, "script said ok");
        Check(!ResultCapture.ShouldCapture(prose.Text), "prose is not a profile");
    }

    static void BackgroundReaderStability() {
        var profile = "```json\n{\"info\":{\"fullName\":\"A\"},\"summary\":\"S\",\"skills\":[],\"experience\":[],\"education\":[],\"certifications\":[]}\n```";
        var calls = 0;
        var stable = ChatResponseReader.ReadStableAsync(
            _ => {
                calls++;
                // First read shorter (still streaming), then two identical complete payloads.
                var text = calls == 1 ? profile[..Math.Min(80, profile.Length)] : profile;
                return Task.FromResult(new ChatReadResult {
                    Status = ChatReadStatus.Ok, Text = text, AssistantCount = 1
                });
            },
            budgetMs: 5000,
            pollMs: 10,
            matchPolls: 2,
            delay: (_, _) => Task.CompletedTask).GetAwaiter().GetResult();
        Check(stable.Success, "became stable");
        Check(calls >= 3, "needed more than one poll");
        Check(ResultCapture.LooksLikeProfileResult(stable.Text), "stable text is a profile");

        var never = ChatResponseReader.ReadStableAsync(
            _ => Task.FromResult(new ChatReadResult { Status = ChatReadStatus.Missing }),
            budgetMs: 50,
            pollMs: 10,
            delay: (_, _) => Task.CompletedTask).GetAwaiter().GetResult();
        Equal(ChatReadStatus.Missing, never.Status, "budget expiry without a payload");
    }

    static void BackgroundReaderScriptIsReadOnly() {
        Check(ChatResponseReader.ScriptIsReadOnly(ChatResponseReader.ReadLastAssistantScript), "read-only");
        Check(ChatResponseReader.ReadLastAssistantScript.Contains("data-message-author-role"), "targets assistant role");
        Check(!ChatResponseReader.ReadLastAssistantScript.Contains("click(", StringComparison.OrdinalIgnoreCase), "no click");
        Check(!ChatResponseReader.ReadLastAssistantScript.Contains("fetch(", StringComparison.OrdinalIgnoreCase), "no fetch");
    }

    static void UnconfirmedSilenceStillTimesOut() {
        var silent = RunWatcherWithGate(_ => "idle");
        Equal(CompletionOutcome.NoResponseStart, silent.Outcome, "nothing usable -> NoResponseStart");
        Equal(1, silent.Announcements, "notified once at the start budget");
        Equal(0, silent.CaptureAttempts, "no Ready — no background scrape");
        Equal(ChatCompletionWatcher.ResponseStartMs / ChatCompletionWatcher.PollMs + 1, silent.Polls,
              "and only then, at 180 s");

        Equal(CompletionOutcome.Cancelled, RunWatcherWithGate(_ => "generating", captureAfterPolls: 3).Outcome,
              "cancelled while generating");
    }

    // ---------- clear job history ----------

    /// <summary>A throwaway world: results folder, prepared-request pair, resume root with real folders.</summary>
    sealed class ResetWorld {
        public string Dir = "", Results = "", Prepared = "", PreparedText = "", Root = "";
        public AppSettings Settings = new();
        public ResetPaths Paths = new();
        public List<JobTask> Tasks = new();

        public string JobFolder(JobTask job, string date) =>
            Path.Combine(Root, date, ResumeOutputManager.JobFolderName(job.Company, job.Title));
    }

    static ResetWorld NewResetWorld(string name) {
        var dir = NewDir("reset-" + name);
        var w = new ResetWorld {
            Dir = dir,
            Results = Path.Combine(dir, "results"),
            Prepared = Path.Combine(dir, "prepared-request.json"),
            PreparedText = Path.Combine(dir, "prepared-request.txt"),
            Root = Path.Combine(dir, "Resumes")
        };
        Directory.CreateDirectory(w.Results);
        Directory.CreateDirectory(w.Root);
        w.Paths = new ResetPaths { ResultsDir = w.Results, PreparedRequestPath = w.Prepared, PreparedRequestTextPath = w.PreparedText };
        w.Settings = new AppSettings { ResumeRootFolder = w.Root };
        File.WriteAllText(w.Prepared, "{}");
        File.WriteAllText(w.PreparedText, "prompt");
        return w;
    }

    /// <summary>Gives a job its four result files and, optionally, a generated folder for a date.</summary>
    static JobTask ResetJob(ResetWorld w, string id, string company = "Acme", string title = "Engineer",
                            string status = "Completed", string? documentsOn = null, string? infoJobId = null) {
        var job = new JobTask { JobId = id, Company = company, Title = title, Jd = "...", Status = status };
        foreach (var suffix in new[] { ".json", ".raw.txt", ".docgen.txt", ".effective-style.json" })
            File.WriteAllText(Path.Combine(w.Results, id + suffix), "x");

        if (documentsOn is string date) {
            var folder = w.JobFolder(job, date);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Resume.docx"), "docx");
            File.WriteAllText(Path.Combine(folder, "Resume.pdf"), "pdf");
            File.WriteAllText(Path.Combine(folder, "resume-info.json"),
                $$"""{ "jobId": "{{infoJobId ?? id}}", "company": "{{company}}", "role": "{{title}}" }""");
            job.ResumePath = Path.Combine(folder, "Resume.docx");
        }
        w.Tasks.Add(job);
        return job;
    }

    static void ResetPlansOnlyOwnedFiles() {
        var w = NewResetWorld("owned");
        var a = ResetJob(w, "RB-1", status: "Completed");
        var b = ResetJob(w, "RB-2", "Stripe", "Backend Engineer", "Queued");
        File.WriteAllText(Path.Combine(w.Results, "RB-STRANGER.json"), "someone else's");

        var plan = JobHistoryReset.Plan(w.Tasks, w.Settings, includeDocuments: false, w.Paths);
        Equal(2, plan.JobCount, "both jobs are in the plan");
        Equal("2 jobs (1 queued, 1 completed)", plan.JobSummary(), "the summary the dialog shows");
        Equal(10, plan.Files.Count, "4 files per job plus the two prepared-request files");
        Check(plan.Files.All(f => JobHistoryReset.IsUnderRoot(f, w.Results) || f == w.Prepared || f == w.PreparedText),
              "every planned file is inside this app's own folders");
        Check(!plan.Files.Any(f => f.Contains("STRANGER")), "another job's result file is never planned");
        Equal(0, plan.Folders.Count, "no documents were requested");

        var report = JobHistoryReset.Execute(plan, w.Paths);
        Equal(10, report.FilesDeleted, "all planned files deleted");
        Check(!report.AnyFailure, "no failures");
        Check(File.Exists(Path.Combine(w.Results, "RB-STRANGER.json")), "the stranger's file survives");
        Check(!File.Exists(w.Prepared) && !File.Exists(w.PreparedText), "prepared-request files are gone");
        foreach (var job in new[] { a, b })
            Check(!Directory.GetFiles(w.Results, job.JobId + "*").Any(), job.JobId + "'s results are gone");
    }

    static void ResetNeverPlansBaseline() {
        var w = NewResetWorld("baseline");
        ResetJob(w, "RB-1");
        foreach (var suffix in new[] { ".json", ".raw.txt", ".effective-style.json" })
            File.WriteAllText(Path.Combine(w.Results, ResultCapture.BaselineJobId + suffix), "baseline");

        // Even a task carrying the reserved id must not pull the baseline files in.
        w.Tasks.Add(new JobTask { JobId = ResultCapture.BaselineJobId, Company = "X", Title = "Y" });
        w.Tasks.Add(new JobTask { JobId = "   ", Company = "X", Title = "Y" });

        var plan = JobHistoryReset.Plan(w.Tasks, w.Settings, includeDocuments: false, w.Paths);
        Check(!plan.Files.Any(f => Path.GetFileName(f).StartsWith(ResultCapture.BaselineJobId, StringComparison.OrdinalIgnoreCase)),
              "BASELINE.* is never planned");
        JobHistoryReset.Execute(plan, w.Paths);
        foreach (var suffix in new[] { ".json", ".raw.txt", ".effective-style.json" })
            Check(File.Exists(Path.Combine(w.Results, ResultCapture.BaselineJobId + suffix)), "BASELINE" + suffix + " survives");
    }

    static void ResetIgnoresUnsafeResumePath() {
        var w = NewResetWorld("unsafe");
        var outside = NewDir("reset-unsafe-outside");
        File.WriteAllText(Path.Combine(outside, "precious.docx"), "not ours");

        // The task claims a document far outside the Resume Root; it must count for nothing.
        var job = ResetJob(w, "RB-1");
        job.ResumePath = Path.Combine(outside, "precious.docx");

        var plan = JobHistoryReset.Plan(w.Tasks, w.Settings, includeDocuments: true, w.Paths);
        Equal(0, plan.Folders.Count, "a ResumePath outside the root plans no folder");
        Check(!plan.Files.Any(f => f.Contains("precious")), "and no file");
        JobHistoryReset.Execute(plan, w.Paths);
        Check(File.Exists(Path.Combine(outside, "precious.docx")), "the outside file is untouched");

        // The guard itself.
        Check(JobHistoryReset.IsUnderRoot(Path.Combine(w.Root, "2026-09-19", "Acme - Engineer"), w.Root), "a real child is under the root");
        Check(!JobHistoryReset.IsUnderRoot(w.Root, w.Root), "the root itself is not 'under' the root");
        Check(!JobHistoryReset.IsUnderRoot(Path.Combine(w.Root, "..", "elsewhere"), w.Root), "a path that climbs out is refused");
        Check(!JobHistoryReset.IsUnderRoot(@"C:\Windows\System32", w.Root), "an unrelated path is refused");
        Check(!JobHistoryReset.IsUnderRoot("", w.Root) && !JobHistoryReset.IsUnderRoot(w.Root, ""), "empty paths are refused");
    }

    static void ResetFolderNeedsMatchingInfo() {
        var w = NewResetWorld("folders");
        const string date = "2026-09-19";
        var mine = ResetJob(w, "RB-MINE", "Cogniify", "AI Engineer", documentsOn: date);
        var wrongId = ResetJob(w, "RB-WRONG", "Stripe", "Backend Engineer", documentsOn: date, infoJobId: "RB-SOMEONE-ELSE");
        var noInfo = ResetJob(w, "RB-NOINFO", "Acme", "Engineer", documentsOn: date);
        File.Delete(Path.Combine(w.JobFolder(noInfo, date), "resume-info.json"));

        // A folder under a NON-date parent, and a stranger's folder, must both be ignored.
        var notDated = Path.Combine(w.Root, "Archive", ResumeOutputManager.JobFolderName("Cogniify", "AI Engineer"));
        Directory.CreateDirectory(notDated);
        File.WriteAllText(Path.Combine(notDated, "resume-info.json"), """{ "jobId": "RB-MINE" }""");
        var stranger = Path.Combine(w.Root, date, "Someone Else - Role");
        Directory.CreateDirectory(stranger);
        File.WriteAllText(Path.Combine(stranger, "Resume.docx"), "theirs");

        var plan = JobHistoryReset.Plan(w.Tasks, w.Settings, includeDocuments: true, w.Paths);
        Equal(1, plan.Folders.Count, "only the folder whose resume-info.json matches");
        Equal(w.JobFolder(mine, date), plan.Folders[0], "and it is the right one");

        var report = JobHistoryReset.Execute(plan, w.Paths);
        Equal(1, report.FoldersDeleted, "one folder deleted");
        Check(!Directory.Exists(w.JobFolder(mine, date)), "the matching folder is gone");
        Check(Directory.Exists(w.JobFolder(wrongId, date)), "a folder whose info names another job survives");
        Check(Directory.Exists(w.JobFolder(noInfo, date)), "a folder with no resume-info.json survives");
        Check(Directory.Exists(notDated), "a folder outside a yyyy-MM-dd parent survives");
        Check(Directory.Exists(stranger), "a stranger's folder survives");
        Equal(0, report.DateFoldersRemoved, "the date folder still holds other folders, so it stays");
        Check(Directory.Exists(Path.Combine(w.Root, date)), "the date folder survives");

        // With nothing left in it, the date folder is tidied away.
        var solo = NewResetWorld("folders-solo");
        var only = ResetJob(solo, "RB-ONLY", documentsOn: date);
        var soloReport = JobHistoryReset.Execute(JobHistoryReset.Plan(solo.Tasks, solo.Settings, true, solo.Paths), solo.Paths);
        Equal(1, soloReport.DateFoldersRemoved, "an empty date folder is removed");
        Check(!Directory.Exists(Path.Combine(solo.Root, date)), "and it is gone");
        Check(Directory.Exists(solo.Root), "the Resume Root itself is never deleted");
    }

    /// <summary>Re-imported jobs share a company and role, so one folder name maps to several job ids.</summary>
    static void ResetHandlesDuplicateJobFolders() {
        var w = NewResetWorld("duplicates");
        const string company = "Caterpillar Inc.", title = "Senior AI Software Engineer";
        var first = ResetJob(w, "RB-DUP-1", company, title, documentsOn: "2026-09-18");
        var second = ResetJob(w, "RB-DUP-2", company, title, documentsOn: "2026-09-19");
        Equal(ResumeOutputManager.JobFolderName(company, title),
              ResumeOutputManager.JobFolderName(company, title), "both jobs map to one folder name");

        var plan = JobHistoryReset.Plan(w.Tasks, w.Settings, includeDocuments: true, w.Paths);   // must not throw
        Equal(2, plan.Folders.Count, "both dated folders are planned");
        Equal(8, plan.Files.Count - 2, "and both jobs' result files (plus the prepared pair)");
        var report = JobHistoryReset.Execute(plan, w.Paths);
        Check(!report.AnyFailure && report.FoldersDeleted == 2, "both folders deleted: " + report.Describe());
        Check(!Directory.Exists(w.JobFolder(first, "2026-09-18")) && !Directory.Exists(w.JobFolder(second, "2026-09-19")), "gone");

        // One folder holding revisions of a CLEARED job and a KEPT job is left alone.
        var mixed = NewResetWorld("duplicates-mixed");
        var cleared = ResetJob(mixed, "RB-KEEP-A", company, title, documentsOn: "2026-09-19");
        var folder = mixed.JobFolder(cleared, "2026-09-19");
        File.WriteAllText(Path.Combine(folder, "resume-info (2).json"), """{ "jobId": "RB-NOT-CLEARED" }""");
        var mixedPlan = JobHistoryReset.Plan(mixed.Tasks, mixed.Settings, includeDocuments: true, mixed.Paths);
        Equal(0, mixedPlan.Folders.Count, "a folder shared with a job that stays is never deleted");
        JobHistoryReset.Execute(mixedPlan, mixed.Paths);
        Check(Directory.Exists(folder), "and it survives");
    }

    static void ResetWithoutDocuments() {
        var w = NewResetWorld("nodocs");
        var job = ResetJob(w, "RB-1", documentsOn: "2026-09-19");

        var plan = JobHistoryReset.Plan(w.Tasks, w.Settings, includeDocuments: false, w.Paths);
        Check(!plan.IncludesDocuments && plan.Folders.Count == 0 && plan.DateFolders.Count == 0, "no folders planned");
        var report = JobHistoryReset.Execute(plan, w.Paths);
        Equal(0, report.FoldersDeleted, "no folder deleted");
        Check(File.Exists(job.ResumePath), "the DOCX is still there");
        Check(File.Exists(Path.ChangeExtension(job.ResumePath, ".pdf")), "the PDF is still there");
        Equal(6, report.FilesDeleted, "results and prepared-request files were still cleared");
    }

    static void ResetReportsFailures() {
        var w = NewResetWorld("failures");
        ResetJob(w, "RB-1");
        var locked = Path.Combine(w.Results, "RB-1.json");
        var plan = JobHistoryReset.Plan(w.Tasks, w.Settings, includeDocuments: false, w.Paths);

        ResetReport report;
        using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            report = JobHistoryReset.Execute(plan, w.Paths);

        Check(report.AnyFailure, "the locked file is reported as a failure");
        Equal(1, report.Failures.Count, "exactly one failure");
        Equal(locked, report.Failures[0].Path, "and it names the file");
        Check(report.Failures[0].Reason.Length > 0, "with a reason: " + report.Failures[0].Reason);
        Check(report.Describe().Contains("could NOT be deleted", StringComparison.Ordinal), "the message says so plainly");
        Check(report.FilesDeleted == plan.Files.Count - 1, "the others were still deleted");
        Check(File.Exists(locked), "and the locked file is still there");
    }

    static void ResetLeavesEverythingElseAlone() {
        var w = NewResetWorld("survivors");
        ResetJob(w, "RB-1", documentsOn: "2026-09-19");

        // Files that must never be involved, written next to the ones that are.
        var survivors = new Dictionary<string, string> {
            [Path.Combine(w.Dir, "settings.json")] = """{ "PromptMode": "Normal", "NormalPrompt": "C:\\mine.txt" }""",
            [Path.Combine(w.Dir, "candidate-profile.json")] = """{ "info": {} }""",
            [Path.Combine(w.Dir, "baseline-profile.json")] = "baseline",
            [Path.Combine(w.Dir, "diagnostics.log")] = "log line",
            [Path.Combine(w.Dir, "my-master-prompt.txt")] = "master",
            [Path.Combine(w.Dir, "my-normal-prompt.txt")] = "normal",
        };
        var browser = Path.Combine(w.Dir, "JobBrowserWebView2");
        Directory.CreateDirectory(browser);
        survivors[Path.Combine(browser, "Cookies")] = "session";
        var incoming = Path.Combine(w.Dir, "Incoming"); Directory.CreateDirectory(incoming);
        survivors[Path.Combine(incoming, "job-1.json")] = "{}";
        var imported = Path.Combine(w.Dir, "Imported"); Directory.CreateDirectory(imported);
        survivors[Path.Combine(imported, "job-0.json")] = "{}";
        foreach (var (path, text) in survivors) File.WriteAllText(path, text);
        var before = survivors.Keys.ToDictionary(p => p, File.ReadAllBytes);

        JobHistoryReset.Execute(JobHistoryReset.Plan(w.Tasks, w.Settings, includeDocuments: true, w.Paths), w.Paths);

        foreach (var (path, bytes) in before) {
            Check(File.Exists(path), "still exists: " + Path.GetFileName(path));
            Check(File.ReadAllBytes(path).SequenceEqual(bytes), "byte-identical: " + Path.GetFileName(path));
        }
        Check(Directory.Exists(browser) && Directory.Exists(incoming) && Directory.Exists(imported),
              "the browser profile and the input folders survive");
    }

    static void ResetRefusedWhileProcessing() {
        var w = NewResetWorld("processing");
        ResetJob(w, "RB-1", status: "Completed");
        Check(!JobHistoryReset.IsProcessing(w.Tasks), "no job in flight");

        ResetJob(w, "RB-2", status: "Processing");
        Check(JobHistoryReset.IsProcessing(w.Tasks), "a Processing job blocks the reset");
        Check(JobHistoryReset.IsProcessing(new List<JobTask>()) == false, "an empty list is fine");

        // The plan itself stays honest about what it would cover.
        var plan = JobHistoryReset.Plan(w.Tasks, w.Settings, includeDocuments: false, w.Paths);
        Check(plan.JobSummary().Contains("1 processing", StringComparison.Ordinal), "the summary names it: " + plan.JobSummary());
    }

    // ---------- prompt modes ----------

    /// <summary>The job the golden fixture was captured with. Any change here invalidates the fixture.</summary>
    static JobTask PromptJob() => new() {
        JobId = "RB-20260919-101500-abcdef01",
        Company = "Cogniify",
        Title = "Senior Generative AI Engineer",
        Jd = "Build and ship generative AI features.\nOwn evaluation and reliability.",
        Link = "https://jobright.ai/jobs/info/6aada17fde327d3e210d3913",
        About = "Cogniify builds AI tooling for small teams."
    };

    static AppSettings PromptSettings(string mode, string? normalPrompt = null) => new() {
        MasterPrompt = Fixture("prepare-master-prompt.txt"),
        CandidateProfile = Fixture("prepare-profile.json"),
        PromptMode = mode,
        NormalPrompt = normalPrompt ?? ""
    };

    /// <summary>Prepare writes prepared-request.json/.txt in the live data folder; both are restored.</summary>
    static void WithPreparedFiles(Action body) {
        var paths = new[] { RequestPreparation.PreparedPath, RequestPreparation.PreparedTextPath };
        var backup = paths.ToDictionary(p => p, p => File.Exists(p) ? File.ReadAllBytes(p) : null);
        try { body(); }
        finally {
            foreach (var (p, b) in backup) { if (b is not null) File.WriteAllBytes(p, b); else if (File.Exists(p)) File.Delete(p); }
        }
    }

    static string NormalPromptFile(string text) {
        var path = Path.Combine(NewDir("normal-prompt-" + Math.Abs(text.GetHashCode())), "my-prompt.txt");
        File.WriteAllText(path, text);
        return path;
    }

    static void ResumePromptUnchanged() => WithPreparedFiles(() => {
        // The fixture was captured from the build BEFORE prompt modes existed.
        var expected = File.ReadAllText(Fixture("prepare-resume-expected.txt"));
        var prepared = RequestPreparation.Prepare(PromptJob(), PromptSettings(PromptModes.Resume));
        Equal(expected.Length, prepared.Text.Length, "prepared length");
        Check(expected == prepared.Text, "the Resume prompt must be byte-for-byte unchanged");

        // The same through the explicit entry point, and with no mode stored at all (older settings).
        Check(RequestPreparation.PrepareResume(PromptJob(), PromptSettings(PromptModes.Resume)).Text == expected, "PrepareResume");
        var older = PromptSettings(PromptModes.Resume); older.PromptMode = "";
        Check(RequestPreparation.Prepare(PromptJob(), older).Text == expected, "older settings with no mode");

        // The job identity travels with the request, as before.
        Equal("RB-20260919-101500-abcdef01", prepared.JobId, "job id");
        Equal("Cogniify", prepared.Company, "company");
        Equal("Senior Generative AI Engineer", prepared.Title, "title");
    });

    static void NormalPromptAssembly() => WithPreparedFiles(() => {
        const string mine = "Write me a bold, modern resume.\r\nUse short sentences.\r\nBe specific about impact.";
        var settings = PromptSettings(PromptModes.Normal, NormalPromptFile(mine + "\r\n\r\n"));
        var text = RequestPreparation.Prepare(PromptJob(), settings).Text;

        Check(text.StartsWith(mine, StringComparison.Ordinal), "the user's prompt comes first, unchanged");
        Check(text.IndexOf(mine, StringComparison.Ordinal) < text.IndexOf("===== COMPLETE JOB PAYLOAD =====", StringComparison.Ordinal),
              "prompt, then payload");
        Check(text.IndexOf("===== COMPLETE JOB PAYLOAD =====", StringComparison.Ordinal) <
              text.IndexOf("===== EXECUTION INSTRUCTION =====", StringComparison.Ordinal), "payload, then contract");

        // The SAME full payload as Resume mode: job fields and the whole candidate profile.
        var resume = RequestPreparation.Prepare(PromptJob(), PromptSettings(PromptModes.Resume)).Text;
        string Payload(string all) {
            var from = all.IndexOf("===== COMPLETE JOB PAYLOAD =====", StringComparison.Ordinal);
            return all[from..all.IndexOf("===== EXECUTION INSTRUCTION =====", StringComparison.Ordinal)];
        }
        Check(Payload(text) == Payload(resume), "the payload block is identical in both modes");
        foreach (var expected in new[] { "\"company\": \"Cogniify\"", "\"title\": \"Senior Generative AI Engineer\"",
                                         "Build and ship generative AI features.", "\"link\": \"https://jobright.ai",
                                         "Cogniify builds AI tooling for small teams.", "\"profile\":",
                                         "Jordan Lee", "\"experience\":", "\"education\":" })
            Check(text.Contains(expected, StringComparison.Ordinal), "the payload carries " + expected);

        // Nothing about style is injected: an absent style object is handled by the renderer's default.
        Check(!text.Contains("style", StringComparison.OrdinalIgnoreCase) || text.IndexOf("style", StringComparison.OrdinalIgnoreCase) > text.IndexOf("\"profile\"", StringComparison.Ordinal),
              "no style instructions are added to the prompt");
    });

    static void BothModesShareTheContract() => WithPreparedFiles(() => {
        var resume = RequestPreparation.Prepare(PromptJob(), PromptSettings(PromptModes.Resume)).Text;
        var normal = RequestPreparation.Prepare(PromptJob(), PromptSettings(PromptModes.Normal, NormalPromptFile("Anything at all."))).Text;

        string Contract(string all) => all[all.IndexOf("===== EXECUTION INSTRUCTION =====", StringComparison.Ordinal)..];
        var resumeContract = Contract(resume);
        var normalContract = Contract(normal);

        // One schema: the two blocks differ only in their first sentence.
        Equal(resumeContract.Replace(PromptContract.ResumeOpening, PromptContract.NormalOpening), normalContract,
              "only the opening sentence differs");
        Check(resumeContract.Contains(PromptContract.ResumeOpening, StringComparison.Ordinal), "Resume names the Master Prompt");
        Check(normalContract.Contains(PromptContract.NormalOpening, StringComparison.Ordinal), "Normal names the user's instructions");

        foreach (var rule in new[] { "Return ONLY the updated profile object in a Markdown code block fenced with json.",
                                     "info, summary, skills, experience, certifications, and education",
                                     "experience must use startDate, endDate, and descriptionLines",
                                     "any text outside the JSON code block" })
            Check(normalContract.Contains(rule, StringComparison.Ordinal), "Normal mode still demands: " + rule);
    });

    static void PromptModeFallsBackToResume() {
        foreach (var mode in new[] { null, "", "   ", "resume", "RESUME", "Whatever", "normal-ish", "0" })
            Equal(PromptModes.Resume, PromptModes.Normalize(mode), "mode '" + (mode ?? "null") + "'");
        foreach (var mode in new[] { "Normal", "normal", " NORMAL " })
            Equal(PromptModes.Normal, PromptModes.Normalize(mode), "mode '" + mode + "'");
        Check(!PromptModes.IsNormal(null) && PromptModes.IsNormal("Normal"), "IsNormal");
        Equal(PromptModes.Resume, new AppSettings().PromptMode, "a new settings object defaults to Resume");
        Equal("", new AppSettings().NormalPrompt, "and has no normal prompt");

        // An older settings.json (no mode, no normal prompt) still loads and stays on Resume.
        var older = JsonSerializer.Deserialize<AppSettings>(
            """{ "MasterPrompt": "C:\\prompt.txt", "Docx": true, "AutoSend": false }""",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Equal(PromptModes.Resume, PromptModes.Normalize(older.PromptMode), "older settings read as Resume");
        Equal("C:\\prompt.txt", older.MasterPrompt, "and keep their paths");
        Check(!older.AutoSend, "and their switches");
    }

    static void NormalPromptFileIsRequired() => WithPreparedFiles(() => {
        foreach (var path in new[] { "", "   ", Path.Combine(TempRoot, "does-not-exist.txt") }) {
            var threw = "";
            try { RequestPreparation.Prepare(PromptJob(), PromptSettings(PromptModes.Normal, path)); }
            catch (InvalidOperationException ex) { threw = ex.Message; }
            Check(threw.Contains("Normal Prompt", StringComparison.Ordinal), "clear message, got: " + threw);
            Check(threw.Contains("Settings", StringComparison.Ordinal), "and says where to fix it");
        }

        // Resume mode keeps its own message, and neither mode invents a prompt.
        var missingMaster = PromptSettings(PromptModes.Resume); missingMaster.MasterPrompt = "";
        var masterError = "";
        try { RequestPreparation.Prepare(PromptJob(), missingMaster); }
        catch (InvalidOperationException ex) { masterError = ex.Message; }
        Equal("Configure an existing Master Prompt text file in Settings.", masterError, "the Master Prompt message is unchanged");
    });

    static void BothModesSaveTheSameArtifacts() => WithPreparedFiles(() => {
        foreach (var settings in new[] { PromptSettings(PromptModes.Resume),
                                         PromptSettings(PromptModes.Normal, NormalPromptFile("My own prompt.")) }) {
            if (File.Exists(RequestPreparation.PreparedPath)) File.Delete(RequestPreparation.PreparedPath);
            if (File.Exists(RequestPreparation.PreparedTextPath)) File.Delete(RequestPreparation.PreparedTextPath);

            var prepared = RequestPreparation.Prepare(PromptJob(), settings);
            var mode = PromptModes.Normalize(settings.PromptMode);

            Check(File.Exists(RequestPreparation.PreparedPath), mode + ": prepared-request.json written");
            Check(File.Exists(RequestPreparation.PreparedTextPath), mode + ": prepared-request.txt written");
            Equal(prepared.Text, File.ReadAllText(RequestPreparation.PreparedTextPath), mode + ": the text sidecar matches");

            var reloaded = RequestPreparation.Load()!;
            Equal(prepared.Text, reloaded.Text, mode + ": reloads");
            Equal("RB-20260919-101500-abcdef01", reloaded.JobId, mode + ": job id");
            Equal("Cogniify", reloaded.Company, mode + ": company");
            Equal("Senior Generative AI Engineer", reloaded.Title, mode + ": title");
        }
    });

    static void NormalModeAnswerRunsThePipeline() {
        // The pipeline never sees the prompt mode: the same answer text must behave identically.
        var settings = new AppSettings { ResumeRootFolder = NewDir("normal-mode-output"), Docx = true, Pdf = true };

        // (a) No style in the answer -> the promV4.12 default.
        var plain = Path.Combine(NewDir("normal-plain"), "result.json");
        var answer = "Sure! Here is the resume:\n\n```json\n" + File.ReadAllText(Fixture("resume-basic.json")) + "\n```\n";
        CandidateProfileStore.NormalizeAndSaveTo(answer, plain);
        CandidateProfileStore.Validate(JsonDocument.Parse(File.ReadAllText(plain)).RootElement);
        var plainStyle = StyleNormalizer.Normalize(JsonNode.Parse(File.ReadAllText(plain))!["style"]);
        Equal(StylePresets.Default, plainStyle.Style.Preset, "no style in the answer -> promV4.12");

        var docs = ResumeGenerator.Generate("Cogniify", "Senior Generative AI Engineer", plain, settings, null, "RB-NORMAL-1");
        Check(!docs.AnyFailure, "DOCX and PDF generate from a Normal-mode answer: " + docs.Describe());
        Check(File.Exists(docs.DocxPath!) && File.Exists(docs.PdfPath!), "both documents exist");

        // (b) A valid style in the answer -> that style is used.
        var styled = Path.Combine(NewDir("normal-styled"), "result.json");
        var styledAnswer = "```json\n" + File.ReadAllText(Fixture("resume-custom-style.json")) + "\n```";
        CandidateProfileStore.NormalizeAndSaveTo(styledAnswer, styled);
        var custom = StyleNormalizer.Normalize(JsonNode.Parse(File.ReadAllText(styled))!["style"]);
        Equal("#17365D", custom.Style.Colors.Primary, "the answer's own colour is used");
        Check(custom.Style.Colors.Primary != StylePresets.Get(null).Colors.Primary, "and it is not the default");
        var styledDocs = ResumeGenerator.Generate("Cogniify", "Senior Generative AI Engineer", styled, settings, null, "RB-NORMAL-2");
        Check(!styledDocs.AnyFailure, "a styled Normal-mode answer also generates: " + styledDocs.Describe());
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
        // The canonical input: the five fields plus the optional applyUrl (added for import-time apply
        // link discovery), no external id, no location. It is not a JobTask, so a file or a page reader
        // can never write to storage or the queue itself.
        var names = typeof(JobImportData).GetProperties().Select(p => p.Name).OrderBy(n => n).ToArray();
        Equal("ApplyUrl,Company,CompanyUrl,Description,JobUrl,Title", string.Join(",", names),
              "the five canonical fields plus the optional ApplyUrl");

        var data = new JobImportData();
        Equal("", data.Company, "company defaults empty");
        Check(data.CompanyUrl is null, "companyUrl defaults to none");
        Check(data.ApplyUrl is null, "applyUrl defaults to none");
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

    // ---------- application link capture ----------

    const string ApplyJobPage = "https://jobright.ai/jobs/info/6a5f372bd5c3a14fb34ec73a";
    const string ApplyOtherJobPage = "https://jobright.ai/jobs/info/69d0abea366bb95ba5520be8";

    /// <summary>Runs a check against the real Storage path, restoring the user's tasks.json afterwards.</summary>
    static void WithLiveTasksFile(Action body) {
        var live = Storage.TasksPath;
        var backup = File.Exists(live) ? File.ReadAllBytes(live) : null;
        try { body(); }
        finally {
            if (backup is byte[] content) File.WriteAllBytes(live, content);
            else if (File.Exists(live)) File.Delete(live);
        }
    }

    static void ApplyUrlOldTasksLoad() => WithLiveTasksFile(() => {
        // The shape tasks.json had just before ApplyUrl existed, read through the real loader.
        Directory.CreateDirectory(Storage.DataDir);
        File.WriteAllText(Storage.TasksPath, $$"""
        [
          { "JobId": "RB-OLD-1", "Source": "jobright-browser", "Company": "Oracle", "Title": "ML Engineer",
            "Jd": "...", "Link": "{{ApplyJobPage}}", "CompanyUrl": "https://www.oracle.com/",
            "Status": "Completed", "ApplicationStatus": "Ready" },
          { "JobId": "RB-OLD-2", "Company": "Stripe", "Title": "Backend Engineer", "Jd": "...", "Status": "Queued" }
        ]
        """);

        var tasks = Storage.LoadTasks();
        Equal(2, tasks.Count, "every older task loads");
        foreach (var task in tasks) {
            Equal("", task.ApplyUrl, task.JobId + " has no application link");
            Check(task.ApplyUrlCapturedAt is null, task.JobId + " has no capture time");
        }
        Equal("Completed", tasks[0].Status, "queue status untouched");
        Equal(ApplicationStatus.Ready, tasks[0].ApplicationStatus, "application status untouched");
        Equal(ApplyJobPage, tasks[0].Link, "job link untouched");
    });

    static void ApplyUrlSurvivesReload() => WithLiveTasksFile(() => {
        var at = new DateTime(2026, 9, 18, 14, 30, 0);
        var captured = Job("RB-APPLY-1", "Oracle", "ML Engineer", ApplyJobPage);
        captured.ApplyUrl = "https://careers.oracle.com/jobs/12345";
        captured.ApplyUrlCapturedAt = at;

        Storage.SaveTasks(new[] { captured, Job("RB-APPLY-2") });
        var reloaded = Storage.LoadTasks();

        Equal(2, reloaded.Count, "task count");
        Equal("https://careers.oracle.com/jobs/12345", reloaded[0].ApplyUrl, "ApplyUrl survived");
        Equal(at, reloaded[0].ApplyUrlCapturedAt, "ApplyUrlCapturedAt survived");
        Equal(ApplyJobPage, reloaded[0].Link, "Link keeps its meaning");
        Equal("", reloaded[1].ApplyUrl, "an uncaptured task stays empty");
        Check(reloaded[1].ApplyUrlCapturedAt is null, "an uncaptured task has no time");
    });

    static void ApplyUrlCaptureRule() {
        foreach (var good in new[] {
                     "https://boards.greenhouse.io/acme/jobs/123",
                     "https://acme.wd5.myworkdayjobs.com/en-US/Careers/job/Remote/ML-Engineer_R1",
                     "https://jobs.lever.co/acme/abc-def",
                     "https://www.linkedin.com/jobs/view/4012345678",
                     "http://careers.example.com/apply?id=7" })
            Check(ApplyCapture.IsApplicationUrl(good), "captured: " + good);

        foreach (var bad in new[] {
                     null, "", "not a url", "/jobs/apply", "about:blank", "javascript:void(0)",
                     "mailto:jobs@acme.com", "file:///C:/secret.txt",
                     "https://jobright.ai/jobs/info/6a5f372bd5c3a14fb34ec73a",
                     "https://www.jobright.ai/redirect?to=x", "https://api.jobright.ai/apply",
                     "https://www.linkedin.com/in/someone/", "https://www.linkedin.com/company/1028",
                     "https://x.com/Oracle", "https://www.crunchbase.com/organization/oracle",
                     "https://www.glassdoor.com/Overview/Working-at-Oracle-EI_IE1737.11,17.htm" })
            Check(!ApplyCapture.IsApplicationUrl(bad), "not captured: " + (bad ?? "null"));

        // A look-alike host is not jobright.ai.
        Check(ApplyCapture.IsApplicationUrl("https://notjobright.ai/apply"), "a look-alike host is outside jobright.ai");
    }

    static void ApplyUrlMatchesJob() {
        var now = new DateTime(2026, 9, 18, 15, 0, 0);
        var oracle = Job("RB-MATCH-1", "Oracle", "ML Engineer", ApplyJobPage);
        var other = Job("RB-MATCH-2", "Acme", "Engineer", ApplyOtherJobPage);
        var incoming = Job("RB-MATCH-3", "Local", "Engineer", "https://example.com/job/3");
        var tasks = new List<JobTask> { incoming, other, oracle };
        var before = tasks.ToList();

        // The job page address may carry tracking; the Jobright job id is what matches.
        Equal(ApplyCaptureResult.Recorded,
              ApplyCapture.Record(tasks, ApplyJobPage + "?utm_source=1101", "https://boards.greenhouse.io/oracle/jobs/1?gh_src=abc&utm_medium=x", now),
              "recorded on the viewed job");
        Equal("https://boards.greenhouse.io/oracle/jobs/1?gh_src=abc", oracle.ApplyUrl, "stored normalized, tracking dropped");
        Equal(now, oracle.ApplyUrlCapturedAt, "capture time stamped");
        Equal("", other.ApplyUrl, "another job is untouched");
        Equal("", incoming.ApplyUrl, "a non-Jobright job is untouched");

        // Same address again: nothing changes, not even the time.
        Equal(ApplyCaptureResult.Unchanged,
              ApplyCapture.Record(tasks, ApplyJobPage, "https://boards.greenhouse.io/oracle/jobs/1?gh_src=abc", now.AddHours(1)),
              "the same address is not a change");
        Equal(now, oracle.ApplyUrlCapturedAt, "the time is kept");

        // Not a single job page: the job is unknown.
        Equal(ApplyCaptureResult.NotJobPage,
              ApplyCapture.Record(tasks, "https://jobright.ai/jobs/recommend", "https://jobs.lever.co/x/1", now), "results page");
        Equal(ApplyCaptureResult.NotJobPage,
              ApplyCapture.Record(tasks, null, "https://jobs.lever.co/x/1", now), "no page");

        // A non-application destination records nothing.
        Equal(ApplyCaptureResult.NotApplicationUrl,
              ApplyCapture.Record(tasks, ApplyOtherJobPage, "https://www.linkedin.com/in/someone/", now), "a profile link");
        Equal("", other.ApplyUrl, "still untouched");

        // A job that is not imported: nothing is created.
        Equal(ApplyCaptureResult.UnknownJob,
              ApplyCapture.Record(tasks, "https://jobright.ai/jobs/info/aaaaaaaaaaaaaaaaaaaaaaaa", "https://jobs.lever.co/x/1", now),
              "an unknown job");
        Equal(3, tasks.Count, "no task was added");
        Check(tasks.SequenceEqual(before), "the task list is unchanged");

        // A later, different address replaces the old one.
        Equal(ApplyCaptureResult.Recorded,
              ApplyCapture.Record(tasks, ApplyJobPage, "https://careers.oracle.com/jobs/9", now.AddDays(1)), "a new address");
        Equal("https://careers.oracle.com/jobs/9", oracle.ApplyUrl, "replaced");
        Equal(now.AddDays(1), oracle.ApplyUrlCapturedAt, "restamped");
    }

    // ---------- open application ----------

    static void OpenApplicationIsSafe() {
        // Only refusals are exercised here, so no browser is ever launched by the test run.
        Check(JobTracker.ApplyUrlToOpen(null) is null, "no job");
        Check(!JobTracker.OpenApplyUrl(null), "no job is not opened");

        // Missing ApplyUrl: refused even though the job has a perfectly good Jobright link.
        var noApply = Job("OA-1", "Cogniify", "Engineer", "https://jobright.ai/jobs/info/6aada17fde327d3e210d3913");
        Check(JobTracker.ApplyUrlToOpen(noApply) is null, "an empty ApplyUrl is never replaced by Link");
        Check(!JobTracker.OpenApplyUrl(noApply), "an empty ApplyUrl opens nothing");

        foreach (var bad in new[] { " ", "not a url", "/apply/1", "file:///C:/Windows/System32/calc.exe",
                                    "javascript:alert(1)", "mailto:jobs@example.com", "ftp://example.com/apply",
                                    @"C:\Users\someone\apply.html", "about:blank" }) {
            var job = Job("OA-BAD", "Acme", "Engineer", "https://jobright.ai/jobs/info/6aada17fde327d3e210d3913");
            job.ApplyUrl = bad;
            Check(JobTracker.ApplyUrlToOpen(job) is null, "refused: " + bad);
            Check(!JobTracker.OpenApplyUrl(job), "not launched: " + bad);
        }

        // A usable ApplyUrl is what would be opened — never Link, even when both are set.
        var both = Job("OA-2", "Cogniify", "Engineer", "https://jobright.ai/jobs/info/6aada17fde327d3e210d3913");
        both.ApplyUrl = "  https://app.dover.com/apply/Cogniify/1e3fc78f?jr_id=6aada17f  ";
        Equal("https://app.dover.com/apply/Cogniify/1e3fc78f?jr_id=6aada17f", JobTracker.ApplyUrlToOpen(both), "ApplyUrl, trimmed");
        both.Link = "";
        Equal("https://app.dover.com/apply/Cogniify/1e3fc78f?jr_id=6aada17f", JobTracker.ApplyUrlToOpen(both), "Link plays no part");
        Check(JobTracker.IsOpenableUrl("http://careers.example.com/apply"), "plain http is allowed, as for Open Job");
    }

    // ---------- application readiness ----------

    static JobTask ReadinessJob(string? resumePath, string applyUrl) {
        var job = Job("RD-1", "Cogniify", "Engineer", "https://jobright.ai/jobs/info/6aada17fde327d3e210d3913");
        job.ResumePath = resumePath ?? "";
        job.ApplyUrl = applyUrl;
        return job;
    }

    static void ReadinessStates() {
        const string resume = @"C:\Resumes\2026-09-18\Cogniify - Engineer\Resume.docx";
        const string dover = "https://app.dover.com/apply/Cogniify/1e3fc78f";

        var none = ReadinessJob(null, "");
        Equal(ApplicationReadiness.NeedsResume, JobTracker.GetReadiness(none), "no resume, no link");
        Equal("Needs resume", none.ReadinessDisplay, "its text");

        var linkOnly = ReadinessJob(null, dover);
        Equal(ApplicationReadiness.NeedsResume, JobTracker.GetReadiness(linkOnly), "a link without a resume still needs the resume");

        var resumeOnly = ReadinessJob(resume, "");
        Equal(ApplicationReadiness.NeedsApplyLink, JobTracker.GetReadiness(resumeOnly), "resume, no link");
        Equal("Needs apply link", resumeOnly.ReadinessDisplay, "its text");

        var both = ReadinessJob(resume, dover);
        Equal(ApplicationReadiness.ReadyToApply, JobTracker.GetReadiness(both), "resume and link");
        Equal("Ready to apply", both.ReadinessDisplay, "its text");
        Equal(ApplicationReadiness.ReadyToApply, both.Readiness, "the task property agrees with JobTracker");

        // The wording never reuses the bare application-status word.
        foreach (var r in Enum.GetValues<ApplicationReadiness>()) {
            Check(!ApplicationStatus.Ordered.Contains(JobTracker.ReadinessText(r)), r + " text is not a status name");
            Check(JobTracker.ReadinessHint(r).Length > 0, r + " has a hint");
        }
    }

    static void ReadinessRejectsInvalidApplyUrl() {
        const string resume = @"C:\Resumes\Resume.docx";
        foreach (var bad in new[] { " ", "not a url", "/apply/1", "file:///C:/apply.html", "javascript:alert(1)",
                                    "mailto:jobs@example.com", "ftp://example.com/apply", "about:blank" })
            Equal(ApplicationReadiness.NeedsApplyLink, JobTracker.GetReadiness(ReadinessJob(resume, bad)), "unusable: " + bad);

        // The job link is not an application link.
        var jobright = ReadinessJob(resume, "");
        Check(JobTracker.IsOpenableUrl(jobright.Link), "the Jobright link itself is valid");
        Equal(ApplicationReadiness.NeedsApplyLink, JobTracker.GetReadiness(jobright), "but Link never makes a job ready");
        Equal(ApplicationReadiness.ReadyToApply, JobTracker.GetReadiness(ReadinessJob(resume, "http://careers.example.com/a")), "plain http counts");
    }

    static void ReadinessIsDisplayOnly() {
        var job = ReadinessJob(@"C:\Resumes\Resume.docx", "https://jobs.lever.co/acme/1");
        job.Status = "Completed";
        JobTracker.UpdateStatus(job, ApplicationStatus.Applied, new DateTime(2026, 9, 18, 10, 0, 0));
        var appliedAt = job.AppliedAt;

        Equal(ApplicationReadiness.ReadyToApply, job.Readiness, "ready");
        _ = job.ReadinessDisplay; _ = job.ReadinessHint;
        Equal("Completed", job.Status, "queue status untouched");
        Equal(ApplicationStatus.Applied, job.ApplicationStatus, "application status untouched");
        Equal(appliedAt, job.AppliedAt, "timestamps untouched");

        // Not in the saved JSON, under any name.
        var json = JsonSerializer.Serialize(new[] { job });
        foreach (var name in new[] { "Readiness", "ReadinessDisplay", "ReadinessHint", "Ready to apply" })
            Check(!json.Contains(name, StringComparison.Ordinal), "tasks.json must not carry " + name);
        Check(json.Contains("\"ApplyUrl\""), "the stored fields are still written");

        // Change notification covers the readiness text, so an open dashboard row updates live.
        var raised = new List<string>();
        job.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");
        job.NotifyTrackingChanged();
        Check(raised.Contains(nameof(JobTask.ReadinessDisplay)) && raised.Contains(nameof(JobTask.Readiness)),
              "NotifyTrackingChanged raises the readiness properties");
    }

    // ---------- mark applied ----------

    static void MarkAppliedFromViewedAndReady() {
        var viewedAt = new DateTime(2026, 9, 17, 9, 0, 0);
        var appliedAt = new DateTime(2026, 9, 18, 15, 40, 0);

        // Viewed -> Applied: ReadyAt was skipped, so it is backfilled with the same time.
        var viewed = Job("MA-1");
        viewed.ViewedAt = viewedAt;
        Check(JobTracker.CanMarkApplied(viewed), "offered from Viewed");
        Check(JobTracker.MarkApplied(viewed, appliedAt), "Viewed -> Applied changes the job");
        Equal(ApplicationStatus.Applied, viewed.ApplicationStatus, "now Applied");
        Equal(appliedAt, viewed.AppliedAt, "AppliedAt stamped");
        Equal(appliedAt, viewed.ReadyAt, "skipped ReadyAt backfilled");
        Equal(viewedAt, viewed.ViewedAt, "ViewedAt kept");

        // Ready -> Applied: ReadyAt already set, kept as it was.
        var readyAt = new DateTime(2026, 9, 18, 10, 0, 0);
        var ready = Job("MA-2");
        JobTracker.UpdateStatus(ready, ApplicationStatus.Ready, readyAt);
        Check(JobTracker.CanMarkApplied(ready), "offered from Ready");
        Check(JobTracker.MarkApplied(ready, appliedAt), "Ready -> Applied changes the job");
        Equal(ApplicationStatus.Applied, ready.ApplicationStatus, "now Applied");
        Equal(appliedAt, ready.AppliedAt, "AppliedAt stamped");
        Equal(readyAt, ready.ReadyAt, "ReadyAt not rewritten");
    }

    static void MarkAppliedPreservesHistory() {
        var first = new DateTime(2026, 9, 1, 12, 0, 0);
        var later = new DateTime(2026, 9, 18, 12, 0, 0);

        // Already Applied / Interview / Done: not offered, no change, no timestamp touched.
        foreach (var stage in new[] { ApplicationStatus.Applied, ApplicationStatus.Interview, ApplicationStatus.Done }) {
            var job = Job("MA-" + stage);
            JobTracker.UpdateStatus(job, stage, first);
            var before = (job.ReadyAt, job.AppliedAt, job.InterviewAt, job.DoneAt, job.UpdatedAt);
            Check(!JobTracker.CanMarkApplied(job), stage + " is not offered Mark Applied");
            Check(!JobTracker.MarkApplied(job, later), stage + ": Mark Applied changes nothing");
            Equal(stage, job.ApplicationStatus, stage + " is not moved backwards");
            Equal(before, (job.ReadyAt, job.AppliedAt, job.InterviewAt, job.DoneAt, job.UpdatedAt), stage + ": timestamps untouched");
        }

        // Moved back to Ready by hand after applying, then Mark Applied again: the original AppliedAt stays.
        var corrected = Job("MA-BACK");
        JobTracker.MarkApplied(corrected, first);
        JobTracker.UpdateStatus(corrected, ApplicationStatus.Ready, later);
        Check(JobTracker.MarkApplied(corrected, later), "re-applied after a correction");
        Equal(first, corrected.AppliedAt, "AppliedAt is never rewritten");

        Check(JobTracker.CanMarkApplied((string?)null), "missing status reads as Viewed, so it is offered");
        Check(!JobTracker.CanMarkApplied("Interview"), "by status string too");
    }

    static void MarkAppliedTouchesNothingElse() {
        foreach (var queue in new[] { "Queued", "Processing", "Completed", "Failed" }) {
            var job = ReadinessJob(@"C:\Resumes\Resume.docx", "https://jobs.lever.co/acme/1");
            job.Status = queue;
            var readiness = job.Readiness;
            var platform = job.ApplicationPlatform;
            JobTracker.MarkApplied(job);
            Equal(queue, job.Status, "queue status " + queue + " untouched");
            Equal(readiness, job.Readiness, "readiness unchanged for " + queue);
            Equal(platform, job.ApplicationPlatform, "platform unchanged");
            Equal("https://jobs.lever.co/acme/1", job.ApplyUrl, "ApplyUrl unchanged");
        }
        var notReady = ReadinessJob(null, "");
        JobTracker.MarkApplied(notReady);
        Equal(ApplicationReadiness.NeedsResume, notReady.Readiness, "applying does not make a job 'ready'");
    }

    static void AppliedDateDisplay() {
        var job = Job("MA-DATES");
        job.CreatedAt = new DateTime(2026, 9, 10, 8, 5, 0);
        job.ViewedAt = null;
        Equal("Added Sep 10, 2026, 8:05 AM", JobTracker.StageDatesText(job), "a task with no stage dates falls back to Added");
        Equal(job.TrackingDateDisplay, JobTracker.BoardDateText(job), "Board shows the tracking date before applying");

        job.ViewedAt = new DateTime(2026, 9, 17, 9, 0, 0);
        JobTracker.MarkApplied(job, new DateTime(2026, 9, 18, 15, 40, 0));
        Equal(string.Join(Environment.NewLine, "Viewed Sep 17, 2026, 9:00 AM", "Ready Sep 18, 2026, 3:40 PM", "Applied Sep 18, 2026, 3:40 PM"),
              JobTracker.StageDatesText(job), "every recorded stage, in order");
        Equal("Applied Sep 18", JobTracker.BoardDateText(job), "Board labels the applied date");

        // Moving on to Interview keeps the Board showing the applied date.
        JobTracker.UpdateStatus(job, ApplicationStatus.Interview, new DateTime(2026, 9, 25, 11, 0, 0));
        Equal("Applied Sep 18", JobTracker.BoardDateText(job), "still the applied date at Interview");
        Check(JobTracker.StageDatesText(job).EndsWith("Interview Sep 25, 2026, 11:00 AM"), "Interview appended");
    }

    // ---------- platform filter ----------

    static JobTask PlatformJob(string id, ApplicationPlatform platform, string status = ApplicationStatus.Viewed,
                               string company = "Acme", string title = "Engineer", DateTime? created = null) {
        var job = Job(id, company, title);
        job.ApplicationPlatform = platform;
        if (created is DateTime at) job.CreatedAt = at;
        if (status != ApplicationStatus.Viewed) JobTracker.UpdateStatus(job, status, job.CreatedAt);
        return job;
    }

    static List<JobTask> PlatformSet() => new() {
        PlatformJob("GH-1", ApplicationPlatform.Greenhouse),
        PlatformJob("WD-1", ApplicationPlatform.Workday),
        PlatformJob("WD-2", ApplicationPlatform.Workday),
        PlatformJob("LI-1", ApplicationPlatform.LinkedIn),
        PlatformJob("OT-1", ApplicationPlatform.Other),
        PlatformJob("UN-1", ApplicationPlatform.Unknown),
    };

    static string Ids(IEnumerable<JobTask> tasks) => string.Join(",", tasks.Select(t => t.JobId));

    static void PlatformFilterEmptySelection() {
        var tasks = PlatformSet();
        Equal(6, JobTracker.ApplyFilters(tasks, null, null, null).Count, "the parameter left out");
        Equal(6, JobTracker.ApplyFilters(tasks, null, null, null, platforms: null).Count, "null");
        Equal(6, JobTracker.ApplyFilters(tasks, null, null, null, platforms: new HashSet<ApplicationPlatform>()).Count, "empty");
        Equal(6, JobTracker.ApplyFilters(tasks, null, null, null, platforms: JobTracker.PlatformFilterOrder.ToList()).Count,
              "every choice ticked");
        Equal(Ids(tasks), Ids(JobTracker.ApplyFilters(tasks, null, null, null, platforms: new List<ApplicationPlatform>())),
              "order is kept");
    }

    static void PlatformFilterSinglePlatform() {
        var tasks = PlatformSet();
        Equal("WD-1,WD-2", Ids(JobTracker.ApplyFilters(tasks, null, null, null, platforms: new[] { ApplicationPlatform.Workday })), "Workday");
        Equal("UN-1", Ids(JobTracker.ApplyFilters(tasks, null, null, null, platforms: new[] { ApplicationPlatform.Unknown })), "Unknown");
        Equal("OT-1", Ids(JobTracker.ApplyFilters(tasks, null, null, null, platforms: new[] { ApplicationPlatform.Other })), "Other");
        Equal(0, JobTracker.ApplyFilters(tasks, null, null, null, platforms: new[] { ApplicationPlatform.Lever }).Count, "no Lever jobs");
    }

    static void PlatformFilterOrMatching() {
        var tasks = PlatformSet();
        Equal("GH-1,WD-1,WD-2",
              Ids(JobTracker.ApplyFilters(tasks, null, null, null,
                  platforms: new HashSet<ApplicationPlatform> { ApplicationPlatform.Workday, ApplicationPlatform.Greenhouse })),
              "Greenhouse OR Workday");
        Equal("LI-1,OT-1,UN-1",
              Ids(JobTracker.ApplyFilters(tasks, null, null, null,
                  platforms: new[] { ApplicationPlatform.Unknown, ApplicationPlatform.Other, ApplicationPlatform.LinkedIn })),
              "three platforms, selection order does not matter");
        Equal("WD-1,WD-2",
              Ids(JobTracker.ApplyFilters(tasks, null, null, null, platforms: new[] { ApplicationPlatform.Workday, ApplicationPlatform.Lever })),
              "a platform with no jobs adds nothing");
    }

    static void PlatformFilterCombined() {
        var now = new DateTime(2026, 9, 18, 12, 0, 0);
        var tasks = new List<JobTask> {
            PlatformJob("C-1", ApplicationPlatform.Workday, ApplicationStatus.Applied, "Caterpillar", "AI Engineer", now),
            PlatformJob("C-2", ApplicationPlatform.Workday, ApplicationStatus.Viewed, "Caterpillar", "Data Engineer", now),
            PlatformJob("C-3", ApplicationPlatform.Greenhouse, ApplicationStatus.Applied, "Stripe", "AI Engineer", now.AddDays(-10)),
            PlatformJob("C-4", ApplicationPlatform.LinkedIn, ApplicationStatus.Applied, "Caterpillar", "AI Engineer", now),
        };
        var workdayOrGreenhouse = new[] { ApplicationPlatform.Workday, ApplicationPlatform.Greenhouse };

        Equal("C-1,C-3", Ids(JobTracker.ApplyFilters(tasks, null, ApplicationStatus.Applied, null, now, null, workdayOrGreenhouse)),
              "platform AND status");
        Equal("C-1,C-2", Ids(JobTracker.ApplyFilters(tasks, "caterpillar", null, null, now, null, workdayOrGreenhouse)),
              "platform AND search");
        Equal("C-1", Ids(JobTracker.ApplyFilters(tasks, "caterpillar ai", ApplicationStatus.Applied, null, now, null, workdayOrGreenhouse)),
              "platform AND search AND status");
        Equal("C-1,C-2", Ids(JobTracker.ApplyFilters(tasks, null, null, DateFilter.Last7, now, null, workdayOrGreenhouse)),
              "platform AND date range");
        Equal("C-3", Ids(JobTracker.ApplyFilters(tasks, null, null, DateFilter.AllDates, now, now.AddDays(-10).Date, workdayOrGreenhouse)),
              "platform AND exact day");

        // Board columns are GetTasksByStatus over the same filtered list, so they agree with the List.
        var filtered = JobTracker.ApplyFilters(tasks, null, null, null, now, null, workdayOrGreenhouse);
        var board = ApplicationStatus.Ordered.SelectMany(status => JobTracker.GetTasksByStatus(filtered, status)).Select(t => t.JobId);
        Equal("C-1,C-2,C-3", string.Join(",", board.OrderBy(id => id)), "the board shows exactly the list's jobs");

        // The platform filter never changes a task.
        Equal(ApplicationPlatform.LinkedIn, tasks[3].ApplicationPlatform, "tasks untouched");
    }

    static void PlatformFilterLabelAndOrder() {
        Equal("Greenhouse,Workday,Lever,LinkedIn,Ashby,SmartRecruiters,ICims,Other,Unknown",
              string.Join(",", JobTracker.PlatformFilterOrder), "the explicit choice order");
        Equal(Enum.GetValues<ApplicationPlatform>().Length, JobTracker.PlatformFilterOrder.Count, "every platform is a choice");
        Equal(JobTracker.PlatformFilterOrder.Count, JobTracker.PlatformFilterOrder.Distinct().Count(), "no choice twice");

        Equal("All platforms", JobTracker.PlatformFilterLabel(null), "null");
        Equal("All platforms", JobTracker.PlatformFilterLabel(new HashSet<ApplicationPlatform>()), "nothing ticked");
        Equal("All platforms", JobTracker.PlatformFilterLabel(JobTracker.PlatformFilterOrder.ToList()), "everything ticked");
        Equal("Workday", JobTracker.PlatformFilterLabel(new[] { ApplicationPlatform.Workday }), "one");
        Equal("iCIMS", JobTracker.PlatformFilterLabel(new[] { ApplicationPlatform.ICims }), "display name");
        Equal("Greenhouse, Workday",
              JobTracker.PlatformFilterLabel(new[] { ApplicationPlatform.Workday, ApplicationPlatform.Greenhouse }),
              "two, in the fixed order whatever the tick order");
        Equal("3 platforms",
              JobTracker.PlatformFilterLabel(new[] { ApplicationPlatform.Unknown, ApplicationPlatform.Lever, ApplicationPlatform.Ashby }),
              "three or more");
        Equal("8 platforms", JobTracker.PlatformFilterLabel(JobTracker.PlatformFilterOrder.Skip(1).ToList()), "all but one");

        // The badge and the filter share these display names.
        Equal("Other", JobTracker.PlatformDisplayName(ApplicationPlatform.Other), "Other badge text");
        Equal("SmartRecruiters", JobTracker.PlatformDisplayName(ApplicationPlatform.SmartRecruiters), "SmartRecruiters badge text");
        foreach (var platform in JobTracker.PlatformFilterOrder)
            Check(JobTracker.PlatformDisplayName(platform).Length > 0, platform + " has display text");
    }

    // ---------- readiness filter ----------

    const string RfResume = @"C:\Resumes\Resume.docx";

    static JobTask RfJob(string id, bool resume, string applyUrl, ApplicationPlatform platform = ApplicationPlatform.Unknown,
                         string company = "Acme", string status = ApplicationStatus.Viewed, DateTime? created = null) {
        var job = PlatformJob(id, platform, status, company, "Engineer", created);
        job.ResumePath = resume ? RfResume : "";
        job.ApplyUrl = applyUrl;
        return job;
    }

    static List<JobTask> ReadinessSet() => new() {
        RfJob("R-READY", true, "https://jobs.lever.co/acme/1"),
        RfJob("R-LINK", true, ""),
        RfJob("R-BAD", true, "javascript:alert(1)"),          // an unusable link still needs one
        RfJob("R-RESUME", false, "https://jobs.lever.co/acme/2"),
        RfJob("R-NONE", false, ""),
    };

    static void ReadinessFilterSingleStates() {
        var tasks = ReadinessSet();
        Equal("R-READY", Ids(JobTracker.ApplyFilters(tasks, null, null, null, readiness: new[] { ApplicationReadiness.ReadyToApply })), "ready to apply");
        Equal("R-LINK,R-BAD", Ids(JobTracker.ApplyFilters(tasks, null, null, null, readiness: new[] { ApplicationReadiness.NeedsApplyLink })), "needs apply link");
        Equal("R-RESUME,R-NONE", Ids(JobTracker.ApplyFilters(tasks, null, null, null, readiness: new[] { ApplicationReadiness.NeedsResume })), "needs resume");

        Equal(5, JobTracker.ApplyFilters(tasks, null, null, null).Count, "parameter left out");
        Equal(5, JobTracker.ApplyFilters(tasks, null, null, null, readiness: null).Count, "null");
        Equal(5, JobTracker.ApplyFilters(tasks, null, null, null, readiness: new HashSet<ApplicationReadiness>()).Count, "empty");
        Equal(5, JobTracker.ApplyFilters(tasks, null, null, null, readiness: JobTracker.ReadinessFilterOrder.ToList()).Count, "all three");

        // Evaluated now, never stored: gaining a resume moves a job between states.
        tasks[1].ResumePath = "";
        Equal("R-LINK,R-RESUME,R-NONE",
              Ids(JobTracker.ApplyFilters(tasks, null, null, null, readiness: new[] { ApplicationReadiness.NeedsResume })),
              "readiness follows the job's current data");
    }

    static void ReadinessFilterOrMatching() {
        var tasks = ReadinessSet();
        Equal("R-READY,R-LINK,R-BAD",
              Ids(JobTracker.ApplyFilters(tasks, null, null, null,
                  readiness: new HashSet<ApplicationReadiness> { ApplicationReadiness.NeedsApplyLink, ApplicationReadiness.ReadyToApply })),
              "ready OR needs link, in list order");
        Equal("R-READY,R-RESUME,R-NONE",
              Ids(JobTracker.ApplyFilters(tasks, null, null, null,
                  readiness: new[] { ApplicationReadiness.NeedsResume, ApplicationReadiness.ReadyToApply })),
              "ready OR needs resume");
    }

    static void ReadinessFilterCombined() {
        var now = new DateTime(2026, 9, 18, 12, 0, 0);
        var tasks = new List<JobTask> {
            RfJob("X-1", true, "https://jobs.lever.co/a/1", ApplicationPlatform.Lever, "Caterpillar", ApplicationStatus.Ready, now),
            RfJob("X-2", true, "https://boards.greenhouse.io/b/1", ApplicationPlatform.Greenhouse, "Stripe", ApplicationStatus.Ready, now),
            RfJob("X-3", true, "", ApplicationPlatform.Unknown, "Caterpillar", ApplicationStatus.Ready, now),
            RfJob("X-4", true, "https://jobs.lever.co/a/2", ApplicationPlatform.Lever, "Caterpillar", ApplicationStatus.Applied, now.AddDays(-20)),
            RfJob("X-5", false, "https://jobs.lever.co/a/3", ApplicationPlatform.Lever, "Caterpillar", ApplicationStatus.Viewed, now),
        };
        var ready = new[] { ApplicationReadiness.ReadyToApply };

        Equal("X-1,X-2,X-4", Ids(JobTracker.ApplyFilters(tasks, null, null, null, now, null, null, ready)), "readiness alone");
        Equal("X-1,X-2", Ids(JobTracker.ApplyFilters(tasks, null, ApplicationStatus.Ready, null, now, null, null, ready)), "AND status");
        Equal("X-1,X-4", Ids(JobTracker.ApplyFilters(tasks, "caterpillar", null, null, now, null, null, ready)), "AND search");
        Equal("X-1,X-4", Ids(JobTracker.ApplyFilters(tasks, null, null, null, now, null, new[] { ApplicationPlatform.Lever }, ready)), "AND platform");
        Equal("X-1,X-2", Ids(JobTracker.ApplyFilters(tasks, null, null, DateFilter.Last7, now, null, null, ready)), "AND date range");
        Equal("X-4", Ids(JobTracker.ApplyFilters(tasks, null, null, DateFilter.AllDates, now, now.AddDays(-20).Date, null, ready)), "AND exact day");
        Equal("X-1", Ids(JobTracker.ApplyFilters(tasks, "caterpillar", ApplicationStatus.Ready, DateFilter.Last7, now, null,
                                                 new[] { ApplicationPlatform.Lever, ApplicationPlatform.Greenhouse }, ready)), "all five together");

        // List and Board come from the same filtered list.
        var filtered = JobTracker.ApplyFilters(tasks, null, null, null, now, null, null,
                                               new[] { ApplicationReadiness.ReadyToApply, ApplicationReadiness.NeedsApplyLink });
        var board = ApplicationStatus.Ordered.SelectMany(s => JobTracker.GetTasksByStatus(filtered, s)).Select(t => t.JobId).OrderBy(x => x);
        Equal(string.Join(",", filtered.Select(t => t.JobId).OrderBy(x => x)), string.Join(",", board), "Board shows exactly the List's jobs");
        Equal("X-1,X-2,X-3,X-4", string.Join(",", board), "and they are the right ones");

        // Filtering changes nothing on a task.
        Equal(ApplicationStatus.Ready, tasks[0].ApplicationStatus, "status untouched");
        Equal(RfResume, tasks[0].ResumePath, "resume untouched");
    }

    static void CountReadyToApplyMatchesFilter() {
        Equal(0, JobTracker.CountReadyToApply(new List<JobTask>()), "no jobs");
        Equal(0, JobTracker.CountReadyToApply(null!), "null list");

        var tasks = ReadinessSet();                             // 1 ready, 2 need a link, 2 need a resume
        Equal(1, JobTracker.CountReadyToApply(tasks), "one ready to apply");

        // Every application status counts alike: readiness ignores status, as the filter does.
        var statuses = new List<JobTask>();
        foreach (var status in ApplicationStatus.Ordered)
            statuses.Add(RfJob("S-" + status, true, "https://jobs.lever.co/acme/" + status, status: status));
        statuses.Add(RfJob("S-NOLINK", true, ""));
        Equal(5, JobTracker.CountReadyToApply(statuses), "ready at every status, not the one without a link");

        foreach (var set in new[] { tasks, statuses }) {
            var shown = JobTracker.ApplyFilters(set, null, null, null, readiness: new[] { ApplicationReadiness.ReadyToApply });
            Equal(JobTracker.CountReadyToApply(set), shown.Count, "card count == Readiness filter result");
        }

        // Computed now: gaining a link or losing a resume changes the count; nothing is stored.
        tasks[1].ApplyUrl = "https://jobs.lever.co/acme/new";
        Equal(2, JobTracker.CountReadyToApply(tasks), "a new link makes a job ready");
        tasks[0].ResumePath = "";
        Equal(1, JobTracker.CountReadyToApply(tasks), "no resume, not ready");
    }

    /// <summary>Ready to apply (resume + link) at each application status, plus two that are not ready.</summary>
    static List<JobTask> ActionQueueSet() {
        var tasks = ApplicationStatus.Ordered
            .Select(status => RfJob("AQ-" + status, true, "https://jobs.lever.co/acme/" + status, status: status)).ToList();
        tasks.Add(RfJob("AQ-NOLINK", true, ""));                            // Viewed, needs a link
        tasks.Add(RfJob("AQ-NORESUME", false, "https://jobs.lever.co/acme/x"));   // Viewed, needs a resume
        return tasks;
    }

    static void NeedsActionRule() {
        foreach (var job in ActionQueueSet()) {
            var ready = JobTracker.GetReadiness(job) == ApplicationReadiness.ReadyToApply;
            var notApplied = job.ApplicationStatus is ApplicationStatus.Viewed or ApplicationStatus.Ready;
            Equal(ready && notApplied, JobTracker.NeedsAction(job), $"{job.JobId} ({job.ApplicationStatus})");
        }

        // Exactly the two early stages, and only when ready.
        var set = ActionQueueSet();
        Equal("AQ-Viewed,AQ-Ready", string.Join(",", set.Where(JobTracker.NeedsAction).Select(t => t.JobId)), "the action queue");
        Equal(2, JobTracker.CountNeedsAction(set), "count");
        Equal(0, JobTracker.CountNeedsAction(new List<JobTask>()), "no jobs");
        Equal(0, JobTracker.CountNeedsAction(null!), "null list");

        // Readiness itself is untouched by status: all five stages stay ReadyToApply.
        Equal(5, JobTracker.CountReadyToApply(set), "CountReadyToApply still ignores status");
    }

    static void NotAppliedYetGroupFilter() {
        const string group = ApplicationStatus.Filter.NotAppliedYet;
        var set = ActionQueueSet();

        Equal("AQ-Viewed,AQ-Ready,AQ-NOLINK,AQ-NORESUME",
              Ids(JobTracker.GetTasksByStatus(set, group)), "Viewed and Ready only, ready or not");
        Check(ApplicationStatus.Filter.IsGroup(group) && ApplicationStatus.Filter.IsGroup(" not applied YET "), "recognised, trimmed and case-insensitive");
        Check(!ApplicationStatus.Filter.IsGroup(ApplicationStatus.All) && !ApplicationStatus.Filter.IsGroup(ApplicationStatus.Viewed), "a real status is not the group");

        // It is a filter value only: never stored, never produced by Normalize, never written by UpdateStatus.
        Equal(ApplicationStatus.Viewed, ApplicationStatus.Normalize(group), "Normalize never returns the group");
        Check(!ApplicationStatus.Ordered.Contains(group), "not a real status");
        Check(!ApplicationStatus.Filters.Contains(group), "the original filter list is untouched");
        var job = RfJob("AQ-WRITE", true, "https://jobs.lever.co/acme/1", status: ApplicationStatus.Ready);
        Check(!JobTracker.UpdateStatus(job, group), "UpdateStatus refuses the group");
        Equal(ApplicationStatus.Ready, job.ApplicationStatus, "and stores nothing");

        // Every existing status filter behaves exactly as before.
        Equal(7, ApplicationStatus.Filter.Options.Length, "All + the group + five statuses");
        Equal(6, ApplicationStatus.Filters.Length, "the pre-existing filter list is unchanged");
        Equal(5, ApplicationStatus.Ordered.Length, "Ordered is unchanged");
        Equal(set.Count, JobTracker.GetTasksByStatus(set, ApplicationStatus.All).Count, "All");
        foreach (var status in ApplicationStatus.Ordered)
            Equal(set.Count(t => t.ApplicationStatus == status), JobTracker.GetTasksByStatus(set, status).Count, "filter " + status);

        // The pipeline and the statistics still see the five real stages only.
        var stages = JobTracker.GetPipelineCounts(set);
        Equal("Viewed,Ready,Applied,Interview,Done", string.Join(",", stages.Select(s => s.Status)), "pipeline stages");
        Equal(set.Count, stages.Sum(s => s.Count), "every job in exactly one stage");
        var stats = JobTracker.GetStatistics(set);
        Equal(set.Count, stats.Total, "statistics total");
        Equal(3, stats.Viewed, "statistics count real statuses only");
    }

    static void ActionQueueCardMatchesItsFilters() {
        foreach (var set in new[] { ActionQueueSet(), ReadinessSet(), new List<JobTask>() }) {
            var shown = JobTracker.ApplyFilters(set, null, ApplicationStatus.Filter.NotAppliedYet, null,
                                                readiness: new[] { ApplicationReadiness.ReadyToApply });
            Equal(JobTracker.CountNeedsAction(set), shown.Count, "card count == the card's own filters");
            Check(shown.All(JobTracker.NeedsAction), "and every shown job needs action");
        }

        // The Readiness filter on its own is unchanged: it still shows applied jobs too.
        var tasks = ActionQueueSet();
        Equal(5, JobTracker.ApplyFilters(tasks, null, null, null, readiness: new[] { ApplicationReadiness.ReadyToApply }).Count,
              "readiness filter alone ignores status");
    }

    static void MarkAppliedLeavesActionQueue() {
        var applied = new DateTime(2026, 9, 19, 9, 30, 0);
        var tasks = ActionQueueSet();
        var job = tasks.Single(t => t.JobId == "AQ-Ready");
        var before = JobTracker.CountNeedsAction(tasks);

        Check(JobTracker.NeedsAction(job), "in the queue first");
        Check(JobTracker.MarkApplied(job, applied), "marked applied");

        Equal(before - 1, JobTracker.CountNeedsAction(tasks), "the count drops by one");
        Check(!JobTracker.NeedsAction(job), "the job leaves the queue");
        Check(!JobTracker.ApplyFilters(tasks, null, ApplicationStatus.Filter.NotAppliedYet, null,
                                       readiness: new[] { ApplicationReadiness.ReadyToApply }).Contains(job),
              "and leaves the card's list");

        // What must NOT change.
        Equal(ApplicationReadiness.ReadyToApply, JobTracker.GetReadiness(job), "readiness stays ReadyToApply");
        Equal("Ready to apply", job.ReadinessDisplay, "and so does its text");
        Equal("Queued", job.Status, "queue status untouched");
        Equal(applied, job.AppliedAt, "AppliedAt stamped once");
        Check(JobTracker.ApplyFilters(tasks, null, null, null, readiness: new[] { ApplicationReadiness.ReadyToApply }).Contains(job),
              "the readiness filter alone still shows it");

        // A second Mark Applied changes nothing, including the timestamp.
        Check(!JobTracker.MarkApplied(job, applied.AddDays(1)), "already applied");
        Equal(applied, job.AppliedAt, "AppliedAt is never rewritten");
        Equal(before - 1, JobTracker.CountNeedsAction(tasks), "count unchanged by the second click");
    }

    static void ReadinessFilterLabelAndOrder() {
        Equal("ReadyToApply,NeedsApplyLink,NeedsResume", string.Join(",", JobTracker.ReadinessFilterOrder), "explicit order");
        Equal(Enum.GetValues<ApplicationReadiness>().Length, JobTracker.ReadinessFilterOrder.Count, "every state is a choice");
        Equal(JobTracker.ReadinessFilterOrder.Count, JobTracker.ReadinessFilterOrder.Distinct().Count(), "no state twice");

        Equal("All readiness", JobTracker.ReadinessFilterLabel(null), "null");
        Equal("All readiness", JobTracker.ReadinessFilterLabel(new HashSet<ApplicationReadiness>()), "nothing ticked");
        Equal("All readiness", JobTracker.ReadinessFilterLabel(JobTracker.ReadinessFilterOrder.ToList()), "all three ticked");
        Equal("Ready to apply", JobTracker.ReadinessFilterLabel(new[] { ApplicationReadiness.ReadyToApply }), "one");
        Equal("Ready to apply, Needs apply link",
              JobTracker.ReadinessFilterLabel(new[] { ApplicationReadiness.NeedsApplyLink, ApplicationReadiness.ReadyToApply }),
              "two, in the fixed order whatever the tick order");
        Equal("Needs apply link, Needs resume",
              JobTracker.ReadinessFilterLabel(new[] { ApplicationReadiness.NeedsResume, ApplicationReadiness.NeedsApplyLink }), "another pair");

        // The shared helper directly: its "N noun" branch, which three states never reach.
        var order = new[] { "a", "b", "c", "d" };
        Equal("3 things", JobTracker.MultiSelectLabel(new[] { "a", "c", "d" }, order, s => s.ToUpperInvariant(), "All", "things"), "N noun");
        Equal("A, C", JobTracker.MultiSelectLabel(new[] { "c", "a" }, order, s => s.ToUpperInvariant(), "All", "things"), "names via displayName");
        Equal("All", JobTracker.MultiSelectLabel(order, order, s => s, "All", "things"), "every choice");
    }

    // ---------- apply link discovered at import ----------

    static string PayloadWithLinks(string? applyLink, string? originalUrl) => ScriptResultWith(p => {
        var next = p["next"]!.AsObject();
        next["applyLink"] = applyLink is null ? null : JsonValue.Create(applyLink);
        next["originalUrl"] = originalUrl is null ? null : JsonValue.Create(originalUrl);
    });

    static void ExtractorReadsApplyLink() {
        // The script asks for exactly these two named fields, from the same page-data object.
        Check(JobrightPageExtractor.ReadScript.Contains("applyLink: typeof j.applyLink === 'string'"), "script reads applyLink");
        Check(JobrightPageExtractor.ReadScript.Contains("originalUrl: typeof j.originalUrl === 'string'"), "script reads originalUrl");

        Equal("https://app.dover.com/apply/Cogniify/1e3fc78f?jr_id=6aada17f",
              JobrightPageExtractor.Parse(PayloadWithLinks("https://app.dover.com/apply/Cogniify/1e3fc78f?jr_id=6aada17f",
                                                           "https://boards.greenhouse.io/other/jobs/1")).ApplyUrl,
              "applyLink wins, as on Jobright's own Apply button");
        Equal("https://boards.greenhouse.io/acme/jobs/7",
              JobrightPageExtractor.Parse(PayloadWithLinks(null, "https://boards.greenhouse.io/acme/jobs/7")).ApplyUrl,
              "originalUrl when applyLink is absent");
        Equal("https://jobs.lever.co/acme/9",
              JobrightPageExtractor.Parse(PayloadWithLinks("https://jobright.ai/redirect/9", "https://jobs.lever.co/acme/9")).ApplyUrl,
              "originalUrl when applyLink is not an application address");

        // Stale page data (another job's) is not used, so its link is not either.
        var stale = JobrightPageExtractor.Parse(ScriptResultWith(p => {
            p["next"]!["jobId"] = "aaaaaaaaaaaaaaaaaaaaaaaa";
            p["next"]!["applyLink"] = "https://jobs.lever.co/someone-else/1";
        }));
        Check(stale.ApplyUrl is null, "a link from stale page data is never attributed to this job");
    }

    static void ExtractorApplyLinkMissingOrInvalid() {
        Check(JobrightPageExtractor.Parse(ScriptResult()).ApplyUrl is null, "signed-out shape: no link fields at all");
        Check(JobrightPageExtractor.Parse(ScriptResult("jobright-page-payload-no-jsonld.json")).ApplyUrl is null, "no link fields, no JSON-LD");
        Check(JobrightPageExtractor.Parse(PayloadWithLinks("", "")).ApplyUrl is null, "empty strings");
        foreach (var bad in new[] { "javascript:alert(1)", "file:///C:/x.html", "/apply/1", "not a url",
                                    "https://jobright.ai/jobs/info/6aac7fec95c707f49dff195f", "https://www.linkedin.com/company/1028" })
            Check(JobrightPageExtractor.Parse(PayloadWithLinks(bad, bad)).ApplyUrl is null, "refused: " + bad);
        Equal("ClearlyRated", JobrightPageExtractor.Parse(PayloadWithLinks("javascript:alert(1)", null)).Company,
              "a bad link never stops the job being read");
    }

    static JobImportData ImportData(string url, string? applyUrl) => new() {
        Company = "Cogniify", Title = "Senior Generative AI Engineer", JobUrl = url,
        Description = "Build things.", ApplyUrl = applyUrl
    };

    static void ImportRecordsApplyUrl() => WithLiveTasksFile(() => {
        var tasks = new List<JobTask>();
        var before = DateTime.Now;
        var outcome = JobImporter.ImportOne(ImportData("https://jobright.ai/jobs/info/6aada17fde327d3e210d3913",
                                                       "https://app.dover.com/apply/Cogniify/1e3fc78f?utm_source=jr"),
                                            JobImporter.BrowserSource, tasks);
        Equal(JobImportKind.Imported, outcome.Kind, "imported");
        Check(outcome.ApplyUrlRecorded, "the outcome says a link was recorded");
        var task = tasks.Single();
        Equal("https://app.dover.com/apply/Cogniify/1e3fc78f", task.ApplyUrl, "saved, normalized (tracking dropped)");
        Check(task.ApplyUrlCapturedAt is DateTime at && at >= before, "capture time stamped");
        Equal(ApplicationPlatform.Other, task.ApplicationPlatform, "Dover is detected as Other");
        Equal("https://jobright.ai/jobs/info/6aada17fde327d3e210d3913", task.Link, "Link keeps the Jobright posting");

        // Recognised platforms are detected at import too.
        var gh = JobImporter.ImportOne(ImportData("https://jobright.ai/jobs/info/6aac7fec95c707f49dff195f",
                                                  "https://boards.greenhouse.io/acme/jobs/7"), JobImporter.BrowserSource, tasks);
        Equal(ApplicationPlatform.Greenhouse, tasks.Single(t => t.JobId == gh.JobId).ApplicationPlatform, "Greenhouse detected at import");

        // And it is all in the saved file.
        var saved = Storage.LoadTasks().Single(t => t.JobId == outcome.JobId);
        Equal(task.ApplyUrl, saved.ApplyUrl, "ApplyUrl saved");
        Equal(ApplicationPlatform.Other, saved.ApplicationPlatform, "platform saved");
        Check(saved.ApplyUrlCapturedAt is not null, "time saved");
    });

    static void ImportWithoutApplyUrl() => WithLiveTasksFile(() => {
        var tasks = new List<JobTask>();
        foreach (var (i, link) in new[] { null, "", "javascript:alert(1)", "https://jobright.ai/jobs/info/abc", "not a url" }.Select((l, i) => (i, l))) {
            var outcome = JobImporter.ImportOne(ImportData($"https://example.com/job/{i}", link), JobImporter.IncomingSource, tasks);
            Equal(JobImportKind.Imported, outcome.Kind, $"imported despite link '{link}'");
            Check(!outcome.ApplyUrlRecorded, "nothing recorded");
            var task = tasks.Last();
            Equal("", task.ApplyUrl, "ApplyUrl stays empty");
            Check(task.ApplyUrlCapturedAt is null, "no capture time");
            Equal(ApplicationPlatform.Unknown, task.ApplicationPlatform, "platform Unknown");
        }

        // An Incoming file written before applyUrl existed still deserializes and imports.
        var old = JsonSerializer.Deserialize<JobImportData>(
            """{ "company": "Acme", "title": "Engineer", "jobUrl": "https://example.com/job/old", "description": "x" }""")!;
        Check(old.ApplyUrl is null, "older input has no applyUrl");
        Equal(JobImportKind.Imported, JobImporter.ImportOne(old, JobImporter.IncomingSource, tasks).Kind, "older input imports");
    });

    static void ImportFillsOnlyEmptyApplyUrl() => WithLiveTasksFile(() => {
        const string page = "https://jobright.ai/jobs/info/6aada17fde327d3e210d3913";
        var capturedAt = new DateTime(2026, 9, 18, 14, 35, 43);

        // Already has a link (e.g. from an Apply click): a re-import never replaces it.
        var existing = Job("RB-KEEP", "Cogniify", "Engineer", page);
        existing.ApplyUrl = "https://app.dover.com/apply/Cogniify/original";
        existing.ApplyUrlCapturedAt = capturedAt;
        existing.ApplicationPlatform = ApplicationPlatform.Other;
        var tasks = new List<JobTask> { existing };
        var outcome = JobImporter.ImportOne(ImportData(page, "https://boards.greenhouse.io/acme/jobs/7"), JobImporter.BrowserSource, tasks);
        Equal(JobImportKind.Duplicate, outcome.Kind, "still a duplicate");
        Check(!outcome.ApplyUrlRecorded, "nothing recorded");
        Equal("https://app.dover.com/apply/Cogniify/original", existing.ApplyUrl, "ApplyUrl not overwritten");
        Equal(capturedAt, existing.ApplyUrlCapturedAt, "capture time not overwritten");
        Equal(ApplicationPlatform.Other, existing.ApplicationPlatform, "platform not overwritten");
        Equal(1, tasks.Count, "no task added");

        // Has no link yet: the re-import fills it, and saves.
        var empty = Job("RB-FILL", "Acme", "Engineer", "https://jobright.ai/jobs/info/6aac7fec95c707f49dff195f");
        empty.ApplicationStatus = ApplicationStatus.Applied;
        tasks = new List<JobTask> { empty };
        outcome = JobImporter.ImportOne(ImportData(empty.Link, "https://jobs.lever.co/acme/1"), JobImporter.BrowserSource, tasks);
        Equal(JobImportKind.Duplicate, outcome.Kind, "duplicate");
        Check(outcome.ApplyUrlRecorded, "the empty link was filled");
        Equal("https://jobs.lever.co/acme/1", empty.ApplyUrl, "filled");
        Equal(ApplicationPlatform.Lever, empty.ApplicationPlatform, "platform detected");
        Equal(ApplicationStatus.Applied, empty.ApplicationStatus, "application status untouched");
        Equal("https://jobs.lever.co/acme/1", Storage.LoadTasks().Single().ApplyUrl, "and saved");

        // A duplicate with no usable link changes and saves nothing.
        File.Delete(Storage.TasksPath);
        var none = Job("RB-NONE", "Acme", "Engineer", "https://jobright.ai/jobs/info/bbbbbbbbbbbbbbbbbbbbbbbb");
        JobImporter.ImportOne(ImportData(none.Link, "javascript:alert(1)"), JobImporter.BrowserSource, new List<JobTask> { none });
        Equal("", none.ApplyUrl, "still empty");
        Check(!File.Exists(Storage.TasksPath), "a duplicate with nothing new is not saved");
    });

    // ---------- application platform detection ----------

    static void PlatformIs(ApplicationPlatform expected, string? url) =>
        Equal(expected, ApplicationPlatformDetector.Detect(url), url ?? "null");

    static void PlatformMatching() {
        PlatformIs(ApplicationPlatform.Greenhouse, "https://boards.greenhouse.io/company/jobs/12345");
        PlatformIs(ApplicationPlatform.Greenhouse, "https://job-boards.greenhouse.io/company/jobs/12345");
        PlatformIs(ApplicationPlatform.Greenhouse, "https://boards.eu.greenhouse.io/company/jobs/12345");
        PlatformIs(ApplicationPlatform.Workday, "https://company.wd5.myworkdayjobs.com/job/12345");
        PlatformIs(ApplicationPlatform.Workday, "https://COMPANY.WD1.MYWORKDAYJOBS.COM/en-US/External/job/X_R1");
        PlatformIs(ApplicationPlatform.Workday, "https://wd3.myworkdaysite.com/recruiting/company/External/job/1");
        PlatformIs(ApplicationPlatform.Lever, "https://jobs.lever.co/company/12345");
        PlatformIs(ApplicationPlatform.Lever, "https://jobs.eu.lever.co/company/12345/apply");
        PlatformIs(ApplicationPlatform.LinkedIn, "https://www.linkedin.com/jobs/view/12345");
        PlatformIs(ApplicationPlatform.Ashby, "https://jobs.ashbyhq.com/company/12345");
        PlatformIs(ApplicationPlatform.SmartRecruiters, "https://jobs.smartrecruiters.com/Company/12345-engineer");
        PlatformIs(ApplicationPlatform.ICims, "https://careers-company.icims.com/jobs/12345/engineer/job");
        PlatformIs(ApplicationPlatform.Greenhouse, "http://boards.greenhouse.io/company/jobs/1");
    }

    static void PlatformLookAlikes() {
        PlatformIs(ApplicationPlatform.Other, "https://notgreenhouse.io/company/jobs/1");
        PlatformIs(ApplicationPlatform.Other, "https://greenhouse.io.evil.com/company/jobs/1");
        PlatformIs(ApplicationPlatform.Other, "https://mylever.co/jobs/1");
        PlatformIs(ApplicationPlatform.Other, "https://fakemyworkdayjobs.com/job/1");
        PlatformIs(ApplicationPlatform.Other, "https://example.com/boards.greenhouse.io/jobs/1");
        PlatformIs(ApplicationPlatform.Other, "https://example.com/apply?next=https://jobs.lever.co/x/1");
        // LinkedIn counts only for its job postings.
        PlatformIs(ApplicationPlatform.Other, "https://www.linkedin.com/company/1028");
        PlatformIs(ApplicationPlatform.Other, "https://www.linkedin.com/in/someone/");
    }

    static void PlatformEmbeddedLinks() {
        PlatformIs(ApplicationPlatform.Greenhouse, "https://careers.example.com/jobs?gh_jid=4012345");
        PlatformIs(ApplicationPlatform.Greenhouse, "https://www.example.com/open-roles/?utm_source=x&GH_JID=7");
        PlatformIs(ApplicationPlatform.Ashby, "https://example.com/careers?ashby_jid=5b1c-22");
        // A parameter that only contains the name is not the embed id.
        PlatformIs(ApplicationPlatform.Other, "https://example.com/careers?not_gh_jid=1");
        PlatformIs(ApplicationPlatform.Other, "https://example.com/careers#gh_jid=1");
    }

    static void PlatformMissingOrUnknown() {
        foreach (var missing in new[] { null, "", "   ", "not a url", "/jobs/1", "about:blank", "mailto:jobs@x.com", "ftp://jobs.lever.co/x" })
            PlatformIs(ApplicationPlatform.Unknown, missing);
        PlatformIs(ApplicationPlatform.Other, "https://careers.oracle.com/jobs/12345");
        PlatformIs(ApplicationPlatform.Other, "https://example.com/");
        Equal(ApplicationPlatform.Unknown, new JobTask().ApplicationPlatform, "a new task starts Unknown");
    }

    static void PlatformTolerantLoading() => WithLiveTasksFile(() => {
        Directory.CreateDirectory(Storage.DataDir);
        File.WriteAllText(Storage.TasksPath, """
        [
          { "JobId": "P-1", "ApplicationPlatform": "Greenhouse" },
          { "JobId": "P-2", "ApplicationPlatform": "greenhouse" },
          { "JobId": "P-3", "ApplicationPlatform": "Taleo" },
          { "JobId": "P-4", "ApplicationPlatform": null },
          { "JobId": "P-5", "ApplicationPlatform": 999 },
          { "JobId": "P-6", "ApplicationPlatform": 2 },
          { "JobId": "P-7", "ApplicationPlatform": "3" },
          { "JobId": "P-8", "ApplicationPlatform": { "name": "Lever" } },
          { "JobId": "P-9", "ApplicationPlatform": [ "Lever" ] },
          { "JobId": "P-10", "ApplicationPlatform": "" }
        ]
        """);

        var tasks = Storage.LoadTasks();
        Equal(10, tasks.Count, "no task is lost to an unreadable platform");
        Equal(ApplicationPlatform.Greenhouse, tasks[0].ApplicationPlatform, "a known name");
        Equal(ApplicationPlatform.Greenhouse, tasks[1].ApplicationPlatform, "case does not matter");
        Equal(ApplicationPlatform.Unknown, tasks[2].ApplicationPlatform, "a name this version does not know");
        Equal(ApplicationPlatform.Unknown, tasks[3].ApplicationPlatform, "null");
        Equal(ApplicationPlatform.Unknown, tasks[4].ApplicationPlatform, "an out-of-range number");
        Equal(ApplicationPlatform.Workday, tasks[5].ApplicationPlatform, "a defined number");
        Equal(ApplicationPlatform.Unknown, tasks[6].ApplicationPlatform, "a number written as text");
        Equal(ApplicationPlatform.Unknown, tasks[7].ApplicationPlatform, "an object");
        Equal(ApplicationPlatform.Unknown, tasks[8].ApplicationPlatform, "an array");
        Equal(ApplicationPlatform.Unknown, tasks[9].ApplicationPlatform, "empty text");
        Equal("P-10", tasks[9].JobId, "reading continues correctly after skipped values");
    });

    static void PlatformSaveReload() => WithLiveTasksFile(() => {
        var captured = Job("RB-PLAT-1", "Oracle", "ML Engineer", ApplyJobPage);
        captured.ApplyUrl = "https://jobs.lever.co/oracle/1";
        captured.ApplicationPlatform = ApplicationPlatform.Lever;
        Storage.SaveTasks(new[] { captured, Job("RB-PLAT-2") });

        var text = File.ReadAllText(Storage.TasksPath);
        Check(text.Contains("\"ApplicationPlatform\": \"Lever\""), "stored as the name, not a number");

        var reloaded = Storage.LoadTasks();
        Equal(ApplicationPlatform.Lever, reloaded[0].ApplicationPlatform, "platform survived");
        Equal("https://jobs.lever.co/oracle/1", reloaded[0].ApplyUrl, "ApplyUrl survived");
        Equal(ApplicationPlatform.Unknown, reloaded[1].ApplicationPlatform, "an uncaptured task stays Unknown");

        // The shape tasks.json had before the platform existed.
        File.WriteAllText(Storage.TasksPath, $$"""
        [ { "JobId": "RB-OLD-P", "Link": "{{ApplyJobPage}}", "ApplyUrl": "https://boards.greenhouse.io/o/jobs/1",
            "ApplyUrlCapturedAt": "2026-09-18T14:00:00", "Status": "Completed" },
          { "JobId": "RB-OLD-Q", "Status": "Queued" } ]
        """);
        var older = Storage.LoadTasks();
        Equal(2, older.Count, "older tasks load");
        Equal(ApplicationPlatform.Unknown, older[0].ApplicationPlatform, "no stored platform reads as Unknown");
        Equal("https://boards.greenhouse.io/o/jobs/1", older[0].ApplyUrl, "its ApplyUrl is kept");
        Equal(ApplicationPlatform.Unknown, older[1].ApplicationPlatform, "no ApplyUrl, Unknown");
    });

    static void PlatformDerivedFromApplyUrl() {
        var now = new DateTime(2026, 9, 18, 16, 0, 0);
        var job = Job("RB-DER-1", "Oracle", "ML Engineer", ApplyJobPage);
        var tasks = new List<JobTask> { job };

        ApplyCapture.Record(tasks, ApplyJobPage, "https://jobs.ashbyhq.com/oracle/1", now);
        Equal(ApplicationPlatform.Ashby, job.ApplicationPlatform, "capture detects the platform");

        ApplyCapture.Record(tasks, ApplyJobPage, "https://careers.oracle.com/jobs/2", now);
        Equal(ApplicationPlatform.Other, job.ApplicationPlatform, "a replaced address re-detects");

        // An unknown job and a refused address leave the platform alone.
        ApplyCapture.Record(tasks, ApplyJobPage, "https://www.linkedin.com/in/someone/", now);
        Equal(ApplicationPlatform.Other, job.ApplicationPlatform, "a refused address changes nothing");

        // Startup refresh: a task loaded with an ApplyUrl but no platform gets one; the rest are untouched.
        var loaded = Job("RB-DER-2"); loaded.ApplyUrl = "https://boards.greenhouse.io/x/jobs/1";
        var stale = Job("RB-DER-3"); stale.ApplyUrl = "https://jobs.lever.co/x/1"; stale.ApplicationPlatform = ApplicationPlatform.Workday;
        var none = Job("RB-DER-4");
        var set = new List<JobTask> { loaded, stale, none };

        Equal(2, ApplicationPlatformDetector.Refresh(set), "two tasks needed a platform");
        Equal(ApplicationPlatform.Greenhouse, loaded.ApplicationPlatform, "filled in");
        Equal(ApplicationPlatform.Lever, stale.ApplicationPlatform, "corrected from its ApplyUrl");
        Equal(ApplicationPlatform.Unknown, none.ApplicationPlatform, "no ApplyUrl stays Unknown");
        Equal(0, ApplicationPlatformDetector.Refresh(set), "a second refresh changes nothing (no needless save)");
    }

    // ---------- critical pipeline (isolated component chain) ----------

    /// <summary>
    /// The production path without ChatGPT or Jobright: ImportOne → Prepare → ResultCapture.Accept →
    /// ResumeGenerator → MarkResumeReady. Uses temp ResumeRoot and restores anything written under
    /// the live DataDir (tasks, prepared-request, results\&lt;id&gt;.*).
    /// </summary>
    static void CriticalPipelineChain() => WithTasksFileRestored(() => WithPreparedFiles(() => {
        var tasks = new List<JobTask>();
        var jobUrl = "https://example.com/sample/e2e-pipeline/" + Guid.NewGuid().ToString("N");
        var outcome = JobImporter.ImportOne(new JobImportData {
            Company = "Pipeline Test Co",
            Title = "Senior Validation Engineer",
            JobUrl = jobUrl,
            CompanyUrl = "https://example.com/",
            Description = "Validate the Resume Builder end-to-end document path."
        }, JobImporter.BrowserSource, tasks);

        Equal(JobImportKind.Imported, outcome.Kind, "import succeeded");
        var job = tasks.Single();
        Equal("Queued", job.Status, "new job is Queued");
        Equal(ApplicationStatus.Viewed, job.ApplicationStatus, "new job is Viewed");

        // Clean any leftover result files for this id after the run.
        var resultArtifacts = new[] {
            ProfileResultStore.ResultPath(job.JobId),
            ProfileResultStore.RawPath(job.JobId),
            ProfileResultStore.DocGenLogPath(job.JobId),
            ProfileResultStore.EffectiveStylePath(job.JobId)
        };
        foreach (var p in resultArtifacts) if (File.Exists(p)) File.Delete(p);

        try {
            var prepared = RequestPreparation.Prepare(job, PromptSettings(PromptModes.Resume));
            Equal(job.JobId, prepared.JobId, "prepared request is for this job");
            Check(prepared.Text.Contains(job.Company, StringComparison.Ordinal), "prepared text names the company");
            Check(prepared.Text.Contains(job.Title, StringComparison.Ordinal), "prepared text names the title");
            Check(prepared.Text.Contains("===== COMPLETE JOB PAYLOAD =====", StringComparison.Ordinal), "payload section present");
            Check(File.Exists(RequestPreparation.PreparedPath), "prepared-request.json written");

            // Simulate a successful clipboard capture with a known-good profile fixture.
            var answer = File.ReadAllText(Fixture("resume-prom-v4.12.json"));
            Check(ResultCapture.ShouldCapture(answer), "fixture looks like a capturable profile");
            var captured = ResultCapture.Accept(answer, job.JobId);
            Check(captured.Saved, "capture saved: " + captured.Message);
            Equal(ProfileResultStore.ResultPath(job.JobId), captured.TargetPath, "tailored result path");
            Check(File.Exists(captured.TargetPath), "results\\<jobId>.json exists");
            Check(!File.Exists(CandidateProfileStore.CandidateProfilePath) ||
                  !File.ReadAllText(CandidateProfileStore.CandidateProfilePath).Equals(File.ReadAllText(captured.TargetPath), StringComparison.Ordinal),
                  "baseline candidate-profile is not overwritten by a job result");

            job.Status = "Completed";

            var resumeRoot = NewDir("pipeline-resume-root");
            var settings = PromptSettings(PromptModes.Resume);
            settings.ResumeRootFolder = resumeRoot;
            settings.Docx = true;
            settings.Pdf = true;

            var generation = ResumeGenerator.Generate(
                job.Company, job.Title, captured.TargetPath, settings,
                ProfileResultStore.EffectiveStylePath(job.JobId), job.JobId, job.Link);

            Check(generation.DocxGenerated, "DOCX generated: " + (generation.DocxError ?? generation.FatalError ?? "ok"));
            Check(generation.PdfGenerated, "PDF generated: " + (generation.PdfError ?? generation.FatalError ?? "ok"));
            Check(File.Exists(generation.DocxPath!), "DOCX file on disk");
            Check(File.Exists(generation.PdfPath!), "PDF file on disk");
            Check(File.Exists(Path.Combine(generation.OutputFolder!, "resume-info.json")) ||
                  Directory.GetFiles(generation.OutputFolder!, "resume-info*.json").Length > 0,
                  "resume-info.json written");

            // Content smoke: DOCX opens and carries candidate name from the fixture.
            using (var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(generation.DocxPath!, false)) {
                var body = doc.MainDocumentPart?.Document?.Body?.InnerText ?? "";
                Check(body.Contains("BILLY", StringComparison.OrdinalIgnoreCase) ||
                      body.Contains("Billy", StringComparison.OrdinalIgnoreCase),
                      "DOCX carries the fixture candidate name");
            }

            Check(JobTracker.MarkResumeReady(job, generation.DocxPath), "MarkResumeReady moves Viewed → Ready");
            Equal(ApplicationStatus.Ready, job.ApplicationStatus, "application status is Ready");
            Check(!string.IsNullOrWhiteSpace(job.ResumePath), "ResumePath recorded");
            Equal("Completed", job.Status, "queue Status stays Completed");
        }
        finally {
            foreach (var p in resultArtifacts) {
                try { if (File.Exists(p)) File.Delete(p); } catch { }
            }
        }
    }));

    static void CaptureGateRefusals() {
        Check(!ResultCapture.ShouldCapture(null), "null clipboard");
        Check(!ResultCapture.ShouldCapture(""), "empty clipboard");
        Check(!ResultCapture.ShouldCapture("hello world"), "plain text is not a profile");
        Check(!ResultCapture.ShouldCapture("{ \"foo\": 1 }"), "unrelated JSON is not a profile");

        var prepared = "===== COMPLETE JOB PAYLOAD =====\r\n" + new string('x', 80);
        Check(PromptEchoGuard.IsEchoOfPrompt(prepared, prepared), "verbatim prompt echo is refused");
        Check(!PromptEchoGuard.IsEchoOfPrompt("{\"info\":{},\"summary\":\"A real answer with enough length here.\"}", prepared),
              "a real answer is not treated as an echo");

        // Invalid shape that looks like a failed attempt: kept as raw, never saved as profile.
        WithTasksFileRestored(() => {
            var jobId = "RB-CAPTURE-REFUSE-" + Guid.NewGuid().ToString("N")[..8];
            var raw = "```json\n{\"info\": {}, \"summary\": }\n```"; // broken JSON with markers
            Check(ResultCapture.LooksLikeFailedProfileAttempt(raw) || !ResultCapture.LooksLikeProfileResult(raw),
                  "broken fence is not a valid profile");
            var result = ResultCapture.Accept(raw, jobId);
            Check(!result.Saved, "invalid capture is not saved");
            Check(!File.Exists(ProfileResultStore.ResultPath(jobId)), "no results json for a failed capture");
            try {
                var rawPath = ProfileResultStore.RawPath(jobId);
                if (File.Exists(rawPath)) File.Delete(rawPath);
            } catch { }
        });
    }

    /// <summary>
    /// Mirrors MainWindow page hosting: Visibility changes call EnsureAsync again without Release.
    /// A second Ensure while alive must not create another browser.
    /// </summary>
    static void ChatHostSurvivesNavigation() {
        var creates = 0; var disposes = 0;
        var host = new ChatHost(
            create: () => { creates++; return Task.FromResult(1000 + creates); },
            dispose: () => { disposes++; return Task.FromResult(true); });

        host.EnsureAsync().GetAwaiter().GetResult();
        host.EnsureAsync().GetAwaiter().GetResult(); // "navigate away and back"
        host.EnsureAsync().GetAwaiter().GetResult();

        Equal(1, creates, "one browser for repeated Ensure while alive");
        Equal(1, host.Creations, "ChatHost creation counter");
        Equal(0, disposes, "navigation does not dispose");
        Check(host.IsAlive, "browser still alive");

        host.ReleaseAsync().GetAwaiter().GetResult();
        Equal(1, disposes, "explicit release disposes once");
        Check(!host.IsAlive, "released");
    }

    static void ChatHostDesyncDetection() {
        Check(ChatHost.IsDesynced(hostReportsAlive: true, coreWebViewAvailable: false),
              "alive without a core view is desynced");
        Check(!ChatHost.IsDesynced(hostReportsAlive: true, coreWebViewAvailable: true),
              "alive with a core view is fine");
        Check(!ChatHost.IsDesynced(hostReportsAlive: false, coreWebViewAvailable: false),
              "dead host is not desynced — Ensure will create");
    }

    static string FindThemesFile(string fileName) {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null) {
            var candidate = Path.Combine(dir.FullName, "Themes", fileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Themes/" + fileName);
    }

    /// <summary>
    /// Dark-theme regression: Text.Primary on Bg.Input must stay readable in both palettes.
    /// Parses the theme XAML color literals (no live UI) and requires WCAG AA contrast (≥ 4.5).
    /// </summary>
    static void ThemeInputContrast() {
        foreach (var name in new[] { "Colors.Dark.xaml", "Colors.Light.xaml" }) {
            var path = FindThemesFile(name);
            var xaml = File.ReadAllText(path);
            var bg = ThemeBrushHex(xaml, "Bg.Input");
            var fg = ThemeBrushHex(xaml, "Text.Primary");
            var muted = ThemeBrushHex(xaml, "Text.Muted");
            var ratio = RelativeLuminanceContrast(bg, fg);
            Check(ratio >= 4.5, $"{name}: Text.Primary on Bg.Input contrast {ratio:0.00} (need ≥ 4.5)");
            var mutedRatio = RelativeLuminanceContrast(bg, muted);
            Check(mutedRatio >= 3.0, $"{name}: Text.Muted on Bg.Input contrast {mutedRatio:0.00} (need ≥ 3.0)");
        }
    }

    static void ThemeControlStylesUseDynamicResources() {
        var path = FindThemesFile("Controls.xaml");
        var xaml = File.ReadAllText(path);
        Check(xaml.Contains("TargetType=\"TextBox\""), "TextBox style present");
        Check(xaml.Contains("TargetType=\"ComboBox\""), "ComboBox style present");
        Check(xaml.Contains("TargetType=\"PasswordBox\""), "PasswordBox style present");
        Check(xaml.Contains("PART_ContentHost"), "TextBox/PasswordBox content host templated");
        Check(xaml.Contains("TextElement.Foreground=\"{TemplateBinding Foreground}\""),
              "foreground applied on content host");
        // Input chrome must use DynamicResource so a theme switch updates without restart.
        var inputBlock = xaml.IndexOf("TargetType=\"TextBox\"", StringComparison.Ordinal);
        Check(inputBlock >= 0, "TextBox style index");
        var slice = xaml.Substring(inputBlock, Math.Min(900, xaml.Length - inputBlock));
        Check(slice.Contains("DynamicResource Bg.Input"), "TextBox Background is DynamicResource Bg.Input");
        Check(slice.Contains("DynamicResource Text.Primary"), "TextBox Foreground is DynamicResource Text.Primary");
        Check(!slice.Contains("Background=\"White\"") && !slice.Contains("Background=\"#FFFFFF\""),
              "TextBox style does not hardcode white background");
    }

    static string ThemeBrushHex(string xaml, string key) {
        var marker = $"x:Key=\"{key}\"";
        var i = xaml.IndexOf(marker, StringComparison.Ordinal);
        Check(i >= 0, "brush " + key);
        var colorIdx = xaml.IndexOf("Color=\"#", i, StringComparison.Ordinal);
        Check(colorIdx >= 0 && colorIdx < i + 120, "Color near " + key);
        return xaml.Substring(colorIdx + 8, 6);
    }

    static double RelativeLuminanceContrast(string bgHex, string fgHex) {
        double L(string hex) {
            double Chan(int offset) {
                var c = Convert.ToInt32(hex.Substring(offset, 2), 16) / 255.0;
                return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Chan(0) + 0.7152 * Chan(2) + 0.0722 * Chan(4);
        }
        var a = L(bgHex); var b = L(fgHex);
        var lighter = Math.Max(a, b); var darker = Math.Min(a, b);
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>
    /// The production sequence after a completed job: RecycleAsync then EnsureAsync for the next
    /// job must create a second browser, not no-op on a stale IsAlive.
    /// </summary>
    static void ChatHostRecycleThenEnsure() {
        var creates = 0; var disposes = 0;
        var host = new ChatHost(
            create: () => { creates++; return Task.FromResult(2000 + creates); },
            dispose: () => { disposes++; return Task.FromResult(true); });

        host.EnsureAsync().GetAwaiter().GetResult();
        Equal(1, creates, "job 1 browser");

        host.RecycleAsync().GetAwaiter().GetResult();
        Equal(1, disposes, "job 1 recycled");
        Check(!host.IsAlive, "dead after recycle");

        // Simulate the desync MainWindow guards against: host wrongly still "alive".
        Check(ChatHost.IsDesynced(true, false), "desync helper matches the guard");

        host.EnsureAsync().GetAwaiter().GetResult();
        Equal(2, creates, "job 2 gets a new browser");
        Check(host.IsAlive, "alive for job 2");
        Equal(2, host.Creations, "creation counter");
    }

    static void CopyShortcutIsSemicolon() {
        Equal((ushort)0xBA, KeyboardSimulator.CopyShortcutVk, "VK_OEM_1 / semicolon");
        Equal("Ctrl+Shift+;", KeyboardSimulator.CopyShortcutText, "display text");
        Check(!string.Equals(KeyboardSimulator.CopyShortcutText, "Ctrl+Shift+I", StringComparison.Ordinal),
              "must not claim letter I");
    }

    static void ComposerReadyWaitsForProbe() {
        var calls = 0;
        var delays = new List<int>();
        var ready = ChatComposer.WaitForComposerAsync(
            probe: () => {
                calls++;
                return Task.FromResult(calls >= 3 ? "ready" : "missing");
            },
            budgetMs: 5000,
            delay: (ms, _) => { delays.Add(ms); return Task.CompletedTask; }).GetAwaiter().GetResult();
        Check(ready, "becomes ready on third poll");
        Equal(3, calls, "polled until ready");
        Check(delays.Count >= 1, "used poll delays after the first try");

        var never = ChatComposer.WaitForComposerAsync(
            probe: () => Task.FromResult("missing"),
            budgetMs: 300,
            delay: (ms, _) => Task.CompletedTask).GetAwaiter().GetResult();
        Check(!never, "budget expiry returns false");
    }

    static void QueueStartRefusesWhileRunning() {
        var a = new JobTask { JobId = "RB-S-1", Company = "A", Title = "T", Status = "Queued" };
        var b = new JobTask { JobId = "RB-S-2", Company = "B", Title = "T", Status = "Queued" };
        var queue = new QueueRunner();
        Equal(2, queue.Start(new[] { a, b }), "first start");
        Equal(0, queue.Start(new[] { a, b }), "second start refused while running");
        Check(queue.IsRunning, "still running");
        var first = queue.Next();
        Equal("RB-S-1", first!.JobId, "first job");
        a.Status = "Failed";
        queue.AbandonActive();
        var second = queue.Next();
        Equal("RB-S-2", second!.JobId, "advances to second Queued job");
        queue.Stop();
    }

    static void QueueNextSkipsFailed() {
        var a = new JobTask { JobId = "RB-F-1", Company = "A", Title = "T", Status = "Queued" };
        var queue = new QueueRunner();
        Equal(1, queue.Start(new[] { a }), "one job");
        Equal(a, queue.Next(), "selected");
        a.Status = "Failed";
        a.FailureReason = JobTask.CaptureTimeoutReason;
        queue.OnCaptureTimedOut(null);
        Check(queue.Next() is null, "no further Queued jobs — Finished");
        Check(!queue.IsRunning, "queue finished after terminal job");
        // A new Start after Finished is how Retry Failed + Start Queue works — only Queued jobs enter.
        Equal(0, queue.Start(new[] { a }), "Failed job is not eligible for a new Start");
        a.Status = "Queued"; // explicit Retry Failed
        a.FailureReason = "";
        Equal(1, queue.Start(new[] { a }), "after explicit re-queue it is eligible again");
        queue.Stop();
    }

    static void ClipboardChangedSinceArm() {
        // Same predicate as ClipboardWatcher.ChangedSinceArm (kept pure here so the harness does not
        // pull in the full WPF clipboard implementation).
        static bool Changed(uint armed, uint current) =>
            armed == 0 || current == 0 || current != armed;
        Check(Changed(10, 11), "sequence moved");
        Check(!Changed(10, 10), "sequence unchanged");
        Check(Changed(0, 10), "unknown armed seq does not block");
        Check(Changed(10, 0), "unknown current seq does not block");
    }

    static void BrowserGenerationInvalidatesStaleOps() {
        var gen = new BrowserGeneration();
        Equal(0, gen.Current, "starts at zero");
        var g1 = gen.BeginNew();
        Equal(1, g1, "first browser");
        Check(gen.IsCurrent(g1), "g1 is live");
        Check(CopyFocusPolicy.MayTouchBrowser(g1, gen.Current, cancelled: false, viewExists: true), "may touch");

        gen.Invalidate(); // dispose begins
        Check(!gen.IsCurrent(g1), "g1 stale after invalidate");
        Check(!CopyFocusPolicy.MayTouchBrowser(g1, gen.Current, cancelled: false, viewExists: false),
              "null view + stale gen");

        var g2 = gen.BeginNew();
        Check(g2 > g1, "replacement has a new generation");
        Check(!CopyFocusPolicy.MayTouchBrowser(g1, gen.Current, cancelled: false, viewExists: true),
              "old gen must not touch the replacement view");
        Check(CopyFocusPolicy.MayTouchBrowser(g2, gen.Current, cancelled: false, viewExists: true),
              "new gen may touch");
        Check(!CopyFocusPolicy.MayTouchBrowser(g2, gen.Current, cancelled: true, viewExists: true),
              "cancelled token blocks");
    }

    static void CopyFocusPolicyDecisions() {
        Check(!CopyFocusPolicy.ShouldFocusOnAnswerReady(
                  copyPathAlreadyFocused: true, captureStillArmed: true, jobStillActive: true),
              "skip ShowAnswerReady focus after RequestCopyAsync");
        Check(CopyFocusPolicy.ShouldFocusOnAnswerReady(
                  copyPathAlreadyFocused: false, captureStillArmed: true, jobStillActive: true),
              "focus when copy path did not run");
        Check(!CopyFocusPolicy.ShouldFocusOnAnswerReady(
                  copyPathAlreadyFocused: false, captureStillArmed: false, jobStillActive: true),
              "skip when capture already accepted");

        Check(CopyFocusPolicy.ShouldRetryCopyOnForeground(
                  nowOwned: true, sameGeneration: true, jobStillProcessing: true,
                  captureArmed: true, alreadyCopied: false),
              "retry when we regain foreground");
        Check(!CopyFocusPolicy.ShouldRetryCopyOnForeground(
                  nowOwned: true, sameGeneration: true, jobStillProcessing: true,
                  captureArmed: true, alreadyCopied: true),
              "no retry after clipboard already changed");
        Check(!CopyFocusPolicy.ShouldRetryCopyOnForeground(
                  nowOwned: false, sameGeneration: true, jobStillProcessing: true,
                  captureArmed: true, alreadyCopied: false),
              "no retry while another app owns foreground");
    }

    static void StaleGenerationCannotTouchReplacement() {
        // Simulates: Job A Ready → copy → capture → recycle (invalidate) → Job B create (BeginNew).
        // A delayed FocusChatPane from Job A must not act on Job B's view.
        var gen = new BrowserGeneration();
        var jobA = gen.BeginNew();
        gen.Invalidate();
        var jobB = gen.BeginNew();
        Check(!CopyFocusPolicy.MayTouchBrowser(jobA, gen.Current, false, true),
              "Job A focus after recycle must not touch Job B");
        Check(CopyFocusPolicy.MayTouchBrowser(jobB, gen.Current, false, true),
              "Job B may focus its own browser");
    }

    static void QueueRunnerCaptureAttribution() {
        var a = new JobTask { JobId = "RB-Q-A", Company = "A", Title = "T", Status = "Queued" };
        var b = new JobTask { JobId = "RB-Q-B", Company = "B", Title = "T", Status = "Processing" }; // stale
        var tasks = new List<JobTask> { a, b };

        Equal(1, QueueRunner.RecoverStaleProcessing(tasks), "one stale Processing recovered");
        Equal("Queued", b.Status, "stale job re-queued");

        var queue = new QueueRunner();
        Equal(2, queue.Start(tasks), "two queued jobs");
        var first = queue.Next();
        Equal(a.JobId, first!.JobId, "first job is active");
        Equal(a.JobId, queue.ActiveJobId, "ActiveJobId set");

        const string prepared = "prepared request body that is long enough for echo detection xxxxxxxx";
        Equal(CaptureDecision.Accept, queue.Classify("profile-answer-one", prepared), "first capture accepted");
        queue.OnCaptureSucceeded("profile-answer-one");

        // Activate the next job so Classify has somewhere to attribute to.
        a.Status = "Completed";
        var second = queue.Next();
        Equal(b.JobId, second!.JobId, "second job becomes active");
        Equal(CaptureDecision.Duplicate, queue.Classify("profile-answer-one", prepared), "same text refused for next job");

        queue.OnCaptureTimedOut("late-clipboard-text");
        // After timeout ActiveJobId is null — begin again to test late quarantine.
        queue.BeginJob(b.JobId);
        Equal(CaptureDecision.LateResponse, queue.Classify("late-clipboard-text", prepared), "timed-out clipboard quarantined");
        Equal(CaptureDecision.PromptEcho, queue.Classify(prepared, prepared), "prompt echo classified");
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
