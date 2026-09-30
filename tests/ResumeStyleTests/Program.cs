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

    static string? OnlyTest;

    static int Main(string[] args) {
        if (args.Length > 0) OnlyTest = args[0];
        TempRoot = Path.Combine(Path.GetTempPath(), "ResumeBuilderStyleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TempRoot);
        // Job saves mirror into SQLite. Keep that file in the temp folder, never the user's database.
        JobStore.DatabasePathOverride = Path.Combine(TempRoot, "test-default.db");

        // The harness must not append to the user's real diagnostics.log.
        PerfLog.Enabled = false;
        // Existing prepare checks read the prompt file they just wrote. A converted copy is opt-in.
        PromptConversion.AllowUnadaptedSource = true;
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
            Console.WriteLine("Tailored DOCX content");
            Test("accepted tailored text replaces every stale source paragraph", TailoredDocxDropsStaleSourceText);
            Test("a request resume is the style source, not the original resume", RequestResumeBeatsOriginalStyle);
            Test("the last selected style reference is used when the request names none", SelectedStyleReferenceIsUsed);
            Test("no style reference uses the app default, not the original resume", DefaultStyleIgnoresOriginalResume);

            Console.WriteLine();
            Console.WriteLine("Prompt conversion");
            Test("an unchanged prompt reuses its converted copy", PromptConversionReusesUnchangedSource);
            Test("a changed prompt is not treated as ready", PromptConversionDetectsSourceChange);
            Test("a rejected conversion keeps the previous copy", PromptConversionKeepsPreviousOnFailure);
            Test("an unconverted prompt is refused for job preparation", UnadaptedPromptIsRefused);
            Test("an old JSON prompt adaptation is not reused", PromptConversionRejectsJsonContract);

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
            Test("import captures the company icon and the job-site icon", ImportCapturesCompanyAndPlatformIcons);
            Test("Applications consistency: Failed is application status only", ApplicationsConsistencyFailedCount);
            Test("Applications consistency: company logo URL", ApplicationsConsistencyLogoUrl);
            Test("Applications consistency: platform list comes from the jobs", ApplicationsConsistencyPlatformList);
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
            Test("a helper without an external link uses __NEXT_DATA__", ExtractorApplyUrlFallsBackToNextData);
            Test("a missing or invalid page link leaves ApplyUrl empty", ExtractorApplyLinkMissingOrInvalid);
            Test("import saves a discovered ApplyUrl with its time and platform", ImportRecordsApplyUrl);
            Test("import without a usable link still succeeds, link empty", ImportWithoutApplyUrl);
            Test("an existing ApplyUrl is never overwritten; an empty one is filled", ImportFillsOnlyEmptyApplyUrl);

            Console.WriteLine();
            Console.WriteLine("ChatGPT rate limit and pacing");
            Test("the rate-limit wording is detected; the probe only reads", RateLimitIsDetectedFromItsWording);
            Test("a rate limit ends the answer watch at once", RateLimitEndsTheWatchAtOnce);
            Test("nothing is sent during the configured cooldown; Stop cancels it", NothingIsSentDuringACooldown);
            Test("the same job resumes afterwards, not failed", TheSameJobResumesAfterTheCooldown);
            Test("the configured pause separates jobs; 0 = immediately; Stop cancels", TheConfiguredPauseSeparatesJobs);
            Test("pacing settings default, persist and validate", PacingSettingsPersistAndValidate);

            Console.WriteLine();
            Console.WriteLine("Phase 1: runtime safety and install-readiness");
            Test("settings.json is saved atomically with a .bak", SettingsSaveIsAtomicWithBackup);
            Test("tasks.json is saved atomically with a .bak", TasksSaveIsAtomicWithBackup);
            Test("a corrupt settings.json is kept and never overwritten", CorruptSettingsIsNeverOverwritten);
            Test("a corrupt tasks.json is kept and never overwritten", CorruptTasksIsNeverOverwritten);
            Test("a corrupt file is restored from its .bak", CorruptFileRecoversFromBackup);
            Test("missing files are a normal first run", MissingStateFilesAreAFirstRun);
            Test("a locked file returns the last good copy, not an empty list", LockedStateFileKeepsTheLastGoodCopy);
            Test("the default output folder only fills a blank setting", DefaultResumeRootOnlyFillsABlank);
            Test("CurrentResume.docx follows the Resume Root", CurrentResumeFollowsTheResumeRoot);
            Test("only one instance can hold the mutex", SingleInstanceAllowsOnlyOne);
            Test("logs rotate and crash logs are bounded", LogsAreRotatedAndBounded);
            Test("a crash is logged once and logging never throws", CrashLogIsWrittenOncePerException);
            Test("a missing WebView2 runtime is a clear message", WebView2CheckIsFriendly);
            Test("first-run setup names exactly what is missing", SetupCheckNamesWhatIsMissing);

            Console.WriteLine();
            Console.WriteLine("Resume upload: Copy Resume Path and CurrentResume.docx");
            Test("Copy Resume Path copies the folder of the job's exact DOCX", CopyResumePathCopiesTheExactDocx);
            Test("no DOCX, a PDF, a folder or a URL copies nothing and says so", CopyResumePathRefusesAnythingButARealDocx);
            Test("CurrentResume.docx is created after a successful DOCX", CurrentResumeIsCreatedAfterGeneration);
            Test("the newest job replaces the alias; originals stay unchanged", CurrentResumeFollowsTheNewestJobAndLeavesOriginalsAlone);
            Test("an alias failure is logged, leaves the old alias, never fails the job", CurrentResumeFailureNeverFailsTheJob);
            Test("paths with spaces and special characters work as-is", UploadPathsSurviveSpacesAndSpecialCharacters);
            Test("the alias comes from the Documents folder, no user name in code", UploadAliasUsesTheDocumentsFolderNotAUserName);
            Test("tasks.json, settings.json and the real alias are untouched", UploadHelpersLeaveLiveDataAlone);

            Console.WriteLine();
            Console.WriteLine("Normal Prompt style contract (nested schema, 9 pt body)");
            Test("9 pt is accepted for body, bullet, skillValues and education", NinePointAcceptedInBodySections);
            Test("8.9 pt is an error strictly and clamped to 9 on capture", BelowNinePointIsCorrectedPerApi);
            Test("the 8-24 pt range, line spacing and weight rules are unchanged", GlobalFontAndSpacingRulesUnchanged);
            Test("flat style fields such as bodyFontSize stay unsupported", FlatStylePropertiesStayUnsupported);
            Test("9 pt survives normalize -> save -> ResumeDocument", NinePointSurvivesSaveAndReachesTheModel);
            Test("the DOCX renders summary, bullets, skills and education at 9 pt", NinePointReachesTheDocx);
            Test("the PDF model renders the same sizes; both documents generate", NinePointReachesThePdfModel);
            Test("the preset sizes Resume mode relies on are unchanged", PresetDefaultsUnchanged);
            Test("Normal Prompt mode refuses an answer without a style object", NormalModeRequiresStyle);
            Test("Resume mode still accepts an answer without style", ResumeModeStyleStaysOptional);
            Test("a prepared request records the mode it was sent in", PreparedRequestRecordsItsMode);
            Test("the Normal contract describes exactly the accepted schema", NormalContractDescribesTheRealSchema);

            Console.WriteLine();
            Console.WriteLine("Job import filter: rules");
            Test("a LinkedIn apply destination is refused; another ATS is not", FilterLinkedInPlatform);
            Test("LinkedIn in the text, a profile link or no link never rejects", FilterLinkedInIsAboutTheDestinationOnly);
            Test("clearance demanded of the applicant is refused", FilterSecurityClearanceRequirements);
            Test("security work, certifications and secure systems still import", FilterSecurityIsNotClearance);
            Test("explicit U.S. citizenship restrictions are refused", FilterCitizenshipRequirements);
            Test("work authorization and EEO wording are not citizenship rules", FilterWorkAuthorizationIsNotCitizenship);
            Test("one sentence matching two rules has a fixed precedence", FilterCitizenshipAndClearancePrecedence);
            Test("export-control / U.S.-person restrictions are refused when on", FilterExportControlRestrictions);
            Test("ordinary compliance and governance wording still imports", FilterExportControlLeavesOrdinaryComplianceAlone);
            Test("an explicit refusal of sponsorship is refused when on", FilterNoVisaSponsorship);
            Test("offered sponsorship and work authorization still import", FilterSponsorshipOfferedIsAccepted);
            Test("the five switches are one shared definition", FilterSwitchesAreOneSharedDefinition);
            Test("every filter off accepts everything", FilterAllOffAcceptsEverything);

            Console.WriteLine();
            Console.WriteLine("Job import filter: persistence");
            Test("the five defaults, and the Filters (N) count", FilterSettingsDefaults);
            Test("a settings.json written before the filters loads with the defaults", FilterSettingsOldFileLoadsWithDefaults);
            Test("all five survive save and reload", FilterSettingsSurviveSaveAndReload);
            Test("toggling one filter preserves every unrelated setting", FilterToggleKeepsUnrelatedSettings);

            Console.WriteLine();
            Console.WriteLine("Job import filter: the import gate");
            Test("a refused job creates no task and writes no tasks.json", FilterGateCreatesNoTask);
            Test("an accepted job still follows the existing import path", FilterGateStillImportsAcceptedJobs);
            Test("the filter runs after the duplicate decision; backfill is intact", FilterGateRunsAfterTheDuplicateDecision);
            Test("changing a switch affects the next ImportOne call", FilterSettingsChangeAffectsTheNextImport);
            Test("the Incoming folder inherits the same gate", FilterIncomingFolderSharesTheGate);
            Test("the Auto Import target counts accepted imports only", FilterAutoImportTargetCountsImportsOnly);
            Test("a rejected job is tried once per session and never blacklisted", FilterSessionNeverRetriesTheSameRejection);

            Console.WriteLine();
            Console.WriteLine("Application platform detection");
            Test("each known ATS address maps to its platform", PlatformMatching);
            Test("look-alike domains are not mistaken for an ATS", PlatformLookAlikes);
            Test("embedded Greenhouse / Ashby job links are recognised", PlatformEmbeddedLinks);
            Test("a missing or unusable ApplyUrl is Unknown; an unrecognised site is Unknown", PlatformMissingOrUnknown);
            Test("the twenty application platforms are detected and cached", AtsPlatformsIconsAndFilter);
            Test("an unreadable stored platform loads as Unknown and keeps every task", PlatformTolerantLoading);
            Test("the platform survives save and reload; older tasks load as Unknown", PlatformSaveReload);
            Test("capture and startup refresh derive the platform from ApplyUrl", PlatformDerivedFromApplyUrl);
            Test("fifty ATS platforms and custom-domain fingerprints", PlatformFingerprints);

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
            Test("queue and stack order waiting jobs only", QueueAndStackOrder);
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
            Console.WriteLine("SQLite job store");
            Test("migration imports existing jobs and applications", SqliteMigrationImportsJobs);
            Test("migration links a resume by internal job id, not company or title", SqliteMigrationLinksByJobId);
            Test("a repeated migration creates no duplicate rows", SqliteMigrationIsIdempotent);
            Test("a finished migration still stores a new job without removing older rows", SqliteAlreadyMigratedStillUpsertsNewJobs);
            Test("Jobright source and external id are unique together", SqliteJobrightIdentityIsUnique);
            Test("recording a generated resume creates then updates one output", SqliteResumeOutputUpserts);
            Test("loading applications does not scan resume folders", SqliteApplicationsLoadDoesNotScan);
            Test("SQLite is the only job store", SqliteIsTheOnlyJobStore);
            Test("a missing DOCX is kept and does not start a folder search", SqliteMissingDocxDoesNotScan);
            Test("Apply is enabled only for an application URL while the job can still be applied", ApplyButtonUsesOnlyTheApplicationUrl);
            Test("Apply stays enabled for every status when a job or application URL exists", ApplyEnabledForEveryStatus);
            Test("Apply opens a stored application URL and captures a missing one from Jobright", ApplyCaptureFlow);
            Test("an empty ApplyUrl is filled from SQLite and a known resume path is stored", SqliteRepairFillsEmptyApplyUrlAndResumePath);
            Test("an empty incoming value does not wipe a stored job field", SqliteUpsertKeepsNonEmptyFields);

            Console.WriteLine();
            Console.WriteLine("Profiles");
            Test("a profile gets its own id and database", ProfileCreateIsUnique);
            Test("two profiles do not share a database", ProfileDatabasesAreIsolated);
            Test("the single-user database is copied and the original is kept", ProfileMigrationKeepsTheOriginal);
            Test("the same profile cannot be locked twice", ProfileLockIsOneWorkspace);
            Test("different profiles can be open together", ProfileLocksAreIndependent);
            Test("rename keeps the profile id and its files", ProfileRenameKeepsData);
            Test("the profile list reloads for the chooser", ProfileChooserReloads);
            Test("a profile avatar stays in that profile folder", ProfileAvatarIsPerProfile);

            Console.WriteLine();
            Console.WriteLine("HTML round trip");
            Test("a constructed resume keeps its text through HTML", HtmlRoundTripKeepsConstructedText);
            Test("the same DOCX becomes the same HTML twice", HtmlRoundTripIsDeterministic);
            Test("the sample resumes keep their text through HTML", HtmlRoundTripSamples);
            Test("HTML tailoring keeps Billy Lin's locked facts and writes a new resume", HtmlTailorBilly);
            Test("Word PDF conversion closes its process", WordPdfConversionClosesWord);
            Test("email tasks tailor through the HTML pipeline", EmailHtmlTailoring);
            Test("HTML tailoring accepts a returned resume with fewer bullets", HtmlTailorRejectedResume);
            Test("HTML tailoring uses the Original Resume only", HtmlOriginalResumeIsTheOnlySource);
            Test("HTML tailoring regenerates source HTML when the Original Resume changes", HtmlOriginalResumeRegenerates);
            Test("HTML tailoring restores style and allows section edits", HtmlTailorLocksStyleAndSectionOrder);
            Test("HTML line height is Word multiple spacing", HtmlLineHeightUsesMultipleSpacing);

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
        Equal(9.0, style.Body.FontSize, "8.5 pt body text was raised to the 9 pt floor");
        Equal(24.0, style.Name.FontSize, "40 pt name was lowered to the maximum");
        Equal(9.0, style.SkillValues.FontSize, "8 pt skill values were raised to the 9 pt floor");
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
        Check(errors.Contains("style.body.fontSize must be >= 9 pt"), "expected the body font size error, got: " + Join(errors));
        Check(errors.Contains("style.name.fontSize must be <= 24 pt"), "expected the name font size error, got: " + Join(errors));
        Check(errors.Contains("style.skillValues.fontSize must be >= 9 pt"), "expected the skill values error, got: " + Join(errors));
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

    static void TailoredDocxDropsStaleSourceText() {
        var path = Path.Combine(NewDir("stale-docx"), "source.docx");
        using (var word = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document)) {
            var main = word.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body());
            var body = main.Document.Body!;
            void Para(string text, string? style = null) {
                var paragraph = new W.Paragraph();
                if (style is not null)
                    paragraph.ParagraphProperties = new W.ParagraphProperties(new W.ParagraphStyleId { Val = style });
                paragraph.Append(new W.Run(new W.Text(text) { Space = SpaceProcessingModeValues.Preserve }));
                body.Append(paragraph);
            }
            Para("Old Name");
            Para("PROFESSIONAL SUMMARY");
            Para("Old summary that must disappear");
            Para("TECHNICAL SKILLS");
            Para("Old Category", "Heading2");
            Para("old skills OLD_SKILLS_TEXT_456");
            Para("second old skill line");
            Para("PROFESSIONAL EXPERIENCE");
            Para("Old Co | Old Title | 2010 - 2011", "Heading2");
            Para("Old City | Contract | On-site");
            Para("OLD_PROJECT_TEXT_123");
            Para("Kubernetes only technology line");
            Para("• Old bullet one");
            Para("• Old bullet two");
            Para("CERTIFICATIONS");
            Para("Old Certificate");
            Para("EDUCATION");
            Para("Old Degree - Old School | 2000 - 2004");
            main.Document.Save();
        }

        var resume = new ResumeDocument {
            Name = "New Name",
            Summary = "NEW_SUMMARY_TEXT",
            Skills = {
                new SkillBlock { Category = "Languages", Skills = "NEW_SKILL_VALUES" },
                new SkillBlock { Category = "Cloud", Skills = "NEW_CLOUD_SKILL" }
            },
            Experience = {
                new ExperienceBlock {
                    Company = "New Co",
                    Title = "New Role",
                    StartDate = "Jan 2024",
                    EndDate = "Present",
                    Location = "Austin, TX",
                    EmploymentType = "Full-time",
                    WorkArrangement = "Remote",
                    Lines = {
                        BulletLine.Plain("NEW_BULLET_ONE"),
                        BulletLine.Plain("NEW_BULLET_TWO"),
                        BulletLine.Plain("NEW_BULLET_THREE")
                    }
                }
            },
            Certifications = { "NEW_CERT_NAME" },
            Education = {
                new EducationBlock { Degree = "NEW_DEGREE_NAME", School = "NEW_SCHOOL_NAME", Dates = "2018 - 2022" }
            }
        };
        TemplateDocxWriter.ReplaceText(path, resume);
        using var saved = WordprocessingDocument.Open(path, false);
        var text = string.Concat(saved.MainDocumentPart!.Document!.Body!.Descendants<W.Text>().Select(node => node.Text));
        foreach (var stale in new[] {
            "OLD_PROJECT_TEXT_123", "OLD_SKILLS_TEXT_456", "Kubernetes only",
            "Old bullet", "Old summary", "Old Certificate", "Old Degree", "Old Co", "second old skill"
        })
            Check(!text.Contains(stale, StringComparison.Ordinal), "stale text survived: " + stale);
        foreach (var expected in new[] {
            "NEW_SUMMARY_TEXT", "NEW_SKILL_VALUES", "NEW_CLOUD_SKILL",
            "New Co", "New Role", "Austin, TX", "Full-time", "Remote",
            "NEW_BULLET_ONE", "NEW_BULLET_TWO", "NEW_BULLET_THREE",
            "NEW_CERT_NAME", "NEW_DEGREE_NAME", "NEW_SCHOOL_NAME"
        })
            Check(text.Contains(expected, StringComparison.Ordinal), "missing tailored text: " + expected);
    }

    static void WriteStyleDocx(string path, string marker) {
        using var word = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = word.AddMainDocumentPart();
        main.Document = new W.Document(new W.Body());
        var body = main.Document.Body!;
        void Para(string text, string? style = null) {
            var paragraph = new W.Paragraph();
            if (style is not null)
                paragraph.ParagraphProperties = new W.ParagraphProperties(new W.ParagraphStyleId { Val = style });
            paragraph.Append(new W.Run(new W.Text(text) { Space = SpaceProcessingModeValues.Preserve }));
            body.Append(paragraph);
        }
        Para("Style Name");
        Para("PROFESSIONAL SUMMARY");
        Para(marker);
        Para("TECHNICAL SKILLS");
        Para("Old Category", "Heading2");
        Para("old skill line");
        Para("PROFESSIONAL EXPERIENCE");
        Para("Old Co | Old Title | 2010 - 2011", "Heading2");
        Para("Old City | Contract | On-site");
        Para("• Old bullet one");
        main.Document.Save();
    }

    static string DocxBody(string path) {
        using var saved = WordprocessingDocument.Open(path, false);
        return string.Concat(saved.MainDocumentPart!.Document!.Body!.Descendants<W.Text>().Select(node => node.Text));
    }

    static void RequestResumeBeatsOriginalStyle() {
        var dir = NewDir("style-priority");
        var master = Path.Combine(dir, "master.docx");
        var selected = Path.Combine(dir, "selected.docx");
        var request = Path.Combine(dir, "request.docx");
        WriteStyleDocx(master, "old master OLD_MASTER_MARKER_123");
        WriteStyleDocx(selected, "old style OLD_STYLE_MARKER_456");
        WriteStyleDocx(request, "old request OLD_REQUEST_MARKER_789");
        var chosen = ResumeGenerator.ResumeStyleSource.Resolve(request, selected);
        Equal("request", chosen!.Priority, "the request resume wins");
        Equal(Path.GetFullPath(request), chosen.Path, "the request file is the template");
        Check(ResumeGenerator.ResumeStyleSource.Resolve(Path.Combine(dir, "missing.docx"), selected)!.Priority == "selected",
            "a missing request file falls through to the selected reference");

        var settings = new AppSettings {
            ResumeRootFolder = Path.Combine(dir, "out"),
            Docx = true,
            Pdf = false,
            OriginalResume = master,
            StyleReferenceResume = selected
        };
        var result = ResumeGenerator.Generate("Caterpillar Inc.", "Senior AI Software Engineer",
            Fixture("resume-basic.json"), settings, null, "STYLE-REQ", null, null, request);
        Check(result.DocxGenerated, result.DocxError ?? result.FatalError ?? "docx");
        var text = DocxBody(result.DocxPath!);
        Check(text.Contains("twelve years", StringComparison.Ordinal), "tailored summary is written");
        Check(!text.Contains("OLD_REQUEST_MARKER_789", StringComparison.Ordinal), "request template text is not kept");
        Check(!text.Contains("OLD_STYLE_MARKER_456", StringComparison.Ordinal), "selected template text is not kept");
        Check(!text.Contains("OLD_MASTER_MARKER_123", StringComparison.Ordinal), "the original resume is not the style source");
    }

    static void SelectedStyleReferenceIsUsed() {
        var dir = NewDir("style-selected");
        var master = Path.Combine(dir, "master.docx");
        var selected = Path.Combine(dir, "selected.docx");
        WriteStyleDocx(master, "old master OLD_MASTER_MARKER_123");
        WriteStyleDocx(selected, "old style OLD_STYLE_MARKER_456");
        var chosen = ResumeGenerator.ResumeStyleSource.Resolve(null, selected);
        Equal("selected", chosen!.Priority, "the last selected reference is used");
        var settings = new AppSettings {
            ResumeRootFolder = Path.Combine(dir, "out"),
            Docx = true,
            Pdf = false,
            OriginalResume = master,
            StyleReferenceResume = selected
        };
        var result = ResumeGenerator.Generate("Caterpillar Inc.", "Senior AI Software Engineer",
            Fixture("resume-basic.json"), settings, null, "STYLE-SEL");
        Check(result.DocxGenerated, result.DocxError ?? result.FatalError ?? "docx");
        var text = DocxBody(result.DocxPath!);
        Check(text.Contains("twelve years", StringComparison.Ordinal), "tailored summary is written");
        Check(!text.Contains("OLD_STYLE_MARKER_456", StringComparison.Ordinal), "selected template text is not kept");
        Check(!text.Contains("OLD_MASTER_MARKER_123", StringComparison.Ordinal), "the original resume is not the style source");
    }

    static void DefaultStyleIgnoresOriginalResume() {
        var dir = NewDir("style-default");
        var master = Path.Combine(dir, "master.docx");
        WriteStyleDocx(master, "old master OLD_MASTER_MARKER_123");
        Check(ResumeGenerator.ResumeStyleSource.Resolve(null, null) is null, "no reference means the app default");
        Check(ResumeGenerator.ResumeStyleSource.Resolve("", " ") is null, "blank paths are not a reference");
        var settings = new AppSettings {
            ResumeRootFolder = Path.Combine(dir, "out"),
            Docx = true,
            Pdf = false,
            OriginalResume = master
        };
        var result = ResumeGenerator.Generate("Caterpillar Inc.", "Senior AI Software Engineer",
            Fixture("resume-basic.json"), settings, null, "STYLE-DEF");
        Check(result.DocxGenerated, result.DocxError ?? result.FatalError ?? "docx");
        var text = DocxBody(result.DocxPath!);
        Check(text.Contains("twelve years", StringComparison.Ordinal), "default style still writes the tailored summary");
        Check(!text.Contains("OLD_MASTER_MARKER_123", StringComparison.Ordinal), "the original resume is not copied");
    }

    static void WithAdaptationRoot(Action body) {
        var previous = PromptConversion.Root;
        var allow = PromptConversion.AllowUnadaptedSource;
        var followedProfile = previous == Path.Combine(ProfileContext.ProfileRoot, "PromptAdaptation");
        PromptConversion.Root = NewDir("prompt-adaptation");
        try { body(); }
        finally {
            PromptConversion.Root = followedProfile ? "" : previous;
            PromptConversion.AllowUnadaptedSource = allow;
        }
    }

    static string AdaptedSample() =>
        "Preserve the user's tailoring strategy and tone.\r\n"
        + PromptConversion.HtmlContract;

    static void PromptConversionReusesUnchangedSource() => WithAdaptationRoot(() => {
        var source = Path.Combine(NewDir("prompt-src"), "mine.txt");
        File.WriteAllText(source, "Tailor every bullet to the job description. Do not invent employers.");
        var before = File.ReadAllBytes(source);
        Check(PromptConversion.TrySave(source, AdaptedSample(), out var error), error);
        Check(before.AsSpan().SequenceEqual(File.ReadAllBytes(source)), "the user's prompt file must stay unchanged");
        Check(PromptConversion.Matches(source), "the same file and hash is ready");
        Equal(File.ReadAllText(PromptConversion.ConvertedPath), PromptConversion.RequireText(source), "jobs read the converted copy");
        var saved = PromptConversion.Load();
        Equal(PromptConversion.OutputModeHtml, saved?.OutputMode ?? "", "the adaptation is HTML");
        Equal(PromptConversion.HtmlContractVersion.ToString(), (saved?.HtmlContractVersion ?? 0).ToString(), "the HTML contract version is current");
        var instruction = PromptConversion.BuildInstruction(File.ReadAllText(source), resumeMode: true);
        Check(instruction.Contains(PromptConversion.HtmlContract, StringComparison.Ordinal), "the HTML contract is the required output");
        Check(instruction.Contains("top-level section order", StringComparison.Ordinal), "section order is locked");
        Check(instruction.Contains("<style> block exactly", StringComparison.Ordinal), "the style block is locked");
        Check(instruction.Contains("Do not invent employers", StringComparison.Ordinal), "the user's strategy is included");
        Check(!instruction.Contains("descriptionLines", StringComparison.Ordinal), "the JSON schema is not the HTML contract");
    });

    static void PromptConversionDetectsSourceChange() => WithAdaptationRoot(() => {
        var source = Path.Combine(NewDir("prompt-src"), "mine.txt");
        File.WriteAllText(source, "First version of the tailoring rules.");
        Check(PromptConversion.TrySave(source, AdaptedSample(), out _), "first conversion");
        Check(PromptConversion.Matches(source), "unchanged source matches");
        File.AppendAllText(source, " Added a new rule.");
        Check(!PromptConversion.Matches(source), "a changed file is not ready");
        Check(File.Exists(PromptConversion.ConvertedPath), "the previous converted file stays on disk");
    });

    static void PromptConversionKeepsPreviousOnFailure() => WithAdaptationRoot(() => {
        var source = Path.Combine(NewDir("prompt-src"), "mine.txt");
        File.WriteAllText(source, "Tailor to the posting.");
        Check(PromptConversion.TrySave(source, AdaptedSample(), out _), "first conversion");
        var kept = File.ReadAllBytes(PromptConversion.ConvertedPath);
        var profile = """{"info":{"name":"A"},"summary":"S","skills":[],"experience":[]}""";
        Check(!PromptConversion.TrySave(source, profile, out var error), "a profile JSON is not a prompt");
        Check(error.Contains("previous prepared prompt", StringComparison.Ordinal), error);
        Check(kept.AsSpan().SequenceEqual(File.ReadAllBytes(PromptConversion.ConvertedPath)), "the previous converted prompt was kept");
        Check(!PromptConversion.TrySave(source, "too short", out _), "a short reply is refused");
        Check(kept.AsSpan().SequenceEqual(File.ReadAllBytes(PromptConversion.ConvertedPath)), "a short reply does not replace the file");
    });

    static void UnadaptedPromptIsRefused() => WithAdaptationRoot(() => {
        var source = Path.Combine(NewDir("prompt-src"), "mine.txt");
        File.WriteAllText(source, "A prompt that has not been prepared.");
        PromptConversion.AllowUnadaptedSource = false;
        try {
            PromptConversion.RequireText(source);
            Check(false, "an unconverted prompt must be refused");
        } catch (InvalidOperationException ex) {
            Check(ex.Message.Contains("Prompt ready", StringComparison.Ordinal), ex.Message);
        }
        PromptConversion.AllowUnadaptedSource = true;
        Equal(File.ReadAllText(source), PromptConversion.RequireText(source), "the test harness may still read the source file");
    });

    static void PromptConversionRejectsJsonContract() => WithAdaptationRoot(() => {
        var source = Path.Combine(NewDir("prompt-src"), "mine.txt");
        File.WriteAllText(source, "Tailor every bullet to the job description.");
        Directory.CreateDirectory(PromptConversion.Root);
        File.WriteAllText(PromptConversion.ConvertedPath, AdaptedSample());
        var record = new PromptConversion.Record {
            OriginalPromptPath = Path.GetFullPath(source),
            OriginalPromptHash = PromptConversion.HashFile(source),
            ConvertedPromptPath = Path.GetFullPath(PromptConversion.ConvertedPath),
            ConvertedPromptHash = PromptConversion.HashFile(PromptConversion.ConvertedPath),
            ConvertedAt = DateTimeOffset.Now.ToString("o"),
            OutputMode = "JSON",
            HtmlContractVersion = 0
        };
        File.WriteAllText(PromptConversion.MetadataPath, System.Text.Json.JsonSerializer.Serialize(record));
        Check(!PromptConversion.Matches(source), "a JSON adaptation is rebuilt even when the prompt file is unchanged");
        record.OutputMode = "";
        record.HtmlContractVersion = 0;
        File.WriteAllText(PromptConversion.MetadataPath, System.Text.Json.JsonSerializer.Serialize(record));
        Check(!PromptConversion.Matches(source), "a prompt adapted before the HTML contract is not ready");
    });

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

        // One schema. Normal mode differs in exactly three places: the opening sentence, the verification
        // rule (which adds style), and the STYLE CONTRACT appended at the end. Everything else is shared.
        var resumeVerify = resumeContract.Split("\r\n").Single(l => l.StartsWith("Before responding, verify the top-level", StringComparison.Ordinal));
        var expectedNormal = resumeContract.Replace(PromptContract.ResumeOpening, PromptContract.NormalOpening)
                                           .Replace(resumeVerify, PromptContract.NormalVerify)
                             + "\r\n\r\n" + PromptContract.StyleContract();
        Equal(expectedNormal, normalContract, "Normal = Resume + opening, verify rule and style contract");
        Check(!resumeContract.Contains("STYLE CONTRACT", StringComparison.Ordinal), "Resume mode carries no style contract");
        Check(normalContract.Contains("===== STYLE CONTRACT (REQUIRED) =====", StringComparison.Ordinal), "Normal mode carries it");
        Check(resumeContract.Contains(PromptContract.ResumeOpening, StringComparison.Ordinal), "Resume names the Master Prompt");
        Check(normalContract.Contains(PromptContract.NormalOpening, StringComparison.Ordinal), "Normal names the user's instructions");

        foreach (var rule in new[] { "Return ONLY the updated profile object in a Markdown code block fenced with json.",
                                     "info, summary, skills, experience, certifications, education, and style",
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
        // The documents are named after the candidate (profile info.name = "BILLY LIN").
        Equal(Path.Combine(expectedFolder, "BILLY LIN.docx"), result.DocxPath, "DOCX path");
        Equal(Path.Combine(expectedFolder, "BILLY LIN.pdf"), result.PdfPath, "PDF path");
        Equal(Path.Combine(expectedFolder, "resume-info.json"), result.MetadataPath, "metadata path");

        Check(File.Exists(result.DocxPath!), "BILLY LIN.docx should exist");
        Check(File.Exists(result.PdfPath!), "BILLY LIN.pdf should exist");
        Check(File.Exists(result.MetadataPath!), "resume-info.json should exist");
        Equal(0, Directory.GetFiles(root).Length, "nothing may be written loose in the Resume Root");

        // Spaces kept, Windows-invalid characters sanitized, a blank name falls back to "Resume".
        Equal("Billy Lin", ResumeOutputManager.DocumentBaseName("Billy Lin"), "spaces are kept");
        Equal("Jane O Doe", ResumeOutputManager.DocumentBaseName("Jane: O/Doe?"), "invalid characters sanitized");
        foreach (var blank in new string?[] { null, "", "   ", "***" })
            Equal("Resume", ResumeOutputManager.DocumentBaseName(blank), $"blank name '{blank ?? "null"}' falls back");

        // Open Resume's relink finds the candidate-named DOCX through resume-info.json, not "Resume*.docx".
        var job = new JobTask {
            JobId = "STRESS-114209-001",
            Company = "Caterpillar Inc.",
            Title = "Senior AI Software Engineer"
        };
        Equal(result.DocxPath, JobTracker.FindExistingResume(job, root), "the generated DOCX is found by its real name");
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
        Equal("BILLY LIN.docx", Path.GetFileName(first.DocxPath!), "first run");
        Equal("BILLY LIN (2).docx", Path.GetFileName(second.DocxPath!), "second run");
        Equal("BILLY LIN (3).docx", Path.GetFileName(third.DocxPath!), "third run");

        // The whole set moves together, so a pair is never split across revisions.
        Equal("BILLY LIN (2).pdf", Path.GetFileName(second.PdfPath!), "second run PDF");
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
        Equal("BILLY LIN.docx", metadata["docxFile"]!.GetValue<string>(), "docxFile");
        Equal("BILLY LIN.pdf", metadata["pdfFile"]!.GetValue<string>(), "pdfFile");

        var generatedAt = metadata["generatedAt"]!.GetValue<string>();
        Check(DateTime.TryParse(generatedAt, out _), "generatedAt should be a timestamp: " + generatedAt);
        Equal(19, generatedAt.Length, "generatedAt format yyyy-MM-ddTHH:mm:ss");

        // A disabled document type is recorded as null rather than a filename that does not exist.
        var docxOnly = new AppSettings { ResumeRootFolder = NewDir("metadata-docx"), Docx = true, Pdf = false };
        var second = ResumeGenerator.Generate("Tesla", "ML Engineer", Fixture("resume-basic.json"), docxOnly, null, "JOB-2", "");
        var record = JsonNode.Parse(File.ReadAllText(second.MetadataPath!))!.AsObject();
        Equal("BILLY LIN.docx", record["docxFile"]!.GetValue<string>(), "docxFile");
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
        Equal(9.0, written["body"]!["fontSize"]!.GetValue<double>(), "the written style carries the clamped value");
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
        Equal(9.0, style["body"]!["fontSize"]!.GetValue<double>(), "the body size was clamped");
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

        string MakeResume(string date, string folder, string file, string jobId) {
            var dir = Path.Combine(root, date, folder);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, file);
            File.WriteAllText(path, "not really a docx");
            var info = Path.Combine(dir, "resume-info.json");
            if (!File.Exists(info))
                File.WriteAllText(info, $$"""{ "jobId": "{{jobId}}" }""");
            return path;
        }

        // On disk, but no path on the task. The link is the internal id in resume-info.json.
        var expected = MakeResume(today, "Caterpillar Inc - Senior AI Software Engineer", "Resume.docx", "R-1");
        MakeResume(yesterday, "Caterpillar Inc - Senior AI Software Engineer", "Resume.docx", "R-1");   // older, must lose

        var job = Job("R-1", "Caterpillar Inc.", "Senior AI Software Engineer");
        Check(!job.ResumeGenerated, "the job starts with no resume path");
        Equal(expected, JobTracker.FindExistingResume(job, root), "the newest dated folder wins");

        // The newest revision inside that folder wins too.
        var second = MakeResume(today, "Caterpillar Inc - Senior AI Software Engineer", "Resume (2).docx", "R-1");
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

    static void StatusSurvivesRestart() => WithLiveTasksFile(() => {
        // Storage writes the live tasks.json; the helper restores it (and its .bak) afterwards.
        {
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
        }
    });

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
        Equal(7, ApplicationStatus.Filters.Length, "the filter list is All plus the six statuses");
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

        // JSON-LD with no job id is not proof it belongs to the open card.
        var unscoped = JobrightPageExtractor.Parse(ScriptResultWith(p =>
            p["ld"] = new JsonArray((JsonNode?)"{\"@type\":\"JobPosting\",\"title\":\"Other role\",\"hiringOrganization\":{\"name\":\"Other Co\"},\"description\":\"OTHER JOB TEXT\"}")));
        Equal("ClearlyRated", unscoped.Company, "page data keeps the company");
        Equal("Backend Software Engineer", unscoped.Title, "page data keeps the title");
        Check(!unscoped.Description.Contains("OTHER JOB TEXT"), "an unidentified posting does not replace this job's description");

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
    static void WithLiveTasksFile(Action body) => WithLiveStateFile(Storage.TasksPath, body);

    static void ApplyUrlOldTasksLoad() => WithLiveTasksFile(() => UsingStore(() => {
        // An old tasks.json is left on disk and is not the job store. The same fields live in SQLite.
        Directory.CreateDirectory(Storage.DataDir);
        File.WriteAllText(Storage.TasksPath, $$"""
        [
          { "JobId": "RB-OLD-1", "Source": "jobright-browser", "Company": "Oracle", "Title": "ML Engineer",
            "Jd": "...", "Link": "{{ApplyJobPage}}", "CompanyUrl": "https://www.oracle.com/",
            "Status": "Completed", "ApplicationStatus": "Ready" },
          { "JobId": "RB-OLD-2", "Company": "Stripe", "Title": "Backend Engineer", "Jd": "...", "Status": "Queued" }
        ]
        """);
        var before = File.ReadAllBytes(Storage.TasksPath);
        var older = new JobTask {
            JobId = "RB-OLD-1", Source = JobImporter.BrowserSource, Company = "Oracle", Title = "ML Engineer",
            Jd = "...", Link = ApplyJobPage, CompanyUrl = "https://www.oracle.com/",
            Status = "Completed", ApplicationStatus = ApplicationStatus.Ready
        };
        Storage.SaveTasks(new[] { older, new JobTask { JobId = "RB-OLD-2", Company = "Stripe", Title = "Backend Engineer", Jd = "...", Status = "Queued" } });
        Check(before.AsSpan().SequenceEqual(File.ReadAllBytes(Storage.TasksPath)), "tasks.json is left untouched");

        var tasks = Storage.LoadTasks().OrderBy(task => task.JobId, StringComparer.Ordinal).ToList();
        Equal(2, tasks.Count, "every stored job loads from SQLite");
        foreach (var task in tasks) {
            Equal("", task.ApplyUrl, task.JobId + " has no application link");
            Check(task.ApplyUrlCapturedAt is null, task.JobId + " has no capture time");
        }
        Equal("Completed", tasks[0].Status, "queue status untouched");
        Equal(ApplicationStatus.Ready, tasks[0].ApplicationStatus, "application status untouched");
        Equal(ApplyJobPage, tasks[0].Link, "job link untouched");
    }));

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
        Equal("GH-1,WD-1,WD-2", Ids(JobTracker.ApplyFilters(tasks, null, null, null, platforms: JobTracker.PlatformFilterOrder.ToList())),
              "every ATS choice ticked still leaves out job boards and Unknown");
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
        Equal("Greenhouse,Workday,Lever,Ashby,SmartRecruiters,ICims,Taleo,BambooHr,Jobvite,SuccessFactors,AdpRecruiting,OracleRecruitingCloud,UkgPro,JazzHr,Recruitee,BreezyHr,Pinpoint,Teamtailor,Workable,RipplingRecruiting,DayforceRecruiting,CornerstoneRecruiting,Avature,Phenom,Eightfold,Beamery,Bullhorn,JobAdder,ZohoRecruit,Cats,ApplicantStack,ClearCompany,PaylocityRecruiting,PaycomRecruiting,PaycorRecruiting,IsolvedTalent,Fountain,Paradox,Comeet,Manatal,RecruitCrm,Recruiterflow,JobScore,Homerun,PersonioRecruiting,TeamEngine,TrakstarHire,Neogov,GovernmentJobs,SymplrRecruiting",
              string.Join(",", JobTracker.PlatformFilterOrder), "the explicit choice order");
        Check(!JobTracker.PlatformFilterOrder.Contains(ApplicationPlatform.Jobright)
              && !JobTracker.PlatformFilterOrder.Contains(ApplicationPlatform.LinkedIn)
              && !JobTracker.PlatformFilterOrder.Contains(ApplicationPlatform.Indeed)
              && !JobTracker.PlatformFilterOrder.Contains(ApplicationPlatform.Wellfound)
              && !JobTracker.PlatformFilterOrder.Contains(ApplicationPlatform.Dice)
              && !JobTracker.PlatformFilterOrder.Contains(ApplicationPlatform.Unknown)
              && !JobTracker.PlatformFilterOrder.Contains(ApplicationPlatform.Other),
              "job boards are not platform-filter choices");
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
              JobTracker.PlatformFilterLabel(new[] { ApplicationPlatform.Lever, ApplicationPlatform.Ashby, ApplicationPlatform.Taleo }),
              "three or more");
        Equal("49 platforms", JobTracker.PlatformFilterLabel(JobTracker.PlatformFilterOrder.Skip(1).ToList()), "all but one");

        // The badge and the filter share these display names.
        Equal("Other", JobTracker.PlatformDisplayName(ApplicationPlatform.Other), "Other badge text");
        Equal("SmartRecruiters", JobTracker.PlatformDisplayName(ApplicationPlatform.SmartRecruiters), "SmartRecruiters badge text");
        foreach (var platform in JobTracker.PlatformFilterOrder)
            Check(JobTracker.PlatformDisplayName(platform).Length > 0, platform + " has display text");
    }

    static void ImportCapturesCompanyAndPlatformIcons() {
        var job = Job("ICON-1", "Acme", "Engineer", "https://jobright.ai/jobs/info/aaaaaaaaaaaaaaaaaaaaaaaa");
        job.Source = JobImporter.BrowserSource;
        job.Jd = "Build things. Its website is https://www.acme.com/about.";
        job.ApplicationPlatform = ApplicationPlatform.Unknown;

        Equal("https://www.acme.com/about", IconCache.CompanyPageUrl(job), "the company site in the job text");
        Equal("jobright", string.Join(",", IconCache.PlatformsFor(job).Select(IconCache.PlatformKey)),
              "a Jobright posting captures the Jobright icon");
        Equal("Jobright", job.PlatformToolTip, "the row names the job site when there is no ATS");

        job.CompanyUrl = "https://careers.acme.com/";
        job.NotifyIconsChanged();
        // LogoUrl is resolved once. A company URL set before the first read wins over the job text.
        var withSite = Job("ICON-2", "Acme", "Engineer", "https://jobright.ai/jobs/info/bbbbbbbbbbbbbbbbbbbbbbbb");
        withSite.Jd = job.Jd;
        withSite.CompanyUrl = "https://careers.acme.com/";
        Equal("https://careers.acme.com/", IconCache.CompanyPageUrl(withSite), "the stored company URL wins");

        withSite.ApplyUrl = "https://boards.greenhouse.io/acme/jobs/1";
        withSite.ApplicationPlatform = ApplicationPlatform.Greenhouse;
        Equal("greenhouse,jobright", string.Join(",", IconCache.PlatformsFor(withSite).Select(IconCache.PlatformKey)),
              "an ATS icon is captured as well as the job site");
        Equal("Greenhouse", withSite.PlatformToolTip, "the row names the ATS");
    }

    static void ApplicationsConsistencyFailedCount() {
        var viewed = Job("AC-V");
        var ready = Job("AC-R");
        JobTracker.UpdateStatus(ready, ApplicationStatus.Ready);
        var queueFailed = Job("AC-Q");
        queueFailed.Status = "Failed";
        var marked = Job("AC-M");
        JobTracker.UpdateStatus(marked, ApplicationStatus.Failed);
        var applied = Job("AC-P");
        JobTracker.UpdateStatus(applied, ApplicationStatus.Applied);
        var tasks = new[] { viewed, ready, queueFailed, marked, applied };

        Equal("Queued", marked.Status, "application Failed does not change the queue");
        Equal(ApplicationStatus.Viewed, queueFailed.ApplicationStatus, "a queue failure is not an application outcome");
        Equal(2, JobTracker.CountStatus(tasks, ApplicationStatus.Viewed), "Viewed, including the queue failure");
        Equal(1, JobTracker.CountStatus(tasks, ApplicationStatus.Ready), "Ready");
        Equal(1, JobTracker.CountStatus(tasks, ApplicationStatus.Applied), "Applied");
        Equal(0, JobTracker.CountStatus(tasks, ApplicationStatus.Interview), "Interview");
        Equal(1, JobTracker.CountStatus(tasks, ApplicationStatus.Failed), "Failed card is the explicit status only");
        Equal(0, JobTracker.CountStatus(tasks, ApplicationStatus.Done), "Done");
        Equal(tasks.Length, ApplicationStatus.Known.Sum(status => JobTracker.CountStatus(tasks, status)),
              "the six statuses cover every job");
        Equal(JobTracker.CountStatus(tasks, ApplicationStatus.Failed),
              JobTracker.GetTasksByStatus(tasks, ApplicationStatus.Failed).Count,
              "the Failed filter matches the Failed card");
        Equal("AC-M", JobTracker.GetTasksByStatus(tasks, ApplicationStatus.Failed)[0].JobId, "the same record");
    }

    static void ApplicationsConsistencyLogoUrl() {
        var fromField = new JobTask {
            CompanyUrl = "https://www.ford.com/cars",
            Jd = "Its website is https://www.gm.com."
        };
        Equal("https://www.ford.com/cars", fromField.LogoUrl, "the stored company URL");

        var fromJob = new JobTask {
            CompanyUrl = "",
            Jd = "Headquartered in Detroit. Its website is https://www.gm.com."
        };
        Equal("https://www.gm.com", fromJob.LogoUrl, "the website stated in the job");
        Equal("https://www.gm.com", fromJob.LogoUrl, "resolved once");

        var noise = new JobTask { CompanyUrl = " ", Jd = "Apply at https://boards.greenhouse.io/acme/jobs/1" };
        Check(noise.LogoUrl is null, "a random link is not a company logo");
        Equal("ford.com", IconCache.CompanyDomain(fromField.LogoUrl), "www is not part of the cache key");
        Equal("gm.com", IconCache.CompanyDomain(fromJob.LogoUrl), "cache key");
    }

    static void ApplicationsConsistencyPlatformList() {
        var tasks = new List<JobTask> {
            PlatformJob("GH", ApplicationPlatform.Greenhouse),
            PlatformJob("WD", ApplicationPlatform.Workday),
            PlatformJob("JR", ApplicationPlatform.Jobright),
            PlatformJob("UN", ApplicationPlatform.Unknown),
            PlatformJob("WD2", ApplicationPlatform.Workday)
        };
        Equal("Greenhouse,Workday", string.Join(",", JobTracker.PlatformsPresent(tasks)),
              "only ATS values, Jobright omitted even when stored");
        Equal("", string.Join(",", JobTracker.PlatformsPresent(new[] { tasks[2], tasks[3] })),
              "a job-site value and Unknown produce no platform choice");
        Equal("All platforms", JobTracker.PlatformFilterLabel(null), "All platforms is the empty selection");

        const string posting = "https://jobright.ai/jobs/info/aaaaaaaaaaaaaaaaaaaaaaaa";
        Equal(ApplicationPlatform.Unknown, ApplicationPlatformDetector.Resolve(null, posting),
              "a Jobright posting is not an ATS");
        Equal(ApplicationPlatform.Unknown, ApplicationPlatformDetector.Resolve(posting, null),
              "jobright.ai is not an application platform");
        Equal(ApplicationPlatform.Greenhouse,
              ApplicationPlatformDetector.Resolve("https://boards.greenhouse.io/acme/jobs/1", posting),
              "the application URL supplies the ATS");
        Equal(ApplicationPlatform.Workday,
              ApplicationPlatformDetector.Detect("https://www.workday.com/en-us/pages/job.html"),
              "workday.com");

        var site = new JobTask { Link = posting, Source = "jobright-browser" };
        Equal("Jobright", site.JobSite, "job site comes from the posting");
        Equal(ApplicationPlatform.Unknown, site.ApplicationPlatform, "platform stays empty");
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
        Equal(8, ApplicationStatus.Filter.Options.Length, "All + the group + six statuses");
        Equal(7, ApplicationStatus.Filters.Length, "the status filter includes Failed");
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

    static string ApplySources(string? helperApply, string? helperOriginal, string? nextApply, string? nextOriginal,
                                string? helperJobId = null, string? nextJobId = null) {
        const string pageId = "6aac7fec95c707f49dff195f";
        return ScriptResultWith(p => {
            var helper = p["next"]!.AsObject();
            helper["jobId"] = helperJobId ?? pageId;
            helper["applyLink"] = helperApply is null ? null : JsonValue.Create(helperApply);
            helper["originalUrl"] = helperOriginal is null ? null : JsonValue.Create(helperOriginal);
            p["applyFallback"] = new JsonObject {
                ["jobId"] = nextJobId ?? pageId,
                ["applyLink"] = nextApply is null ? null : JsonValue.Create(nextApply),
                ["originalUrl"] = nextOriginal is null ? null : JsonValue.Create(nextOriginal)
            };
        });
    }

    static void ExtractorApplyUrlFallsBackToNextData() {
        const string jobright = "https://jobright.ai/jobs/info/6aac7fec95c707f49dff195f";
        const string otherId = "aaaaaaaaaaaaaaaaaaaaaaaa";

        var helperLink = JobrightPageExtractor.Parse(ApplySources(
            "https://boards.greenhouse.io/acme/jobs/1", "https://jobs.lever.co/acme/9",
            "https://jobs.ashbyhq.com/acme/1", null));
        Equal("https://boards.greenhouse.io/acme/jobs/1", helperLink.ApplyUrl, "helper applyLink wins");
        Equal("ClearlyRated", helperLink.Company, "job text stays on the helper");

        Equal("https://jobs.lever.co/acme/9",
              JobrightPageExtractor.Parse(ApplySources(jobright, "https://jobs.lever.co/acme/9",
                  "https://boards.greenhouse.io/acme/jobs/2", null)).ApplyUrl,
              "helper originalUrl when applyLink is not external");

        var fromNext = JobrightPageExtractor.Parse(ApplySources(jobright, "", "https://jobs.ashbyhq.com/acme/3", null));
        Equal("https://jobs.ashbyhq.com/acme/3", fromNext.ApplyUrl, "__NEXT_DATA__ applyLink when the helper has none");
        Equal("ClearlyRated", fromNext.Company, "helper text is kept");

        Equal("https://apply.workable.com/acme/j/1",
              JobrightPageExtractor.Parse(ApplySources(null, jobright, jobright, "https://apply.workable.com/acme/j/1")).ApplyUrl,
              "__NEXT_DATA__ originalUrl when neither applyLink is external");

        Check(JobrightPageExtractor.Parse(ApplySources(jobright, jobright, jobright, jobright)).ApplyUrl is null,
              "jobright.ai from both sources is not an ApplyUrl");

        Check(JobrightPageExtractor.Parse(ApplySources("", "", "https://jobs.lever.co/acme/4", null, nextJobId: otherId)).ApplyUrl is null,
              "a stale __NEXT_DATA__ job id is not used");
        var staleHelper = JobrightPageExtractor.Parse(ApplySources(
            "https://jobs.lever.co/someone-else/1", null, null, null, helperJobId: otherId));
        Check(staleHelper.ApplyUrl is null, "a stale helper job id is not used");
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
                                            JobImporter.BrowserSource, tasks, NoFilters);
        Equal(JobImportKind.Imported, outcome.Kind, "imported");
        Check(outcome.ApplyUrlRecorded, "the outcome says a link was recorded");
        var task = tasks.Single();
        Equal("https://app.dover.com/apply/Cogniify/1e3fc78f", task.ApplyUrl, "saved, normalized (tracking dropped)");
        Check(task.ApplyUrlCapturedAt is DateTime at && at >= before, "capture time stamped");
        Equal(ApplicationPlatform.Unknown, task.ApplicationPlatform, "an unrecognised host is not an ATS");
        Equal("https://jobright.ai/jobs/info/6aada17fde327d3e210d3913", task.Link, "Link keeps the Jobright posting");

        // Recognised platforms are detected at import too.
        var gh = JobImporter.ImportOne(ImportData("https://jobright.ai/jobs/info/6aac7fec95c707f49dff195f",
                                                  "https://boards.greenhouse.io/acme/jobs/7"), JobImporter.BrowserSource, tasks, NoFilters);
        Equal(ApplicationPlatform.Greenhouse, tasks.Single(t => t.JobId == gh.JobId).ApplicationPlatform, "Greenhouse detected at import");

        // And it is all in the saved file.
        var saved = Storage.LoadTasks().Single(t => t.JobId == outcome.JobId);
        Equal(task.ApplyUrl, saved.ApplyUrl, "ApplyUrl saved");
        Equal(ApplicationPlatform.Unknown, saved.ApplicationPlatform, "platform saved");
        Check(saved.ApplyUrlCapturedAt is not null, "time saved");
    });

    static void ImportWithoutApplyUrl() => WithLiveTasksFile(() => {
        var tasks = new List<JobTask>();
        foreach (var (i, link) in new[] { null, "", "javascript:alert(1)", "https://jobright.ai/jobs/info/abc", "not a url" }.Select((l, i) => (i, l))) {
            var outcome = JobImporter.ImportOne(ImportData($"https://example.com/job/{i}", link), JobImporter.IncomingSource, tasks, NoFilters);
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
        Equal(JobImportKind.Imported, JobImporter.ImportOne(old, JobImporter.IncomingSource, tasks, NoFilters).Kind, "older input imports");
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
        var outcome = JobImporter.ImportOne(ImportData(page, "https://boards.greenhouse.io/acme/jobs/7"), JobImporter.BrowserSource, tasks, NoFilters);
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
        outcome = JobImporter.ImportOne(ImportData(empty.Link, "https://jobs.lever.co/acme/1"), JobImporter.BrowserSource, tasks, NoFilters);
        Equal(JobImportKind.Duplicate, outcome.Kind, "duplicate");
        Check(outcome.ApplyUrlRecorded, "the empty link was filled");
        Equal("https://jobs.lever.co/acme/1", empty.ApplyUrl, "filled");
        Equal(ApplicationPlatform.Lever, empty.ApplicationPlatform, "platform detected");
        Equal(ApplicationStatus.Applied, empty.ApplicationStatus, "application status untouched");
        Equal("https://jobs.lever.co/acme/1", Storage.LoadTasks().Single().ApplyUrl, "and saved");

        // A duplicate with no usable link changes and saves nothing.
        File.Delete(Storage.TasksPath);
        var none = Job("RB-NONE", "Acme", "Engineer", "https://jobright.ai/jobs/info/bbbbbbbbbbbbbbbbbbbbbbbb");
        JobImporter.ImportOne(ImportData(none.Link, "javascript:alert(1)"), JobImporter.BrowserSource, new List<JobTask> { none }, NoFilters);
        Equal("", none.ApplyUrl, "still empty");
        Check(!File.Exists(Storage.TasksPath), "a duplicate with nothing new is not saved");
    });

    // ---------- application platform detection ----------

    static void AtsPlatformsIconsAndFilter() {
        (ApplicationPlatform Platform, string Url)[] mapped = {
            (ApplicationPlatform.Greenhouse, "https://boards.greenhouse.io/acme/jobs/1"),
            (ApplicationPlatform.Workday, "https://acme.wd1.myworkdayjobs.com/job/1"),
            (ApplicationPlatform.Lever, "https://jobs.lever.co/acme/1"),
            (ApplicationPlatform.Ashby, "https://jobs.ashbyhq.com/acme/1"),
            (ApplicationPlatform.SmartRecruiters, "https://jobs.smartrecruiters.com/Acme/1"),
            (ApplicationPlatform.ICims, "https://careers-acme.icims.com/jobs/1/job"),
            (ApplicationPlatform.Taleo, "https://acme.taleo.net/careersection/job/1"),
            (ApplicationPlatform.BambooHr, "https://acme.bamboohr.com/careers/1"),
            (ApplicationPlatform.Jobvite, "https://jobs.jobvite.com/acme/job/1"),
            (ApplicationPlatform.SuccessFactors, "https://acme.successfactors.com/career?job=1"),
            (ApplicationPlatform.AdpRecruiting, "https://workforcenow.adp.com/mascsr/default/mdf/recruitment/1"),
            (ApplicationPlatform.OracleRecruitingCloud, "https://fa-ex.fa.ocs.oraclecloud.com/hcmUI/CandidateExperience/en/sites/CX/job/1"),
            (ApplicationPlatform.UkgPro, "https://recruiting.ultipro.com/ACM/JobBoard/1"),
            (ApplicationPlatform.JazzHr, "https://acme.applytojob.com/apply/1"),
            (ApplicationPlatform.Recruitee, "https://acme.recruitee.com/o/1"),
            (ApplicationPlatform.BreezyHr, "https://acme.breezy.hr/p/1"),
            (ApplicationPlatform.Pinpoint, "https://app.pinpoint.com/acme/jobs/1"),
            (ApplicationPlatform.Teamtailor, "https://acme.teamtailor.com/jobs/1"),
            (ApplicationPlatform.Workable, "https://apply.workable.com/acme/j/1"),
            (ApplicationPlatform.RipplingRecruiting, "https://ats.rippling.com/acme/jobs/1"),
        };
        Equal(20, mapped.Length, "twenty platforms");
        foreach (var (platform, url) in mapped) {
            Equal(platform, ApplicationPlatformDetector.Detect(url), url);
            Check(ApplicationPlatformDetector.IsAts(platform), platform + " is an application platform");
            Equal("platform:" + IconCache.PlatformKey(platform), IconCache.PlatformCacheKey(platform), platform + " cache key");
        }
        Equal(ApplicationPlatform.AdpRecruiting, ApplicationPlatformDetector.Detect("https://recruiting.adp.com/jobs/1"), "ADP recruiting host");
        Equal(ApplicationPlatform.JazzHr, ApplicationPlatformDetector.Detect("https://acme.jazz.co/apply/1"), "JazzHR host");
        Equal(ApplicationPlatform.UkgPro, ApplicationPlatformDetector.Detect("https://acme.rec.pro.ukg.net/ACM/jobboard"), "UKG recruiting host");
        Equal(ApplicationPlatform.Workday, ApplicationPlatformDetector.Detect("https://acme.myworkdaysite.com/recruiting/job/1"), "Workday site");
        Equal(ApplicationPlatform.Workday, ApplicationPlatformDetector.Detect("https://www.workday.com/en-us/jobs/1"), "workday.com");
        Equal(ApplicationPlatform.Unknown, ApplicationPlatformDetector.Detect("https://www.adp.com/"), "ADP's marketing site is not recruiting");
        Equal(ApplicationPlatform.Unknown, ApplicationPlatformDetector.Detect("https://analytics.oraclecloud.com/analytics/1"), "an Oracle Cloud site that is not recruiting");
        Equal(ApplicationPlatform.Unknown, ApplicationPlatformDetector.Detect("https://app.rippling.com/dashboard"), "Rippling outside recruiting");
        Equal(ApplicationPlatform.Unknown, ApplicationPlatformDetector.Detect("https://example.com/jobs/1"), "unknown host");
        Check(!ApplicationPlatformDetector.IsAts(ApplicationPlatform.Jobright), "Jobright is not an application platform");
        Check(!ApplicationPlatformDetector.IsAts(ApplicationPlatform.LinkedIn), "LinkedIn is not an application platform");
        Check(!ApplicationPlatformDetector.IsAts(ApplicationPlatform.Indeed), "Indeed is not an application platform");
        Check(!ApplicationPlatformDetector.IsAts(ApplicationPlatform.Dice), "Dice is not an application platform");
        Check(!ApplicationPlatformDetector.IsAts(ApplicationPlatform.Wellfound), "Wellfound is not an application platform");
        Equal(ApplicationPlatform.Unknown, ApplicationPlatformDetector.Resolve("https://jobright.ai/jobs/info/aaaaaaaaaaaaaaaaaaaaaaaa", null),
              "a Jobright address is not stored as the platform");

        UsingStore(() => {
            var tasks = new List<JobTask>();
            var data = ImportData("https://jobright.ai/jobs/info/aaaaaaaaaaaaaaaaaaaaaaaa", "https://boards.greenhouse.io/acme/jobs/9");
            Equal(JobImportKind.Imported, JobImporter.ImportOne(data, JobImporter.BrowserSource, tasks, NoFilters).Kind, "imported");
            var saved = JobStore.GetJobs().Single();
            Equal(ApplicationPlatform.Greenhouse, saved.ApplicationPlatform, "import stores the platform");
            Equal("Jobright", saved.JobSite, "import stores the job site separately");
            Check(saved.ApplyUrl.Contains("greenhouse.io", StringComparison.Ordinal), "import stores the apply address");
            Equal(JobImportKind.Duplicate, JobImporter.ImportOne(data, JobImporter.BrowserSource, tasks, NoFilters).Kind, "duplicate");
            Equal(1, JobStore.GetJobs().Count, "one job row");
            Equal(ApplicationPlatform.Greenhouse, JobStore.GetJobs().Single().ApplicationPlatform, "a duplicate keeps the platform");
        });

        var jobs = new[] {
            PlatformJob("GH", ApplicationPlatform.Greenhouse),
            PlatformJob("GH2", ApplicationPlatform.Greenhouse),
            PlatformJob("JR", ApplicationPlatform.Jobright),
            PlatformJob("LI", ApplicationPlatform.LinkedIn),
            PlatformJob("UN", ApplicationPlatform.Unknown)
        };
        Equal("Greenhouse", string.Join(",", JobTracker.PlatformsPresent(jobs)), "the filter lists a real ATS once");
        Check(JobTracker.PlatformsPresent(jobs).All(ApplicationPlatformDetector.IsAts), "the filter is only application platforms");

        var row = new JobTask {
            Link = "https://jobright.ai/jobs/info/aaaaaaaaaaaaaaaaaaaaaaaa",
            Source = "jobright-browser",
            ApplyUrl = "https://boards.greenhouse.io/acme/jobs/9",
            ApplicationPlatform = ApplicationPlatform.Greenhouse
        };
        Equal("greenhouse,jobright", string.Join(",", IconCache.PlatformsFor(row).Select(IconCache.PlatformKey)),
              "the platform icon and the job-site icon are separate");
        Check(IconCache.PlatformCacheKey(ApplicationPlatform.Greenhouse) != IconCache.PlatformCacheKey(ApplicationPlatform.Jobright),
              "the two cache keys differ");

        var previousRoot = IconCache.RootOverride;
        var previousSite = IconCache.SiteForTest;
        var previousHandler = IconCache.HandlerForTest;
        IconCache.RootOverride = NewDir("platform-icons");
        IconCache.ResetPlatformDownloads();
        try {
            var platforms = Path.Combine(IconCache.Root, "Platforms");
            Directory.CreateDirectory(platforms);
            var cached = Path.Combine(platforms, "greenhouse.png");
            File.WriteAllBytes(cached, new byte[] { 9, 8, 7 });
            IconCache.FetchPlatformForTest(new JobTask { ApplicationPlatform = ApplicationPlatform.Greenhouse }).GetAwaiter().GetResult();
            Equal(0, IconCache.PlatformDownloadCount, "a cached platform icon is not downloaded again");
            Check(File.ReadAllBytes(cached).AsSpan().SequenceEqual(new byte[] { 9, 8, 7 }), "an existing icon is not overwritten");

            IconCache.HandlerForTest = new PlatformIconHandler();
            IconCache.SiteForTest = _ => "http://icons.test/";
            IconCache.FetchPlatformForTest(new JobTask { ApplicationPlatform = ApplicationPlatform.Taleo }).GetAwaiter().GetResult();
            var savedIcon = Directory.GetFiles(platforms, "platform-taleo.*");
            Equal(1, savedIcon.Length, "the missing platform icon was saved");
            Equal(1, IconCache.PlatformDownloadCount, "the missing icon was downloaded once");
            var bytes = File.ReadAllBytes(savedIcon[0]);
            IconCache.FetchPlatformForTest(new JobTask { ApplicationPlatform = ApplicationPlatform.Taleo }).GetAwaiter().GetResult();
            Equal(1, IconCache.PlatformDownloadCount, "the saved icon is reused");
            Check(File.ReadAllBytes(savedIcon[0]).AsSpan().SequenceEqual(bytes), "the saved icon was not rewritten");
        } finally {
            IconCache.RootOverride = previousRoot;
            IconCache.SiteForTest = previousSite;
            IconCache.HandlerForTest = previousHandler;
        }
    }

    sealed class PlatformIconHandler : System.Net.Http.HttpMessageHandler {
        static readonly byte[] Png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken) {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) {
                var image = new System.Net.Http.ByteArrayContent(Png);
                image.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = image });
            }
            var html = new System.Net.Http.StringContent("<html><head><link rel=\"icon\" href=\"/logo.png\"></head></html>");
            html.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html");
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = html });
        }
    }

    static void PlatformIs(ApplicationPlatform expected, string? url) =>
        Equal(expected, ApplicationPlatformDetector.Detect(url), url ?? "null");

    static void PlatformMatching() {
        PlatformIs(ApplicationPlatform.Greenhouse, "https://boards.greenhouse.io/company/jobs/12345");
        PlatformIs(ApplicationPlatform.Greenhouse, "https://job-boards.greenhouse.io/company/jobs/12345");
        PlatformIs(ApplicationPlatform.Greenhouse, "https://boards.eu.greenhouse.io/company/jobs/12345");
        PlatformIs(ApplicationPlatform.Workday, "https://company.wd5.myworkdayjobs.com/job/12345");
        PlatformIs(ApplicationPlatform.Workday, "https://COMPANY.WD1.MYWORKDAYJOBS.COM/en-US/External/job/X_R1");
        PlatformIs(ApplicationPlatform.Workday, "https://wd3.myworkdaysite.com/recruiting/company/External/job/1");
        PlatformIs(ApplicationPlatform.Workday, "https://company.wd1.workday.com/job/1");
        PlatformIs(ApplicationPlatform.Lever, "https://jobs.lever.co/company/12345");
        PlatformIs(ApplicationPlatform.Lever, "https://jobs.eu.lever.co/company/12345/apply");
        PlatformIs(ApplicationPlatform.LinkedIn, "https://www.linkedin.com/jobs/view/12345");
        PlatformIs(ApplicationPlatform.Ashby, "https://jobs.ashbyhq.com/company/12345");
        PlatformIs(ApplicationPlatform.SmartRecruiters, "https://jobs.smartrecruiters.com/Company/12345-engineer");
        PlatformIs(ApplicationPlatform.ICims, "https://careers-company.icims.com/jobs/12345/engineer/job");
        PlatformIs(ApplicationPlatform.Greenhouse, "http://boards.greenhouse.io/company/jobs/1");
    }

    static void PlatformLookAlikes() {
        PlatformIs(ApplicationPlatform.Unknown, "https://notgreenhouse.io/company/jobs/1");
        PlatformIs(ApplicationPlatform.Unknown, "https://greenhouse.io.evil.com/company/jobs/1");
        PlatformIs(ApplicationPlatform.Unknown, "https://mylever.co/jobs/1");
        PlatformIs(ApplicationPlatform.Unknown, "https://fakemyworkdayjobs.com/job/1");
        PlatformIs(ApplicationPlatform.Unknown, "https://example.com/boards.greenhouse.io/jobs/1");
        PlatformIs(ApplicationPlatform.Unknown, "https://example.com/apply?next=https://jobs.lever.co/x/1");
        // LinkedIn counts only for its job postings.
        PlatformIs(ApplicationPlatform.Other, "https://www.linkedin.com/company/1028");
        PlatformIs(ApplicationPlatform.Other, "https://www.linkedin.com/in/someone/");
    }

    static void PlatformEmbeddedLinks() {
        PlatformIs(ApplicationPlatform.Greenhouse, "https://careers.example.com/jobs?gh_jid=4012345");
        PlatformIs(ApplicationPlatform.Greenhouse, "https://www.example.com/open-roles/?utm_source=x&GH_JID=7");
        PlatformIs(ApplicationPlatform.Ashby, "https://example.com/careers?ashby_jid=5b1c-22");
        // A parameter that only contains the name is not the embed id.
        PlatformIs(ApplicationPlatform.Unknown, "https://example.com/careers?not_gh_jid=1");
        PlatformIs(ApplicationPlatform.Unknown, "https://example.com/careers#gh_jid=1");
    }

    static void PlatformMissingOrUnknown() {
        foreach (var missing in new[] { null, "", "   ", "not a url", "/jobs/1", "about:blank", "mailto:jobs@x.com", "ftp://jobs.lever.co/x" })
            PlatformIs(ApplicationPlatform.Unknown, missing);
        PlatformIs(ApplicationPlatform.Unknown, "https://careers.oracle.com/jobs/12345");
        PlatformIs(ApplicationPlatform.Unknown, "https://example.com/");
        Equal(ApplicationPlatform.Unknown, new JobTask().ApplicationPlatform, "a new task starts Unknown");
    }

    static void PlatformTolerantLoading() => WithLiveTasksFile(() => {
        Directory.CreateDirectory(Storage.DataDir);
        File.WriteAllText(Storage.TasksPath, """
        [
          { "JobId": "P-1", "ApplicationPlatform": "Greenhouse" },
          { "JobId": "P-2", "ApplicationPlatform": "greenhouse" },
          { "JobId": "P-3", "ApplicationPlatform": "NotARealPlatform" },
          { "JobId": "P-4", "ApplicationPlatform": null },
          { "JobId": "P-5", "ApplicationPlatform": 999 },
          { "JobId": "P-6", "ApplicationPlatform": 2 },
          { "JobId": "P-7", "ApplicationPlatform": "3" },
          { "JobId": "P-8", "ApplicationPlatform": { "name": "Lever" } },
          { "JobId": "P-9", "ApplicationPlatform": [ "Lever" ] },
          { "JobId": "P-10", "ApplicationPlatform": "" }
        ]
        """);

        var path = Path.Combine(NewDir("platform-json"), "tasks.json");
        File.WriteAllText(path, File.ReadAllText(Storage.TasksPath));
        var before = File.ReadAllBytes(Storage.TasksPath);
        var tasks = Storage.LoadTasksFrom(path);
        Check(Storage.LoadTasks().All(task => task.JobId != "P-1"), "the app does not read tasks.json");
        Check(before.AsSpan().SequenceEqual(File.ReadAllBytes(Storage.TasksPath)), "tasks.json is left untouched");
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

        var reloaded = Storage.LoadTasks().OrderBy(task => task.JobId, StringComparer.Ordinal).ToList();
        Equal(ApplicationPlatform.Lever, reloaded[0].ApplicationPlatform, "platform survived");
        Equal("https://jobs.lever.co/oracle/1", reloaded[0].ApplyUrl, "ApplyUrl survived");
        Equal(ApplicationPlatform.Unknown, reloaded[1].ApplicationPlatform, "an uncaptured task stays Unknown");

        // The shape tasks.json had before the platform existed.
        var olderPath = Path.Combine(NewDir("platform-older-json"), "tasks.json");
        File.WriteAllText(olderPath, $$"""
        [ { "JobId": "RB-OLD-P", "Link": "{{ApplyJobPage}}", "ApplyUrl": "https://boards.greenhouse.io/o/jobs/1",
            "ApplyUrlCapturedAt": "2026-09-18T14:00:00", "Status": "Completed" },
          { "JobId": "RB-OLD-Q", "Status": "Queued" } ]
        """);
        var older = Storage.LoadTasksFrom(olderPath);
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
        Equal(ApplicationPlatform.Unknown, job.ApplicationPlatform, "a replaced address re-detects");

        // An unknown job and a refused address leave the platform alone.
        ApplyCapture.Record(tasks, ApplyJobPage, "https://www.linkedin.com/in/someone/", now);
        Equal(ApplicationPlatform.Unknown, job.ApplicationPlatform, "a refused address changes nothing");

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

    static void PlatformFingerprints() {
        Equal(50, ApplicationPlatformDetector.SupportedCount, "fifty recruiting platforms");
        (ApplicationPlatform Platform, string Url)[] each = {
            (ApplicationPlatform.Greenhouse, "https://boards.greenhouse.io/acme/jobs/1"),
            (ApplicationPlatform.Workday, "https://acme.wd1.myworkdayjobs.com/job/1"),
            (ApplicationPlatform.Lever, "https://jobs.lever.co/acme/1"),
            (ApplicationPlatform.Ashby, "https://jobs.ashbyhq.com/acme/1"),
            (ApplicationPlatform.SmartRecruiters, "https://jobs.smartrecruiters.com/Acme/1"),
            (ApplicationPlatform.ICims, "https://careers-acme.icims.com/jobs/1/job"),
            (ApplicationPlatform.Taleo, "https://cognizant.taleo.net/careersection/job/1"),
            (ApplicationPlatform.BambooHr, "https://acme.bamboohr.com/careers/1"),
            (ApplicationPlatform.Jobvite, "https://jobs.jobvite.com/acme/job/1"),
            (ApplicationPlatform.SuccessFactors, "https://acme.successfactors.com/career?job=1"),
            (ApplicationPlatform.AdpRecruiting, "https://recruiting.adp.com/jobs/1"),
            (ApplicationPlatform.OracleRecruitingCloud, "https://careersearch.stanford.edu/hcmUI/CandidateExperience/en/sites/CX/job/1"),
            (ApplicationPlatform.UkgPro, "https://recruiting.ultipro.com/ACM/JobBoard/1"),
            (ApplicationPlatform.JazzHr, "https://acme.jazz.co/apply/1"),
            (ApplicationPlatform.Recruitee, "https://acme.recruitee.com/o/1"),
            (ApplicationPlatform.BreezyHr, "https://acme.breezy.hr/p/1"),
            (ApplicationPlatform.Pinpoint, "https://app.pinpoint.com/acme/jobs/1"),
            (ApplicationPlatform.Teamtailor, "https://acme.teamtailor.com/jobs/1"),
            (ApplicationPlatform.Workable, "https://apply.workable.com/acme/j/1"),
            (ApplicationPlatform.RipplingRecruiting, "https://ats.rippling.com/acme/jobs/1"),
            (ApplicationPlatform.DayforceRecruiting, "https://acme.dayforcehcm.com/CandidatePortal/en-US/acme"),
            (ApplicationPlatform.CornerstoneRecruiting, "https://acme.csod.com/ux/ats/careersite/1/home"),
            (ApplicationPlatform.Avature, "https://acme.avature.net/careers/JobDetail/1"),
            (ApplicationPlatform.Phenom, "https://jobs.phenompeople.com/acme/1"),
            (ApplicationPlatform.Eightfold, "https://acme.eightfold.ai/careers/job/1"),
            (ApplicationPlatform.Beamery, "https://app.beamery.com/acme/jobs/1"),
            (ApplicationPlatform.Bullhorn, "https://acme.bullhornstaffing.com/JobBoard/1"),
            (ApplicationPlatform.JobAdder, "https://acme.jobadder.com/job/1"),
            (ApplicationPlatform.ZohoRecruit, "https://recruit.zoho.com/recruit/PortalDetail.na?job=1"),
            (ApplicationPlatform.Cats, "https://acme.catsone.com/careers/1"),
            (ApplicationPlatform.ApplicantStack, "https://acme.applicantstack.com/x/openings/1"),
            (ApplicationPlatform.ClearCompany, "https://acme.clearcompany.com/careers/jobs/1"),
            (ApplicationPlatform.PaylocityRecruiting, "https://recruiting.paylocity.com/Recruiting/Jobs/Details/1"),
            (ApplicationPlatform.PaycomRecruiting, "https://www.paycomonline.net/v4/ats/web.php/jobs/1"),
            (ApplicationPlatform.PaycorRecruiting, "https://recruiting.paycor.com/job/1"),
            (ApplicationPlatform.IsolvedTalent, "https://acme.isolvedhire.com/jobs/1"),
            (ApplicationPlatform.Fountain, "https://web.fountain.com/apply/acme/opening/1"),
            (ApplicationPlatform.Paradox, "https://olivia.paradox.ai/co/acme/Job/1"),
            (ApplicationPlatform.Comeet, "https://www.comeet.com/jobs/acme/1"),
            (ApplicationPlatform.Manatal, "https://www.careers-page.com/acme/job/1"),
            (ApplicationPlatform.RecruitCrm, "https://app.recruitcrm.io/apply/1"),
            (ApplicationPlatform.Recruiterflow, "https://recruiterflow.com/acme/jobs/1"),
            (ApplicationPlatform.JobScore, "https://careers.jobscore.com/careers/acme/jobs/1"),
            (ApplicationPlatform.Homerun, "https://acme.homerun.hr/job/1"),
            (ApplicationPlatform.PersonioRecruiting, "https://acme.jobs.personio.de/job/1"),
            (ApplicationPlatform.TeamEngine, "https://app.teamengine.io/apply/1"),
            (ApplicationPlatform.TrakstarHire, "https://acme.recruiterbox.com/jobs/1"),
            (ApplicationPlatform.Neogov, "https://www.neogov.com/careers/job/1"),
            (ApplicationPlatform.GovernmentJobs, "https://www.governmentjobs.com/careers/acme/jobs/1"),
            (ApplicationPlatform.SymplrRecruiting, "https://careers.symplr.com/acme/jobs/1"),
        };
        Equal(50, each.Length, "one address per platform");
        var seen = new HashSet<ApplicationPlatform>();
        foreach (var (platform, url) in each) {
            var result = ApplicationPlatformDetector.Inspect(url);
            Equal(platform, result.Platform, url);
            Equal(PlatformConfidence.High, result.Confidence, platform + " confidence");
            Check(ApplicationPlatformDetector.IsAts(platform), platform + " is an ATS");
            Check(seen.Add(platform), platform + " once");
            Equal("platform:" + IconCache.PlatformKey(platform), IconCache.PlatformCacheKey(platform), platform + " cache key");
        }

        var oracle = ApplicationPlatformDetector.Inspect(
            "https://careersearch.stanford.edu/hcmUI/CandidateExperience/en/sites/CX/job/1");
        Equal(ApplicationPlatform.OracleRecruitingCloud, oracle.Platform, "custom domain path");
        Equal(PlatformConfidence.High, oracle.Confidence, "path confidence");
        Equal("path:/hcmUI/CandidateExperience/", oracle.Evidence, "path evidence");

        var beaten = ApplicationPlatformDetector.Inspect(
            "https://acme.wd1.myworkdayjobs.com/hcmUI/CandidateExperience/job/1");
        Equal(ApplicationPlatform.OracleRecruitingCloud, beaten.Platform, "path beats the Workday host");
        Check(beaten.Evidence.StartsWith("path:", StringComparison.Ordinal), "path evidence wins");

        Equal(ApplicationPlatform.Unknown, ApplicationPlatformDetector.Detect("https://careers.cognizant.com/global/en/search-results"),
              "a company careers page stays Unknown");
        Equal(ApplicationPlatform.Taleo, ApplicationPlatformDetector.Detect("https://cognizant.taleo.net/job/1"),
              "Taleo host");
        Equal(ApplicationPlatform.Workable, ApplicationPlatformDetector.Detect("https://apply.workable.com/acme/j/1"), "Workable");
        Equal(ApplicationPlatform.Teamtailor, ApplicationPlatformDetector.Detect("https://career.teamtailor.com/jobs/1"), "Teamtailor");
        Equal(ApplicationPlatform.RipplingRecruiting, ApplicationPlatformDetector.Detect("https://ats.rippling.com/acme/jobs/9"),
              "Rippling recruiting path");
        Equal(ApplicationPlatform.Unknown, ApplicationPlatformDetector.Detect("https://app.rippling.com/dashboard"),
              "Rippling outside recruiting");
        Equal(ApplicationPlatform.Unknown, ApplicationPlatformDetector.Detect("https://example.com/jobs/1"), "unknown stays Unknown");

        var page = ApplicationPlatformDetector.InspectPage("https://careers.example.edu/search", new PlatformPageSignals {
            Scripts = ["https://static.oracle.com/hcmUI/CandidateExperience/main.js"]
        });
        Equal(ApplicationPlatform.OracleRecruitingCloud, page.Platform, "page fingerprint");
        Equal(PlatformConfidence.Medium, page.Confidence, "page confidence");
        Check(page.Evidence.StartsWith("page:", StringComparison.Ordinal), "page evidence");
        var already = ApplicationPlatformDetector.InspectPage("https://jobs.lever.co/acme/1", new PlatformPageSignals {
            Scripts = ["https://boards.greenhouse.io/embed.js"]
        });
        Equal(ApplicationPlatform.Lever, already.Platform, "a known URL is not overridden by the page");

        var kept = Job("RB-KEEP", "Cognizant", "Engineer", "https://jobright.ai/jobs/info/aaaaaaaaaaaaaaaaaaaaaaaa");
        kept.ApplyUrl = "https://careers.cognizant.com/global/en/search-results";
        kept.ApplicationPlatform = ApplicationPlatform.Greenhouse;
        Equal(0, ApplicationPlatformDetector.Refresh(new[] { kept }), "Unknown does not replace a stored ATS");
        Equal(ApplicationPlatform.Greenhouse, kept.ApplicationPlatform, "Greenhouse kept");
        Check(!ApplicationPlatformDetector.ShouldUpdate(ApplicationPlatform.Greenhouse, page),
              "a page guess does not downgrade a stored ATS");
        Check(ApplicationPlatformDetector.ShouldUpdate(ApplicationPlatform.Unknown, page),
              "a page guess can fill an empty platform");
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
        }, JobImporter.BrowserSource, tasks, NoFilters);

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

    static void QueueAndStackOrder() {
        var older = new JobTask { JobId = "RB-OLD", Company = "A", Title = "T", Status = "Queued", CreatedAt = new DateTime(2026, 9, 1, 8, 0, 0) };
        var newer = new JobTask { JobId = "RB-NEW", Company = "B", Title = "T", Status = "Queued", CreatedAt = new DateTime(2026, 9, 2, 8, 0, 0) };
        var sameTime = new JobTask { JobId = "RB-AAA", Company = "C", Title = "T", Status = "Queued", CreatedAt = newer.CreatedAt };
        var running = new JobTask { JobId = "RB-RUN", Company = "D", Title = "T", Status = "Processing", CreatedAt = new DateTime(2026, 9, 3, 8, 0, 0) };
        var failed = new JobTask { JobId = "RB-FAIL", Company = "E", Title = "T", Status = "Failed", CreatedAt = new DateTime(2026, 8, 1, 8, 0, 0) };
        var done = new JobTask { JobId = "RB-DONE", Company = "F", Title = "T", Status = "Completed", CreatedAt = new DateTime(2026, 8, 2, 8, 0, 0) };
        var stamp = older.CreatedAt;
        var list = new List<JobTask> { newer, failed, older, running, done, sameTime };
        string Ids(IEnumerable<JobTask> jobs) => string.Join(",", jobs.Select(job => job.JobId));

        var fifo = JobQueueOrder.Arrange(list, QueueModes.Queue);
        Equal("RB-RUN,RB-OLD,RB-AAA,RB-NEW,RB-FAIL,RB-DONE", Ids(fifo), "queue runs the oldest waiting job first");
        var lifo = JobQueueOrder.Arrange(list, QueueModes.Stack);
        Equal("RB-RUN,RB-AAA,RB-NEW,RB-OLD,RB-FAIL,RB-DONE", Ids(lifo), "stack runs the newest waiting job first");
        Equal("Processing", running.Status, "the processing job stays processing");
        Equal("Failed", failed.Status, "a failed job is not requeued");
        Equal("Completed", done.Status, "a completed job is not requeued");
        Equal(stamp, older.CreatedAt, "timestamps are not rewritten");
        Equal(ApplicationStatus.Viewed, older.ApplicationStatus, "application status is untouched");

        var imported = new JobTask { JobId = "RB-IMP", Company = "G", Title = "T", Status = "Queued", CreatedAt = new DateTime(2026, 9, 4, 8, 0, 0) };
        Equal("RB-RUN,RB-OLD,RB-AAA,RB-NEW,RB-IMP,RB-FAIL,RB-DONE",
              Ids(JobQueueOrder.Arrange(fifo.Append(imported).ToList(), QueueModes.Queue)),
              "a new job goes to the bottom of the queue");
        Equal("RB-RUN,RB-IMP,RB-AAA,RB-NEW,RB-OLD,RB-FAIL,RB-DONE",
              Ids(JobQueueOrder.Arrange(lifo.Append(imported).ToList(), QueueModes.Stack)),
              "a new job goes to the top of the stack");

        var first = new JobTask { JobId = "RB-A", Company = "A", Title = "T", Status = "Queued", CreatedAt = new DateTime(2026, 9, 1) };
        var second = new JobTask { JobId = "RB-B", Company = "B", Title = "T", Status = "Queued", CreatedAt = new DateTime(2026, 9, 2) };
        var third = new JobTask { JobId = "RB-C", Company = "C", Title = "T", Status = "Queued", CreatedAt = new DateTime(2026, 9, 3) };
        var runner = new QueueRunner();
        runner.Start(JobQueueOrder.Arrange(new[] { third, first, second }, QueueModes.Queue));
        var active = runner.Next();
        Equal("RB-A", active!.JobId, "the run starts with the oldest");
        active.Status = "Processing";
        runner.ReorderRemaining(JobQueueOrder.Arrange(new[] { active, second, third }, QueueModes.Stack));
        Equal("RB-A", runner.ActiveJobId, "changing mode leaves the active job active");
        Equal("Processing", active.Status, "changing mode does not requeue it");
        active.Status = "Completed";
        Equal("RB-C", runner.Next()!.JobId, "the rest of the run follows stack order");
        runner.Stop();

        UsingStore(() => {
            var savedOlder = new JobTask { JobId = "RB-A", Company = "A", Title = "T", Status = "Queued", CreatedAt = new DateTime(2026, 9, 1, 9, 0, 0) };
            var savedNewer = new JobTask { JobId = "RB-B", Company = "B", Title = "T", Status = "Queued", CreatedAt = new DateTime(2026, 9, 2, 9, 0, 0) };
            Storage.SaveTasks(new[] { savedNewer, savedOlder });
            var loaded = Storage.LoadTasks();
            Equal("RB-B,RB-A", Ids(JobQueueOrder.Arrange(loaded, QueueModes.Stack)), "restart keeps stack order");
            Equal("RB-A,RB-B", Ids(JobQueueOrder.Arrange(loaded, QueueModes.Queue)), "restart keeps queue order");
            Equal("Queued", loaded.Single(job => job.JobId == "RB-A").Status, "saved queue status");
            Equal(savedOlder.CreatedAt, loaded.Single(job => job.JobId == "RB-A").CreatedAt, "saved import time");
        });

        var path = Path.Combine(NewDir("queue-mode"), "settings.json");
        Storage.SaveSettingsTo(path, new AppSettings { QueueOrder = "stack" });
        Equal(QueueModes.Stack, QueueModes.Normalize(Storage.LoadSettingsFrom(path).QueueOrder), "the selected mode is saved");
        Equal(QueueModes.Queue, QueueModes.Normalize(new AppSettings().QueueOrder), "a missing mode is Queue");
        Equal(QueueModes.Queue, QueueModes.Normalize("lifo"), "an unknown mode is Queue");
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
    static void WithTasksFileRestored(Action body) => WithLiveStateFile(Storage.TasksPath, body);

    static void ImportOneCreatesOneViewedTask() => WithTasksFileRestored(() => {
        var tasks = new List<JobTask> { Job("OLD-TASK-1") };
        var outcome = JobImporter.ImportOne(CanonicalJob(), JobImporter.BrowserSource, tasks, NoFilters);

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
        JobImporter.ImportOne(CanonicalJob("https://example.com/jobs/1"), JobImporter.IncomingSource, tasks, NoFilters);
        JobImporter.ImportOne(CanonicalJob("https://example.com/jobs/2"), JobImporter.IncomingSource, tasks, NoFilters);

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
        JobImporter.ImportOne(CanonicalJob(), JobImporter.BrowserSource, tasks, NoFilters);
        var original = tasks[0];

        var reloaded = Storage.LoadTasks().Single();
        Equal(original.JobId, reloaded.JobId, "the internal id survives save and reload unchanged");
        Equal("https://www.caterpillar.com/", reloaded.CompanyUrl, "companyUrl persisted");
        Equal(original.Link, reloaded.Link, "job URL persisted");
        Equal(ApplicationStatus.Viewed, reloaded.ApplicationStatus, "status persisted");

        // Importing the same job again after the reload finds it by URL and keeps its id.
        var again = JobImporter.ImportOne(CanonicalJob(), JobImporter.BrowserSource, Storage.LoadTasks(), NoFilters);
        Equal(JobImportKind.Duplicate, again.Kind, "still a duplicate after reload");
        Equal(original.JobId, again.JobId, "the duplicate names the original task, not a new id");
    });

    static void DuplicatesAreFoundByJobUrl() => WithTasksFileRestored(() => {
        var tasks = new List<JobTask>();
        const string url = "https://jobright.ai/jobs/info/6aac7fec95c707f49dff195f";
        var first = JobImporter.ImportOne(CanonicalJob(url), JobImporter.BrowserSource, tasks, NoFilters);

        foreach (var same in new[] {
            url,                                                 // raw-identical
            url + "?utm_source=1146",                            // tracking
            url + "/#apply",                                     // slash and fragment
            "HTTPS://JOBRIGHT.AI/jobs/info/6aac7fec95c707f49dff195f" }) {
            var outcome = JobImporter.ImportOne(CanonicalJob(same), JobImporter.BrowserSource, tasks, NoFilters);
            Equal(JobImportKind.Duplicate, outcome.Kind, "duplicate: " + same);
            Equal(first.JobId, outcome.JobId, "reports the existing task for " + same);
            Equal("Senior AI Software Engineer", outcome.Title, "reports the existing title");
            Equal("Caterpillar Inc", outcome.Company, "reports the existing company");
        }
        Equal(1, tasks.Count, "no second task, no second id");

        // A different job at the same company is a different job.
        var other = JobImporter.ImportOne(CanonicalJob("https://jobright.ai/jobs/info/bbbbbbbbbbbbbbbbbbbbbbbb"),
                                          JobImporter.BrowserSource, tasks, NoFilters);
        Equal(JobImportKind.Imported, other.Kind, "two job URLs at the same company are both allowed");
        Equal(2, tasks.Count, "second job added");

        // The duplicate key is the job URL, not the id: an old task with a matching link is found.
        var legacy = new List<JobTask> { new() { JobId = "STRESS-1-001", Company = "X", Title = "Y", Link = url + "?utm_source=old" } };
        Equal(JobImportKind.Duplicate, JobImporter.ImportOne(CanonicalJob(url), JobImporter.BrowserSource, legacy, NoFilters).Kind,
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
            var outcome = JobImporter.ImportOne(data, JobImporter.IncomingSource, tasks, NoFilters);
            Equal(JobImportKind.Invalid, outcome.Kind, reason);
            Equal(reason, outcome.Reason, "reason");
            Equal(0, tasks.Count, "nothing added for: " + reason);
        }

        // companyUrl is optional: missing, empty or unusable, the job still imports.
        foreach (var companyUrl in new string?[] { null, "", "not a url" }) {
            var data = CanonicalJob("https://example.com/jobs/" + Guid.NewGuid().ToString("N"));
            data.CompanyUrl = companyUrl;
            Equal(JobImportKind.Imported, JobImporter.ImportOne(data, JobImporter.IncomingSource, tasks, NoFilters).Kind, "companyUrl " + (companyUrl ?? "null"));
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
        JobImporter.ImportOne(CanonicalJob("https://example.com/jobs/one?utm_source=email"), JobImporter.BrowserSource, tasks, NoFilters);

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

    // ================= ChatGPT rate limit + pacing =================

    static void RateLimitIsDetectedFromItsWording() {
        foreach (var text in new[] { "Too many requests", "Error: TOO MANY REQUESTS.",
                                     "You’re making requests too quickly. Please wait a few minutes before trying again.",
                                     "Please wait a few minutes before trying again" })
            Check(RateLimit.IsRateLimitText(text), "detected: " + text);
        foreach (var text in new[] { null, "", "Too many cooks", "Network error", "Something went wrong" })
            Check(!RateLimit.IsRateLimitText(text), "not a rate limit: " + (text ?? "null"));

        // The page probe returns only its two tokens and never acts on the page.
        var script = RateLimit.ProbeScript;
        var returns = System.Text.RegularExpressions.Regex.Matches(script, @"return\s+'([^']*)'").Select(m => m.Groups[1].Value).Distinct().OrderBy(s => s);
        Equal("ok,rate-limited", string.Join(",", returns), "only the two tokens are returned");
        foreach (var skipped in new[] { "data-message-author-role=\"user\"", ".markdown", "contenteditable", "#prompt-textarea", "pre, code" })
            Check(script.Contains(skipped, StringComparison.Ordinal), "the user's message, composer and rendered answer are skipped: " + skipped);
        foreach (var forbidden in new[] { "click(", "dispatchEvent", "clipboard", "fetch(", ".value =", "submit(" })
            Check(!script.Contains(forbidden, StringComparison.Ordinal), "the probe never acts: " + forbidden);
        foreach (var phrase in RateLimit.Phrases)
            Check(script.Contains(phrase, StringComparison.Ordinal), "the probe uses the same wording: " + phrase);
    }

    static void RateLimitEndsTheWatchAtOnce() {
        // Generating, then the popup appears on the 5th poll: the watch stops there, it does not keep polling.
        var probe = new ScriptedProbe(_ => "generating");
        var checks = 0;
        var outcome = ChatCompletionWatcher.WaitForAnswerAsync(probe, default, (_, _) => Task.CompletedTask, null,
                          () => Task.FromResult(++checks >= 5)).GetAwaiter().GetResult();
        Equal(CompletionOutcome.RateLimited, outcome, "the rate limit ends the watch");
        Equal(4, probe.Polls, "no further state polls after it was seen");

        // Without a rate limit the watch behaves exactly as before.
        var normal = ChatCompletionWatcher.WaitForAnswerAsync(new ScriptedProbe(p => p < 5 ? "generating" : "idle"), default,
                          (_, _) => Task.CompletedTask, null, () => Task.FromResult(false)).GetAwaiter().GetResult();
        Equal(CompletionOutcome.Ready, normal, "no rate limit -> Ready as usual");
    }

    /// <summary>A virtual clock the gate reads, advanced only by the delays it asks for.</summary>
    sealed class VirtualClock {
        public DateTime Now = new(2026, 9, 21, 9, 0, 0);
        public readonly List<TimeSpan> Delays = new();
        public Task Delay(TimeSpan span, CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            Delays.Add(span);
            Now += span;
            return Task.CompletedTask;
        }
    }

    static void NothingIsSentDuringACooldown() {
        var clock = new VirtualClock();
        var gate = new RateLimitGate(() => clock.Now);
        var start = clock.Now;

        // The CONFIGURED cooldown is used, not a fixed 10 minutes.
        var configured = RateLimit.Cooldown(new AppSettings { RateLimitCooldownMinutes = 15 });
        Equal(TimeSpan.FromMinutes(15), configured, "the configured 15 minutes");
        Equal("RATE LIMIT detected — queue paused for 15 minutes RB-1", RateLimit.DetectedLog("RB-1", configured), "the log names it");

        Check(!gate.IsActive, "no cooldown before a rate limit");
        Equal(start + TimeSpan.FromMinutes(15), gate.Start(configured), "a 15-minute cooldown");
        Check(gate.IsActive, "the cooldown is active");

        DateTime? sentAt = null;
        var resumed = RateLimit.CooldownThenResumeAsync(gate, () => { sentAt = clock.Now; return Task.CompletedTask; },
                                                         default, clock.Delay).GetAwaiter().GetResult();
        Check(resumed, "resumed after the cooldown");
        Check(sentAt >= start + TimeSpan.FromMinutes(15), "nothing is sent before the 15 minutes are up: sent at " + sentAt);
        Check(clock.Delays.All(d => d <= TimeSpan.FromSeconds(30)), "waited in short local steps, polling nothing remote");

        // Stop cancels an active cooldown: it returns at once and nothing is sent.
        gate.Start(configured);
        var sent = false;
        using var stop = new CancellationTokenSource();
        var steps = 0;
        Task StopDuringWait(TimeSpan span, CancellationToken ct) { if (++steps == 2) stop.Cancel(); return clock.Delay(span, ct); }
        Check(!RateLimit.CooldownThenResumeAsync(gate, () => { sent = true; return Task.CompletedTask; }, stop.Token, StopDuringWait)
                  .GetAwaiter().GetResult(), "a stopped cooldown reports so");
        Check(!sent, "and sends nothing");
        Check(steps <= 3, "it stops waiting immediately, not after the full cooldown");

        // Seen again: another cooldown with the CURRENT configured value.
        clock.Now += TimeSpan.FromMinutes(20);
        var changed = RateLimit.Cooldown(new AppSettings { RateLimitCooldownMinutes = 5 });
        Equal(clock.Now + TimeSpan.FromMinutes(5), gate.Start(changed), "a repeat uses the value configured now");
    }

    static void TheSameJobResumesAfterTheCooldown() {
        var clock = new VirtualClock();
        var gate = new RateLimitGate(() => clock.Now);
        var job = Job("RB-RATE-1");
        var attempt = 2;                                   // the attempt that hit the rate limit
        var resent = new List<(string JobId, int Attempt)>();

        gate.Start(RateLimit.Cooldown(new AppSettings()));
        RateLimit.CooldownThenResumeAsync(gate, () => { resent.Add((job.JobId, attempt)); return Task.CompletedTask; },
                                          default, clock.Delay).GetAwaiter().GetResult();

        Equal(1, resent.Count, "re-sent exactly once");
        Equal("RB-RATE-1", resent[0].JobId, "the same job");
        Equal(2, resent[0].Attempt, "at the same attempt number: a rate limit is not a used attempt");
        Equal("Queued", job.Status, "the job is not marked Failed");
        Check(string.IsNullOrEmpty(job.FailureReason), "and carries no failure reason");
    }

    static void TheConfiguredPauseSeparatesJobs() {
        Equal(TimeSpan.FromSeconds(30), RateLimit.JobDelay(new AppSettings()), "30 seconds by default");

        // The configured value is used instead of 30.
        var clock = new VirtualClock();
        var delay = RateLimit.JobDelay(new AppSettings { GptJobDelaySeconds = 45 });
        Check(RateLimit.WaitBetweenJobsAsync(delay, default, clock.Delay).GetAwaiter().GetResult(), "then the queue advances");
        Equal(TimeSpan.FromSeconds(45), clock.Delays.Single(), "one 45-second wait");

        // 0 seconds: the next job starts immediately, with no wait at all.
        var immediate = new VirtualClock();
        Check(RateLimit.WaitBetweenJobsAsync(RateLimit.JobDelay(new AppSettings { GptJobDelaySeconds = 0 }), default, immediate.Delay)
                  .GetAwaiter().GetResult(), "0 advances");
        Equal(0, immediate.Delays.Count, "without waiting");

        // Stop during the delay: stop waiting, do not advance.
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        Check(!RateLimit.WaitBetweenJobsAsync(delay, stopped.Token, clock.Delay).GetAwaiter().GetResult(), "a stopped queue does not advance");
    }

    static void PacingSettingsPersistAndValidate() {
        var dir = NewDir("pacing-settings");

        // A settings.json written before these fields existed gets 30 / 10 and keeps everything else.
        var old = Path.Combine(dir, "old.json");
        File.WriteAllText(old, """{ "MasterPrompt": "C:\\p.txt", "AutoSend": false, "SkipExportControl": true }""");
        var loaded = Storage.LoadSettingsFrom(old);
        Equal(30, loaded.GptJobDelaySeconds, "job delay default");
        Equal(10, loaded.RateLimitCooldownMinutes, "cooldown default");
        Equal(@"C:\p.txt", loaded.MasterPrompt, "other settings kept");
        Check(!loaded.AutoSend && loaded.SkipExportControl, "other flags kept");

        // Values persist and reload through the safe storage.
        var path = Path.Combine(dir, "settings.json");
        Check(Storage.SaveSettingsTo(path, new AppSettings { GptJobDelaySeconds = 0, RateLimitCooldownMinutes = 45, MasterPrompt = "x" }), "saved");
        var reloaded = Storage.LoadSettingsFrom(path);
        Equal(0, reloaded.GptJobDelaySeconds, "0 seconds persists");
        Equal(45, reloaded.RateLimitCooldownMinutes, "45 minutes persists");
        Equal("x", reloaded.MasterPrompt, "an unrelated setting persists");

        // Settings -> Save validation: whole numbers in range; nothing is guessed or turned into 0.
        foreach (var ok in new[] { "0", "30", "600", " 45 " })
            Check(RateLimit.ValidateJobDelay(ok, out _) is null, "job delay accepted: " + ok);
        foreach (var bad in new[] { "", "abc", "-1", "601", "2.5", "1e3", "30s" })
            Check(RateLimit.ValidateJobDelay(bad, out _) is string, "job delay refused: [" + bad + "]");
        foreach (var ok in new[] { "1", "10", "120" })
            Check(RateLimit.ValidateCooldown(ok, out _) is null, "cooldown accepted: " + ok);
        foreach (var bad in new[] { "0", "121", "", "ten", "-5" })
            Check(RateLimit.ValidateCooldown(bad, out _) is string, "cooldown refused: [" + bad + "]");
        Equal("GPT Job Delay must be a whole number from 0 to 600 seconds.", RateLimit.ValidateJobDelay("x", out _), "the message");
        Check(RateLimit.ValidateJobDelay("75", out var seconds) is null && seconds == 75, "the parsed value is returned");

        // A hand-edited out-of-range value is clamped at use and never breaks a run.
        Equal(TimeSpan.FromSeconds(600), RateLimit.JobDelay(new AppSettings { GptJobDelaySeconds = 99999 }), "clamped to 600 s");
        Equal(TimeSpan.FromMinutes(1), RateLimit.Cooldown(new AppSettings { RateLimitCooldownMinutes = 0 }), "clamped to 1 min");
    }

    // ================= Phase 1: runtime safety and install-readiness =================
    //
    // Every file here is in a temporary folder; nothing touches the live settings.json / tasks.json.

    static void SettingsSaveIsAtomicWithBackup() {
        var dir = NewDir("p1-settings-atomic");
        var path = Path.Combine(dir, "settings.json");

        Check(Storage.SaveSettingsTo(path, new AppSettings { MasterPrompt = @"C:\prompts\first.txt" }), "first save");
        Check(!File.Exists(path + ".bak"), "nothing to back up on the first save");
        Check(Storage.SaveSettingsTo(path, new AppSettings { MasterPrompt = @"C:\prompts\second.txt" }), "second save");

        Equal(@"C:\prompts\second.txt", Storage.LoadSettingsFrom(path).MasterPrompt, "the saved value");
        Check(File.Exists(path + ".bak"), "the previous version is kept as .bak");
        Check(File.ReadAllText(path + ".bak").Contains("first.txt", StringComparison.Ordinal), ".bak is the previous version");
        Check(!File.Exists(path + ".tmp"), "no temporary file is left");

        // Same JSON shape as before: an older reader (plain System.Text.Json) still reads it.
        var plain = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path))!;
        Equal(@"C:\prompts\second.txt", plain.MasterPrompt, "backward-compatible JSON");
    }

    static void TasksSaveIsAtomicWithBackup() {
        var dir = NewDir("p1-tasks-atomic");
        var path = Path.Combine(dir, "tasks.json");

        Check(Storage.SaveTasksTo(path, new[] { Job("T-1") }), "first save");
        Check(Storage.SaveTasksTo(path, new[] { Job("T-1"), Job("T-2") }), "second save");

        Equal(2, Storage.LoadTasksFrom(path).Count, "the saved jobs");
        Equal(1, JsonSerializer.Deserialize<List<JobTask>>(File.ReadAllText(path + ".bak"))!.Count, ".bak holds the previous version");
        Check(!File.Exists(path + ".tmp"), "no temporary file is left");
    }

    static void CorruptSettingsIsNeverOverwritten() {
        var dir = NewDir("p1-settings-corrupt");
        var path = Path.Combine(dir, "settings.json");
        const string bad = "{ \"MasterPrompt\": \"C:\\\\my prompt.txt\", this is not json";
        File.WriteAllText(path, bad);

        var loaded = Storage.LoadSettingsFrom(path);
        Equal(AppPaths.DefaultResumeRoot, loaded.ResumeRootFolder, "the app runs on defaults");
        Check(Storage.WriteBlockedReason(path) is not null, "saving settings.json is refused this session");
        Check(!Storage.SaveSettingsTo(path, new AppSettings { MasterPrompt = "overwrite!" }), "the save is refused");
        Equal(bad, File.ReadAllText(path), "the user's file is untouched");

        var copies = Directory.GetFiles(dir, "settings.corrupt-*.json");
        Equal(1, copies.Length, "an exact copy is kept");
        Equal(bad, File.ReadAllText(copies[0]), "the copy is byte-identical");
        Check(Storage.Problems.Any(p => p.Contains("settings.json", StringComparison.Ordinal)), "reported for the startup notice");

        // Launching again on the same bad file does not pile up copies.
        Storage.ClearWriteBlock(path);
        Storage.LoadSettingsFrom(path);
        Equal(1, Directory.GetFiles(dir, "settings.corrupt-*.json").Length, "no duplicate copy");
    }

    static void CorruptTasksIsNeverOverwritten() {
        var dir = NewDir("p1-tasks-corrupt");
        var path = Path.Combine(dir, "tasks.json");
        const string bad = "[ { \"JobId\": \"RB-REAL-1\", \"Company\": \"Acme\" }, { broken";
        File.WriteAllText(path, bad);

        Equal(0, Storage.LoadTasksFrom(path).Count, "no jobs are shown");
        Check(!Storage.SaveTasksTo(path, new[] { Job("NEW-1") }), "a save cannot wipe the real jobs");
        Equal(bad, File.ReadAllText(path), "tasks.json is untouched");
        Equal(bad, File.ReadAllText(Directory.GetFiles(dir, "tasks.corrupt-*.json").Single()), "an exact copy is kept");
    }

    static void CorruptFileRecoversFromBackup() {
        var dir = NewDir("p1-tasks-backup");
        var path = Path.Combine(dir, "tasks.json");
        Storage.SaveTasksTo(path, new[] { Job("A") });
        Storage.SaveTasksTo(path, new[] { Job("A"), Job("B") });   // .bak = [A]
        File.WriteAllText(path, "not json at all");

        var loaded = Storage.LoadTasksFrom(path);
        Equal("A", loaded.Single().JobId, "the .bak is used");
        Check(Storage.WriteBlockedReason(path) is null, "saving is allowed again once recovered");
        Equal("not json at all", File.ReadAllText(Directory.GetFiles(dir, "tasks.corrupt-*.json").Single()), "the bad file is kept");
        Check(Storage.SaveTasksTo(path, loaded), "the recovered list saves");
        Equal(1, Storage.LoadTasksFrom(path).Count, "and reloads");
    }

    static void MissingStateFilesAreAFirstRun() {
        var dir = NewDir("p1-first-run");
        var settingsPath = Path.Combine(dir, "settings.json");
        var tasksPath = Path.Combine(dir, "tasks.json");

        var settings = Storage.LoadSettingsFrom(settingsPath);
        Equal(AppPaths.DefaultResumeRoot, settings.ResumeRootFolder, "first-run default output folder");
        Equal(0, Storage.LoadTasksFrom(tasksPath).Count, "no jobs yet");
        Check(Storage.WriteBlockedReason(settingsPath) is null && Storage.WriteBlockedReason(tasksPath) is null, "nothing is refused");
        Check(Storage.SaveSettingsTo(settingsPath, settings) && Storage.SaveTasksTo(tasksPath, new[] { Job("F-1") }), "first saves work");
        Equal(0, Directory.GetFiles(dir, "*.corrupt-*").Length, "a missing file is not treated as corrupt");
    }

    static void LockedStateFileKeepsTheLastGoodCopy() {
        var dir = NewDir("p1-locked");
        var path = Path.Combine(dir, "tasks.json");
        Storage.SaveTasksTo(path, new[] { Job("L-1"), Job("L-2") });
        Equal(2, Storage.LoadTasksFrom(path).Count, "a good load");

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
            Equal(2, Storage.LoadTasksFrom(path).Count, "a locked file returns the last good copy, not an empty list");
            Check(Storage.WriteBlockedReason(path) is null, "a lock is not treated as corruption");
        }
    }

    static void DefaultResumeRootOnlyFillsABlank() {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ResumeAutomation", "Resumes");
        Equal(expected, AppPaths.DefaultResumeRoot, "Documents\\ResumeAutomation\\Resumes via the Documents folder");

        var dir = NewDir("p1-root");
        var custom = Path.Combine(dir, "custom.json");
        File.WriteAllText(custom, """{ "ResumeRootFolder": "D:\\My Resume Output" }""");
        Equal(@"D:\My Resume Output", Storage.LoadSettingsFrom(custom).ResumeRootFolder, "a configured path is never replaced");

        var blank = Path.Combine(dir, "blank.json");
        File.WriteAllText(blank, """{ "ResumeRootFolder": "", "MasterPrompt": "C:\\p.txt" }""");
        var loaded = Storage.LoadSettingsFrom(blank);
        Equal(expected, loaded.ResumeRootFolder, "a blank path gets the default");
        Equal(@"C:\p.txt", loaded.MasterPrompt, "other values are kept");

        var set = new AppSettings { ResumeRootFolder = @"E:\Out" };
        Check(!AppPaths.ApplyFirstRunDefaults(set) && set.ResumeRootFolder == @"E:\Out", "ApplyFirstRunDefaults leaves a set value");
    }

    static void CurrentResumeFollowsTheResumeRoot() {
        Equal(Path.Combine(AppPaths.DefaultAutomationFolder, "CurrentResume.docx"),
              ResumeUpload.CurrentResumePathFor(AppPaths.DefaultResumeRoot), "default layout: ResumeAutomation\\CurrentResume.docx");
        Equal(@"D:\Jobs\CurrentResume.docx", ResumeUpload.CurrentResumePathFor(@"D:\Jobs\Resumes"), "a Resumes root -> its parent");
        Equal(@"D:\Jobs\CurrentResume.docx", ResumeUpload.CurrentResumePathFor(@"D:\Jobs\Resumes\"), "trailing separator");
        Equal(@"D:\My Output\CurrentResume.docx", ResumeUpload.CurrentResumePathFor(@"D:\My Output"), "a custom root -> inside it");
        Equal(@"D:\Resumes\CurrentResume.docx", ResumeUpload.CurrentResumePathFor(@"D:\Resumes"), "never the drive root itself");
        Equal(ResumeUpload.DefaultCurrentResumePath, ResumeUpload.CurrentResumePathFor(""), "blank -> Documents default");
        Equal(ResumeUpload.DefaultCurrentResumePath, ResumeUpload.CurrentResumePathFor(null), "null -> Documents default");
    }

    static void SingleInstanceAllowsOnlyOne() {
        Equal("ResumeBuilder.SingleInstance", SingleInstance.MutexName, "the name the installer will use");

        var name = "ResumeBuilder.Test." + Guid.NewGuid().ToString("N");
        var first = SingleInstance.TryAcquire(name);
        Check(first is not null, "the first instance gets it");
        Check(SingleInstance.TryAcquire(name) is null, "a second instance is refused");
        first!.Dispose();

        var again = SingleInstance.TryAcquire(name);
        Check(again is not null, "free again once the first exits");
        again!.Dispose();
    }

    static void LogsAreRotatedAndBounded() {
        var dir = NewDir("p1-logs");
        var log = Path.Combine(dir, "diagnostics.log");
        for (var session = 1; session <= 8; session++) {
            File.WriteAllText(log, "session " + session);
            LogRetention.Rotate(log, 5);
        }
        Check(!File.Exists(log), "the current log was rotated");
        Equal("session 8", File.ReadAllText(Path.Combine(dir, "diagnostics.1.log")), "newest kept as .1");
        Equal("session 4", File.ReadAllText(Path.Combine(dir, "diagnostics.5.log")), "oldest kept as .5");
        Check(!File.Exists(Path.Combine(dir, "diagnostics.6.log")), "never more than five");

        File.WriteAllText(log, "");
        LogRetention.Rotate(log, 5);
        Check(File.Exists(log), "an empty log is not rotated");

        var crashDir = NewDir("p1-crash");
        for (var i = 0; i < 14; i++) {
            var f = Path.Combine(crashDir, $"crash-old-{i:D2}.log");
            File.WriteAllText(f, "x");
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddHours(-24 + i));
        }
        var written = CrashLog.Write(new InvalidOperationException("boom"), "test", crashDir);
        Check(written is not null && File.Exists(written), "a crash log is written");
        var text = File.ReadAllText(written!);
        Check(text.Contains("InvalidOperationException", StringComparison.Ordinal) && text.Contains("boom", StringComparison.Ordinal)
              && text.Contains("Source:  test", StringComparison.Ordinal), "it names the exception and where it was caught");
        Equal(CrashLog.Kept, Directory.GetFiles(crashDir, "crash-*.log").Length, "only the newest ten are kept");
        Check(File.Exists(written!), "and the new one is among them");
    }

    static void CrashLogIsWrittenOncePerException() {
        var dir = NewDir("p1-crash-once");
        var ex = new InvalidOperationException("same");
        Check(CrashLog.Write(ex, "UI thread", dir) is not null, "first report");
        Check(CrashLog.Write(ex, "background thread", dir) is null, "the same exception is not logged twice");
        Equal(1, Directory.GetFiles(dir, "crash-*.log").Length, "one file");
        Check(CrashLog.Write(new Exception("x"), "t", Path.Combine(dir, "a\0b")) is null, "a bad folder never throws");
    }

    static void WebView2CheckIsFriendly() {
        var ok = WebView2Runtime.Check(() => "153.0.4234.48");
        Check(ok.Available && ok.Version == "153.0.4234.48", "an installed runtime is found");
        Check(!WebView2Runtime.Check(() => null).Available, "no version means not installed");
        var failed = WebView2Runtime.Check(() => throw new FileNotFoundException("WebView2Loader.dll"));
        Check(!failed.Available && failed.Reason!.Contains("could not be checked", StringComparison.Ordinal), "a probe failure is reported, not thrown");

        var message = WebView2Runtime.Message(failed);
        Check(message.Contains(WebView2Runtime.DownloadPage, StringComparison.Ordinal) && message.Contains("Yes", StringComparison.Ordinal)
              && message.Contains("No to exit", StringComparison.Ordinal), "the message names the runtime, where to get it, and Retry / Exit");
        Check(!message.Contains("at Microsoft", StringComparison.Ordinal), "no stack trace in the message");
    }

    static void SetupCheckNamesWhatIsMissing() {
        var fresh = new AppSettings();
        var missing = SetupCheck.Missing(fresh, candidateProfileExists: false);
        Equal("Prompts,Candidate Profile", string.Join(",", missing.Select(m => m.Section)), "a fresh install needs a prompt and a profile");
        Check(SetupCheck.Describe(missing, @"C:\Out")!.Contains(@"C:\Out", StringComparison.Ordinal), "the notice names the output folder");

        var normal = new AppSettings { PromptMode = PromptModes.Normal, MasterPrompt = "" };
        Check(SetupCheck.Missing(normal, true).Single().What.Contains("Normal Prompt", StringComparison.Ordinal), "Normal mode needs its own prompt");

        var prompt = Path.Combine(NewDir("p1-setup"), "prompt.txt");
        File.WriteAllText(prompt, "x");
        var ready = new AppSettings { MasterPrompt = prompt };
        Equal(0, SetupCheck.Missing(ready, true).Count, "configured -> nothing missing");
        Check(SetupCheck.Describe(SetupCheck.Missing(ready, true), "x") is null, "and no notice");
        Equal(prompt, ready.MasterPrompt, "the check never changes a setting");
    }

    // ================= resume upload: Copy Resume Path + CurrentResume.docx =================
    //
    // Every file lives under the temp root, and the clipboard is a recording fake, so neither the
    // user's clipboard nor Documents\ResumeAutomation is ever touched.

    /// <summary>A clipboard stand-in that records what it was asked to copy.</summary>
    sealed class FakeClipboard {
        public List<string> Copied { get; } = new();
        public bool Fail { get; init; }
        public (bool Success, string Message) Set(string text) {
            if (Fail) return (false, "The clipboard is busy.");
            Copied.Add(text);
            return (true, "");
        }
    }

    /// <summary>Generates a real DOCX (and PDF) for one company/role under a temp root, like the app does.</summary>
    static GenerationResult GenerateFor(string root, string company, string role, bool docx = true) =>
        ResumeGenerator.Generate(company, role, Fixture("resume-basic.json"),
                                 new AppSettings { ResumeRootFolder = root, Docx = docx, Pdf = true },
                                 jobId: "RB-UPLOAD-TEST", jobUrl: "https://example.com/job");

    static JobTask UploadJob(string? resumePath, string company = "Acme Corp", string role = "AI Engineer") => new() {
        JobId = "RB-UPLOAD-1", Company = company, Title = role, ResumePath = resumePath ?? "",
        Status = "Completed", ApplicationStatus = ApplicationStatus.Ready
    };

    static string Sha(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

    static void CopyResumePathCopiesTheExactDocx() {
        var root = NewDir("upload-exact");
        var first = GenerateFor(root, "Acme Corp", "AI Engineer");
        var second = GenerateFor(root, "Acme Corp", "AI Engineer");     // same job again -> BILLY LIN (2).docx
        Check(second.DocxPath!.EndsWith("BILLY LIN (2).docx", StringComparison.Ordinal), "the second run is BILLY LIN (2).docx");

        foreach (var generated in new[] { first.DocxPath!, second.DocxPath! }) {
            var clipboard = new FakeClipboard();
            var job = UploadJob(generated);
            var copy = ResumeUpload.CopyResumePath(job, clipboard.Set);

            Check(copy.Copied, "copied: " + copy.Message);
            Equal(1, clipboard.Copied.Count, "one thing copied");
            var text = clipboard.Copied[0];
            Equal(Path.GetDirectoryName(Path.GetFullPath(generated)), text, "exactly the folder holding the job's own DOCX");
            Check(Path.IsPathFullyQualified(text), "an absolute Windows path");
            Check(Directory.Exists(text), "the copied folder exists");
            Check(!text.StartsWith("file:", StringComparison.OrdinalIgnoreCase) &&
                  !text.StartsWith("http", StringComparison.OrdinalIgnoreCase), "never a URL");
            Check(!text.EndsWith(".docx", StringComparison.OrdinalIgnoreCase), "the folder, not Resume.docx");
            Equal("Resume folder path copied.", copy.Message, "the confirmation");

            // Copying changes nothing about the job.
            Equal(generated, job.ResumePath, "ResumePath untouched");
            Equal("Completed", job.Status, "queue status untouched");
            Equal(ApplicationStatus.Ready, job.ApplicationStatus, "application status untouched");
        }
    }

    static void CopyResumePathRefusesAnythingButARealDocx() {
        var root = NewDir("upload-refuse");
        var generated = GenerateFor(root, "Acme Corp", "AI Engineer");

        var bad = new (string What, JobTask? Job)[] {
            ("no job", null),
            ("no resume path", UploadJob("")),
            ("a relative path", UploadJob(@"Resumes\Resume.docx")),
            ("the PDF", UploadJob(generated.PdfPath)),
            ("the folder", UploadJob(Path.GetDirectoryName(generated.DocxPath!))),
            ("a deleted DOCX", UploadJob(Path.Combine(root, "gone", "Resume.docx"))),
            ("a file:// URL", UploadJob(new Uri(generated.DocxPath!).AbsoluteUri))
        };

        foreach (var (what, job) in bad) {
            var clipboard = new FakeClipboard();
            var copy = ResumeUpload.CopyResumePath(job, clipboard.Set);
            Check(!copy.Copied, what + ": nothing copied");
            Equal(ResumeUpload.NotFound, copy.Message, what + ": the safe message");
            Equal(0, clipboard.Copied.Count, what + ": the clipboard was never touched");
        }

        // A clipboard that refuses is reported, not claimed as success.
        var failing = ResumeUpload.CopyResumePath(UploadJob(generated.DocxPath), new FakeClipboard { Fail = true }.Set);
        Check(!failing.Copied && failing.Message.Contains("could not be copied", StringComparison.Ordinal), "a clipboard failure is reported");

        // The alias action refuses a missing alias the same way.
        var noAlias = ResumeUpload.CopyCurrentResumePath(Path.Combine(root, "none", ResumeUpload.AliasFileName), new FakeClipboard().Set);
        Check(!noAlias.Copied, "a missing CurrentResume.docx is not copied");
    }

    static void CurrentResumeIsCreatedAfterGeneration() {
        var root = NewDir("upload-alias-create");
        var alias = Path.Combine(root, "ResumeAutomation", ResumeUpload.AliasFileName);   // folder does not exist yet
        var generated = GenerateFor(Path.Combine(root, "Resumes"), "Acme Corp", "AI Engineer");

        var update = ResumeUpload.PublishCurrentResume(UploadJob(generated.DocxPath), generated, alias);
        Check(update is { Updated: true }, "the alias was written: " + update?.Reason);
        Check(File.Exists(alias), "CurrentResume.docx exists");
        Equal(Sha(generated.DocxPath!), Sha(alias), "it is a byte copy of the generated DOCX");
        Check(!File.Exists(alias + ".tmp"), "no temporary file is left behind");
        Equal($"CURRENT RESUME updated RB-UPLOAD-1 {alias}", update!.LogLine("RB-UPLOAD-1"), "the log line");

        // The alias action copies it.
        var clipboard = new FakeClipboard();
        Check(ResumeUpload.CopyCurrentResumePath(alias, clipboard.Set).Copied, "Copy Current Resume Path works");
        Equal(Path.GetFullPath(alias), clipboard.Copied.Single(), "the alias path is copied");

        // No DOCX produced (DOCX output off) -> nothing to publish.
        var pdfOnly = GenerateFor(Path.Combine(root, "Resumes"), "Pdf Only Co", "Engineer", docx: false);
        Check(ResumeUpload.PublishCurrentResume(UploadJob(pdfOnly.PdfPath), pdfOnly, alias) is null, "a PDF-only run does not touch the alias");
        Equal(Sha(generated.DocxPath!), Sha(alias), "the alias still holds the last DOCX");
    }

    static void CurrentResumeFollowsTheNewestJobAndLeavesOriginalsAlone() {
        var root = NewDir("upload-alias-replace");
        var alias = Path.Combine(root, "ResumeAutomation", ResumeUpload.AliasFileName);

        var a = GenerateFor(Path.Combine(root, "Resumes"), "First Company", "AI Engineer");
        var aHash = Sha(a.DocxPath!);
        ResumeUpload.PublishCurrentResume(UploadJob(a.DocxPath), a, alias);
        Equal(aHash, Sha(alias), "the alias holds job A");

        var b = GenerateFor(Path.Combine(root, "Resumes"), "Second Company", "Staff Engineer");
        var update = ResumeUpload.PublishCurrentResume(UploadJob(b.DocxPath), b, alias);
        Check(update is { Updated: true }, "replaced: " + update?.Reason);
        Equal(Sha(b.DocxPath!), Sha(alias), "the alias now holds job B, the newest");

        // The company-specific originals are exactly where they were, unchanged.
        Check(File.Exists(a.DocxPath!) && File.Exists(b.DocxPath!), "both originals still exist");
        Equal(aHash, Sha(a.DocxPath!), "job A's own resume is unchanged");
        Check(File.Exists(a.PdfPath!) && File.Exists(b.PdfPath!), "the PDFs are untouched");
    }

    static void CurrentResumeFailureNeverFailsTheJob() {
        var root = NewDir("upload-alias-locked");
        var alias = Path.Combine(root, "ResumeAutomation", ResumeUpload.AliasFileName);

        var a = GenerateFor(Path.Combine(root, "Resumes"), "First Company", "AI Engineer");
        ResumeUpload.PublishCurrentResume(UploadJob(a.DocxPath), a, alias);
        var before = Sha(alias);

        var b = GenerateFor(Path.Combine(root, "Resumes"), "Second Company", "Staff Engineer");
        var job = UploadJob(b.DocxPath, "Second Company", "Staff Engineer");

        CurrentResumeUpdate? update;
        // CurrentResume.docx open exclusively — as when it is open in Word.
        using (new FileStream(alias, FileMode.Open, FileAccess.Read, FileShare.None))
            update = ResumeUpload.PublishCurrentResume(job, b, alias);

        Check(update is { Updated: false }, "the locked alias is reported as a failure, not thrown");
        Check(update!.LogLine(job.JobId).StartsWith("WARN current resume alias update failed RB-UPLOAD-1 ", StringComparison.Ordinal),
              "the warning log line: " + update.LogLine(job.JobId));
        Equal(before, Sha(alias), "the previous alias is intact — never zero-byte or partial");
        Check(!File.Exists(alias + ".tmp"), "no temporary file is left behind");

        // The job is exactly as generation left it.
        Equal("Completed", job.Status, "queue status still Completed");
        Equal(ApplicationStatus.Ready, job.ApplicationStatus, "application status still Ready");
        Equal(b.DocxPath, job.ResumePath, "ResumePath still the company-specific resume");
        Check(File.Exists(b.DocxPath!), "the new company-specific resume exists");

        // A missing source is a failure too, never an exception.
        Check(ResumeUpload.UpdateCurrentResume(Path.Combine(root, "missing.docx"), alias) is { Updated: false }, "missing source reported");
    }

    static void UploadPathsSurviveSpacesAndSpecialCharacters() {
        var root = NewDir("upload special root with spaces");
        const string company = "O'Brien & Sons, Inc. (Café)";
        const string role = "Sr. Engineer: AI/ML #1 <Remote>";
        var generated = GenerateFor(Path.Combine(root, "My Resumes"), company, role);
        Check(generated.DocxGenerated, "generated: " + generated.Describe());

        var clipboard = new FakeClipboard();
        var copy = ResumeUpload.CopyResumePath(UploadJob(generated.DocxPath, company, role), clipboard.Set);
        Check(copy.Copied, "copied: " + copy.Message);
        var text = clipboard.Copied.Single();
        Check(Directory.Exists(text), "the copied folder opens as-is: " + text);
        Equal(Path.GetDirectoryName(generated.DocxPath), text, "the folder of the job's DOCX");
        Check(text.Contains(' ') && text.Contains('&') && text.Contains("Café", StringComparison.Ordinal), "spaces and symbols are kept, not escaped");
        Check(!text.Contains('%') && !text.StartsWith("file:", StringComparison.OrdinalIgnoreCase), "no URL encoding");

        var alias = Path.Combine(root, "Resume Automation", ResumeUpload.AliasFileName);
        Check(ResumeUpload.UpdateCurrentResume(generated.DocxPath!, alias).Updated, "the alias works from a path with spaces and symbols");
    }

    static void UploadAliasUsesTheDocumentsFolderNotAUserName() {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ResumeAutomation", "CurrentResume.docx");
        Equal(expected, ResumeUpload.DefaultCurrentResumePath, "resolved from the Documents known folder");

        var source = File.ReadAllText(Path.Combine(Root(), "ResumeUpload.cs"));
        foreach (var hardcoded in new[] { Environment.UserName, @"C:\Users", "Teddy" })
            Check(!source.Contains(hardcoded, StringComparison.OrdinalIgnoreCase), "no hard-coded '" + hardcoded + "' in ResumeUpload.cs");
    }

    static void UploadHelpersLeaveLiveDataAlone() {
        string? Hash(string path) => File.Exists(path) ? Sha(path) : null;
        var tasks = Hash(Storage.TasksPath);
        var settings = Hash(Storage.SettingsPath);
        var liveAlias = Hash(ResumeUpload.DefaultCurrentResumePath);

        var root = NewDir("upload-live-data");
        var generated = GenerateFor(root, "Acme Corp", "AI Engineer");
        ResumeUpload.CopyResumePath(UploadJob(generated.DocxPath), new FakeClipboard().Set);
        ResumeUpload.PublishCurrentResume(UploadJob(generated.DocxPath), generated, Path.Combine(root, ResumeUpload.AliasFileName));

        Equal(tasks, Hash(Storage.TasksPath), "tasks.json untouched");
        Equal(settings, Hash(Storage.SettingsPath), "settings.json untouched");
        Equal(liveAlias, Hash(ResumeUpload.DefaultCurrentResumePath), "the real CurrentResume.docx untouched");

        // The existing Open Resume / Folder / Apply rules still refuse exactly what they did.
        Check(!JobTracker.OpenResume(null) && !JobTracker.OpenResume(UploadJob("")), "Open Resume still refuses a missing resume");
        Check(!JobTracker.OpenResumeFolder(UploadJob("")), "Open Folder still refuses a missing resume");
        Check(JobTracker.ApplyUrlToOpen(UploadJob(generated.DocxPath)) is null, "Apply still needs an ApplyUrl, never the resume");
    }

    // ================= Normal Prompt style contract: nested schema, 9 pt body =================
    //
    // Everything here writes only to temporary folders, except the two Accept() checks, which go
    // through the real results folder under a throwaway job id and delete what they wrote.

    /// <summary>The style the promV5 Normal Prompt asks for, in the REAL nested schema.</summary>
    const string NinePointStyleJson = """
        {
          "preset": "promV4.12",
          "page": { "size": "LETTER", "marginTop": 0.45, "marginBottom": 0.45, "marginLeft": 0.55, "marginRight": 0.55 },
          "colors": { "primary": "#1F4E79", "body": "#000000", "secondary": "#4A4A4A" },
          "fonts": { "family": "Arial" },
          "metadata": { "fontSize": 8.7 },
          "skillValues": { "fontSize": 9, "bold": false, "lineSpacing": 1 },
          "body": { "fontSize": 9, "bold": false, "lineSpacing": 1 },
          "bullet": { "fontSize": 9, "bold": false, "lineSpacing": 1, "leftIndent": 0.18, "hangingIndent": 0.14 },
          "education": { "fontSize": 9, "bold": false, "lineSpacing": 1 }
        }
        """;

    /// <summary>resume-basic.json as a ChatGPT answer, with or without the 9 pt style (under any key).</summary>
    static string NinePointAnswer(bool includeStyle = true, string styleKey = "style", string? styleJson = null) {
        var root = JsonNode.Parse(File.ReadAllText(Fixture("resume-basic.json")))!.AsObject();
        if (includeStyle) root[styleKey] = JsonNode.Parse(styleJson ?? NinePointStyleJson);
        return "Here is the resume:\n\n```json\n" + root.ToJsonString() + "\n```\n";
    }

    /// <summary>A style object with one section set, for the range checks.</summary>
    static JsonObject OneValue(string section, string key, double value) =>
        new() { [section] = new JsonObject { [key] = value } };

    static void NinePointAcceptedInBodySections() {
        foreach (var section in StyleSchema.AlwaysRegular) {
            var style = OneValue(section, "fontSize", 9);
            Equal(0, StyleValidator.Validate(style).Count, section + " 9 pt is valid: " + Join(StyleValidator.Validate(style)));

            var normalized = StyleNormalizer.Normalize(style);
            Equal(9.0, normalized.Style.Section(section).FontSize, section + " 9 pt survives normalization");
            Equal(0, normalized.Warnings.Count, section + " 9 pt needs no correction: " + Join(normalized.Warnings));
        }

        // The whole intended style, in one go: valid, and not a single warning.
        var full = JsonNode.Parse(NinePointStyleJson);
        Equal(0, StyleValidator.Validate(full).Count, "the full 9 pt style is valid: " + Join(StyleValidator.Validate(full)));
        var result = StyleNormalizer.Normalize(full);
        Equal(0, result.Warnings.Count, "the full 9 pt style raises no warning: " + Join(result.Warnings));
        Equal(8.7, result.Style.Metadata.FontSize, "metadata keeps its own 8.7 pt");
    }

    static void BelowNinePointIsCorrectedPerApi() {
        foreach (var section in StyleSchema.AlwaysRegular) {
            var style = OneValue(section, "fontSize", 8.9);

            // Strict contract check: an error.
            Check(StyleValidator.Validate(style).Contains($"style.{section}.fontSize must be >= 9 pt"),
                  section + " 8.9 pt is an error: " + Join(StyleValidator.Validate(style)));

            // Capture path: clamped up to the floor and reported.
            var normalized = StyleNormalizer.Normalize(style);
            Equal(9.0, normalized.Style.Section(section).FontSize, section + " 8.9 pt is raised to 9");
            Check(normalized.Warnings.Any(w => w.Contains($"style.{section}.fontSize")), section + " the correction is reported");
        }
    }

    static void GlobalFontAndSpacingRulesUnchanged() {
        // The general 8–24 pt range still applies to every other section.
        var small = OneValue("name", "fontSize", 7.5);
        Check(StyleValidator.Validate(small).Contains("style.name.fontSize must be >= 8 pt"), "7.5 pt name is an error");
        Equal(8.0, StyleNormalizer.Normalize(small).Style.Name.FontSize, "7.5 pt name is raised to 8");

        var big = OneValue("body", "fontSize", 30);
        Check(StyleValidator.Validate(big).Contains("style.body.fontSize must be <= 24 pt"), "30 pt body is an error");

        // Metadata may be smaller than the body standard, within the general range.
        Equal(0, StyleValidator.Validate(OneValue("metadata", "fontSize", 8.7)).Count, "8.7 pt metadata is valid");

        // lineSpacing below 1.0 is still refused / raised.
        var tight = OneValue("body", "lineSpacing", 0.9);
        Check(StyleValidator.Validate(tight).Contains("style.body.lineSpacing must be >= 1"), "0.9 line spacing is an error");
        Equal(1.0, StyleNormalizer.Normalize(tight).Style.Body.LineSpacing, "0.9 line spacing is raised to 1.0");

        // The weight rule is unchanged: bold body text is still refused.
        var bold = new JsonObject { ["body"] = new JsonObject { ["fontSize"] = 9, ["bold"] = true } };
        Check(StyleValidator.Validate(bold).Any(e => e.StartsWith("style.body.bold must be false")), "bold body is still an error");
        Check(!StyleNormalizer.Normalize(bold).Style.Body.Bold, "bold body is still turned off");
    }

    static void FlatStylePropertiesStayUnsupported() {
        var flat = JsonNode.Parse("""
            { "bodyFontSize": 9, "nameFontSize": 17, "fontFamily": "Arial", "marginTop": 0.45, "metadataFontSize": 8.7 }
            """);

        var errors = StyleValidator.Validate(flat);
        foreach (var key in new[] { "bodyFontSize", "nameFontSize", "fontFamily", "marginTop", "metadataFontSize" })
            Check(errors.Contains($"style.{key} is not a supported style property"), key + " is rejected: " + Join(errors));

        // The capture path ignores them — so the body keeps the preset's 11 pt, NOT the 9 it asked for.
        var normalized = StyleNormalizer.Normalize(flat);
        Equal(11.0, normalized.Style.Body.FontSize, "a flat bodyFontSize does not reach the body");
        Equal(5, normalized.Warnings.Count(w => w.Contains("is not a supported style property")), "each flat key is reported");

        // Unrelated unknown keys stay rejected as before.
        var odd = JsonNode.Parse("""{ "sidebar": {}, "body": { "fontSize": 9, "dropShadow": true } }""");
        var oddErrors = StyleValidator.Validate(odd);
        Check(oddErrors.Contains("style.sidebar is not a supported style property"), "unknown section rejected");
        Check(oddErrors.Contains("style.body.dropShadow is not a supported style property on body"), "unknown key rejected");
    }

    static void NinePointSurvivesSaveAndReachesTheModel() {
        var target = Path.Combine(NewDir("nine-point-save"), "result.json");
        var report = CandidateProfileStore.NormalizeAndSaveTo(NinePointAnswer(), target, requireStyle: true);

        Check(report.StyleSupplied, "the report records that the answer carried a style");
        Check(!report.Changes.Any(c => c.Contains("not a supported", StringComparison.Ordinal)),
              "a valid nested style produces no unsupported-property warning: " + Join(report.Changes));

        // B. The saved results JSON.
        var saved = JsonNode.Parse(File.ReadAllText(target))!["style"]!;
        foreach (var section in StyleSchema.AlwaysRegular)
            Equal(9.0, saved[section]!["fontSize"]!.GetValue<double>(), "saved style." + section + ".fontSize");
        Equal(8.7, saved["metadata"]!["fontSize"]!.GetValue<double>(), "saved style.metadata.fontSize");
        Equal("Arial", saved["fonts"]!["family"]!.GetValue<string>(), "saved font");

        // It is still a valid profile under the strict check.
        Equal(0, CandidateProfileStore.ValidateResumeJson(File.ReadAllText(target)).Count, "the saved profile is valid");

        // C. The renderer input.
        var document = ResumeDocument.FromProfileFile(target);
        Equal(9.0, document.Style.Body.FontSize, "ResumeDocument body");
        Equal(9.0, document.Style.Bullet.FontSize, "ResumeDocument bullet");
        Equal(9.0, document.Style.SkillValues.FontSize, "ResumeDocument skillValues");
        Equal(9.0, document.Style.Education.FontSize, "ResumeDocument education");
        Equal(8.7, document.Style.Metadata.FontSize, "ResumeDocument metadata");
        Equal(0, document.StyleWarnings.Count, "no correction at render time: " + Join(document.StyleWarnings));

        Equal("STYLE accepted preset=promV4.12 body=9 bullet=9 skills=9 education=9 font=Arial",
              ResultCapture.StyleLogLine(report.Profile), "the diagnostics line");
    }

    static string NinePointProfileFile(string label) {
        var target = Path.Combine(NewDir(label), "result.json");
        CandidateProfileStore.NormalizeAndSaveTo(NinePointAnswer(), target, requireStyle: true);
        return target;
    }

    static void NinePointReachesTheDocx() {
        var paragraphs = ReadDocx(RenderFile(NinePointProfileFile("nine-point-docx"), "nine-point"));
        var basic = ResumeDocument.FromProfileFile(Fixture("resume-basic.json"));

        // D. Summary, every bullet, the skill values and both education lines are 9 pt.
        Equal(9.0, paragraphs.Single(p => p.Text == basic.Summary).FontSize, "DOCX summary");

        var bullets = paragraphs.Where(p => p.Text.StartsWith(ResumeDocument.Bullet, StringComparison.Ordinal)).ToList();
        Equal(3, bullets.Count, "every experience bullet was found");
        foreach (var bullet in bullets) Equal(9.0, bullet.FontSize, "DOCX bullet: " + bullet.Text);

        foreach (var skill in basic.Skills)
            Equal(9.0, paragraphs.Single(p => p.Text == skill.Skills).FontSize, "DOCX skill values: " + skill.Skills);

        var education = basic.Education.Single();
        Equal(9.0, paragraphs.Single(p => p.Text == education.Heading).FontSize, "DOCX education heading");
        Equal(9.0, paragraphs.Single(p => p.Text == education.Dates).FontSize, "DOCX education dates");

        // F. Metadata keeps its own size and does not leak into the body. Word stores a font size as
        // whole HALF-points (w:sz), so the requested 8.7 pt is written as 8.5 pt in the DOCX; the PDF
        // keeps 8.7 (see NinePointReachesThePdfModel). 9 pt, being a whole half-point, is exact in both.
        Equal(8.5, paragraphs.Single(p => p.Text == "Peoria, IL").FontSize, "DOCX metadata (8.7 in half-points)");
        Equal(11.5, paragraphs.First(p => p.Text == basic.Skills[0].Category).FontSize, "DOCX skill category keeps its preset size");
        Equal(12.5, paragraphs.First(p => p.Text == ResumeDocument.SummaryHeading.ToUpperInvariant()).FontSize, "DOCX section heading");
        Check(paragraphs.All(p => p.FontSize >= 9.0 || p.Text == "Peoria, IL"), "only the metadata line is below 9 pt");
        Check(paragraphs.All(p => p.FontFamily == "Arial"), "every run is Arial");
    }

    /// <summary>Every paragraph of a MigraDoc model as (text, the font size of each run).</summary>
    static List<(string Text, List<double> Sizes)> PdfRuns(MigraDoc.DocumentObjectModel.Document document) {
        var result = new List<(string, List<double>)>();
        foreach (var sectionObject in document.Sections) {
            if (sectionObject is not MigraDoc.DocumentObjectModel.Section section) continue;
            foreach (var element in section.Elements) {
                if (element is not MigraDoc.DocumentObjectModel.Paragraph paragraph) continue;
                var text = "";
                var sizes = new List<double>();
                foreach (var part in paragraph.Elements) {
                    if (part is not MigraDoc.DocumentObjectModel.FormattedText formatted) continue;
                    sizes.Add(formatted.Font.Size.Point);
                    foreach (var inner in formatted.Elements)
                        if (inner is MigraDoc.DocumentObjectModel.Text t) text += t.Content;
                }
                result.Add((text.Trim(), sizes));
            }
        }
        return result;
    }

    static void NinePointReachesThePdfModel() {
        // E. The PDF is built directly from the same ResumeDocument; inspect its model before rendering.
        var document = ResumeDocument.FromProfileFile(NinePointProfileFile("nine-point-pdf"));
        var runs = PdfRuns(PdfWriter.BuildDocument(document));

        // MigraDoc keeps a Unit as a float, so compare to 0.01 pt.
        double Only(string text, string what) {
            var sizes = runs.Single(r => r.Text == text).Sizes.Select(s => Math.Round(s, 2)).Distinct().ToList();
            Equal(1, sizes.Count, what + " has one size");
            return sizes[0];
        }

        Equal(9.0, Only(document.Summary, "PDF summary"), "PDF summary");
        foreach (var skill in document.Skills) Equal(9.0, Only(skill.Skills, "PDF skills"), "PDF skill values");
        foreach (var bullet in runs.Where(r => r.Text.StartsWith(ResumeDocument.Bullet.Trim(), StringComparison.Ordinal)))
            Check(bullet.Sizes.All(s => Math.Round(s, 2) == 9.0), "PDF bullet is 9 pt: " + bullet.Text);
        Equal(3, runs.Count(r => r.Text.StartsWith(ResumeDocument.Bullet.Trim(), StringComparison.Ordinal)), "PDF bullets found");

        var education = document.Education.Single();
        Equal(9.0, Only(education.Heading, "PDF education heading"), "PDF education heading");
        Equal(9.0, Only(education.Dates, "PDF education dates"), "PDF education dates");

        // F. Metadata keeps 8.7 pt in the PDF too.
        Equal(8.7, Only("Peoria, IL", "PDF metadata"), "PDF metadata");

        // And the whole pipeline produces both files from it without a single style correction.
        var settings = new AppSettings { ResumeRootFolder = NewDir("nine-point-generate"), Docx = true, Pdf = true };
        var generated = ResumeGenerator.Generate("Caterpillar Inc.", "Senior AI Software Engineer",
                                                 NinePointProfileFile("nine-point-generate-input"), settings);
        Check(generated.DocxGenerated && generated.PdfGenerated, "both documents generated: " + generated.Describe());
        Equal(0, generated.StyleWarnings.Count, "no style value was adjusted: " + Join(generated.StyleWarnings));
    }

    static void PresetDefaultsUnchanged() {
        // Resume mode's look does not move: the presets still use the old sizes.
        var prom = StylePresets.PromV412();
        Equal(11.0, prom.Body.FontSize, "promV4.12 body");
        Equal(11.0, prom.Bullet.FontSize, "promV4.12 bullet");
        Equal(10.5, prom.SkillValues.FontSize, "promV4.12 skill values");
        Equal(11.0, prom.Education.FontSize, "promV4.12 education");
        Equal(11.0, StylePresets.Classic().SkillValues.FontSize, "classic skill values");
        Equal(10.5, StylePresets.Compact().SkillValues.FontSize, "compact skill values");
    }

    static void NormalModeRequiresStyle() {
        var dir = NewDir("normal-requires-style");

        // Present: accepted.
        var withStyle = Path.Combine(dir, "with.json");
        CandidateProfileStore.NormalizeAndSaveTo(NinePointAnswer(), withStyle, requireStyle: true);
        Check(File.Exists(withStyle), "a Normal answer with style is saved");

        // Missing: refused with the fixed reason, and NOTHING is written.
        var without = Path.Combine(dir, "without.json");
        try {
            CandidateProfileStore.NormalizeAndSaveTo(NinePointAnswer(includeStyle: false), without, requireStyle: true);
            throw new Exception("a Normal answer without style must be refused");
        } catch (InvalidDataException ex) {
            Equal(CandidateProfileStore.MissingStyleMessage, ex.Message, "the refusal reason");
        }
        Check(!File.Exists(without), "nothing was saved for the refused answer");

        // A style that is not an object is not a style.
        var scalar = Path.Combine(dir, "scalar.json");
        try {
            var root = JsonNode.Parse(File.ReadAllText(Fixture("resume-basic.json")))!.AsObject();
            root["style"] = "compact";
            CandidateProfileStore.NormalizeAndSaveTo("```json\n" + root.ToJsonString() + "\n```", scalar, requireStyle: true);
            throw new Exception("a string style must be refused in Normal mode");
        } catch (InvalidDataException ex) {
            Equal(CandidateProfileStore.MissingStyleMessage, ex.Message, "string style refusal");
        }
        Check(!File.Exists(scalar), "nothing was saved for the string style");

        // A tolerated alias the normalizer already renames still counts as a style object.
        var alias = Path.Combine(dir, "alias.json");
        CandidateProfileStore.NormalizeAndSaveTo(NinePointAnswer(styleKey: "formatting"), alias, requireStyle: true);
        Equal(9.0, JsonNode.Parse(File.ReadAllText(alias))!["style"]!["body"]!["fontSize"]!.GetValue<double>(), "alias style applied");

        // The capture boundary: Accept reports it as an unsaved answer, which MainWindow sends down
        // the existing InvalidOutput retry path (attempt 1 of 3 -> Retry).
        var jobId = "RB-TEST-STYLE-" + Guid.NewGuid().ToString("N")[..8];
        try {
            var refused = ResultCapture.Accept(NinePointAnswer(includeStyle: false), jobId, requireStyle: true);
            Check(!refused.Saved, "Accept does not save it");
            Equal(CandidateProfileStore.MissingStyleMessage, refused.Error, "Accept carries the reason");
            Check(!File.Exists(ProfileResultStore.ResultPath(jobId)), "no results json for the job");
            Equal(AttemptDecision.Retry, GptAttempts.Decide(1), "the first invalid output is retried");

            var accepted = ResultCapture.Accept(NinePointAnswer(), jobId, requireStyle: true);
            Check(accepted.Saved, "the same job with a style is accepted: " + accepted.Error);
        } finally {
            foreach (var path in new[] { ProfileResultStore.ResultPath(jobId), ProfileResultStore.RawPath(jobId) })
                if (File.Exists(path)) File.Delete(path);
        }
    }

    static void ResumeModeStyleStaysOptional() {
        // Default parameter = Resume mode's behaviour: a style-less answer is saved and uses promV4.12.
        var target = Path.Combine(NewDir("resume-mode-no-style"), "result.json");
        var report = CandidateProfileStore.NormalizeAndSaveTo(NinePointAnswer(includeStyle: false), target);
        Check(!report.StyleSupplied, "no style was supplied");
        Check(File.Exists(target), "the answer is saved");

        var document = ResumeDocument.FromProfileFile(target);
        Equal("promV4.12", document.Style.Preset, "the preset fallback");
        Equal(11.0, document.Style.Body.FontSize, "the preset body size, unchanged");
        Equal(10.5, document.Style.SkillValues.FontSize, "the preset skill values size, unchanged");

        // Same through Accept, exactly as MainWindow calls it for a Resume-mode request.
        var jobId = "RB-TEST-STYLE-" + Guid.NewGuid().ToString("N")[..8];
        try {
            var accepted = ResultCapture.Accept(NinePointAnswer(includeStyle: false), jobId, requireStyle: false);
            Check(accepted.Saved, "Resume mode accepts a style-less answer: " + accepted.Error);
        } finally {
            foreach (var path in new[] { ProfileResultStore.ResultPath(jobId), ProfileResultStore.RawPath(jobId) })
                if (File.Exists(path)) File.Delete(path);
        }
    }

    static void PreparedRequestRecordsItsMode() => WithPreparedFiles(() => {
        var resume = RequestPreparation.Prepare(PromptJob(), PromptSettings(PromptModes.Resume));
        Equal(PromptModes.Resume, resume.PromptMode, "a Resume request says so");
        Check(!PromptModes.IsNormal(RequestPreparation.Load()!.PromptMode), "and reloads as Resume");

        var normal = RequestPreparation.Prepare(PromptJob(), PromptSettings(PromptModes.Normal, NormalPromptFile("My prompt.")));
        Equal(PromptModes.Normal, normal.PromptMode, "a Normal request says so");
        Check(PromptModes.IsNormal(RequestPreparation.Load()!.PromptMode), "and reloads as Normal");

        // A prepared-request.json written before the field existed reads as Resume: style stays optional.
        File.WriteAllText(RequestPreparation.PreparedPath, """{ "JobId": "OLD-1", "Company": "A", "Title": "B", "Text": "x" }""");
        Check(!PromptModes.IsNormal(RequestPreparation.Load()!.PromptMode), "an older file reads as Resume");
    });

    static void NormalContractDescribesTheRealSchema() {
        var contract = PromptContract.StyleContract();

        // Every section and every section-specific key the code accepts is named.
        foreach (var section in StyleSchema.Sections) Check(contract.Contains(section, StringComparison.Ordinal), "names " + section);
        foreach (var key in StyleSchema.Common) Check(contract.Contains(key, StringComparison.Ordinal), "names " + key);
        foreach (var section in StyleSchema.Sections)
            foreach (var key in StyleSchema.Allowed(section).Except(StyleSchema.Common))
                Check(contract.Contains($"{section} also accepts:", StringComparison.Ordinal) &&
                      contract.Split("\r\n").Single(l => l.Contains($"{section} also accepts:", StringComparison.Ordinal)).Contains(key),
                      $"{section} lists {key}");
        foreach (var font in StyleLimits.FontFamilies) Check(contract.Contains(font, StringComparison.Ordinal), "names font " + font);
        foreach (var preset in StylePresets.Names) Check(contract.Contains(preset, StringComparison.Ordinal), "names preset " + preset);

        // The required checks.
        foreach (var rule in new[] { "style is present", "style is an object", "body.fontSize = 9", "bullet.fontSize = 9",
                                     "skillValues.fontSize = 9", "education.fontSize = 9", "every lineSpacing is >= 1.0",
                                     "no flat style fields" })
            Check(contract.Contains(rule, StringComparison.Ordinal), "the contract checks: " + rule);

        // The minimal example it gives is itself valid, needs no correction, and hits 9 pt.
        var exampleLine = contract.Split("\r\n").Single(l => l.StartsWith("Minimal valid style:", StringComparison.Ordinal));
        var exampleJson = "{" + exampleLine[exampleLine.IndexOf("\"style\"", StringComparison.Ordinal)..] + "}";
        var example = JsonNode.Parse(exampleJson)!["style"];
        Equal(0, StyleValidator.Validate(example).Count, "the contract's example is valid: " + Join(StyleValidator.Validate(example)));
        var normalized = StyleNormalizer.Normalize(example);
        Equal(0, normalized.Warnings.Count, "the contract's example needs no correction");
        foreach (var section in StyleSchema.AlwaysRegular)
            Equal(9.0, normalized.Style.Section(section).FontSize, "the example sets " + section + " to 9");

        // Flat names appear only in the "do not exist" line, never as something to send.
        foreach (var line in contract.Split("\r\n").Where(l => l.Contains("bodyFontSize", StringComparison.Ordinal)))
            Check(line.Contains("do not exist", StringComparison.Ordinal), "bodyFontSize is only ever named as unsupported");
    }

    // ================= job import filter =================
    //
    // The rules are pure, so most of this needs no files at all: a JobImportData and an AppSettings
    // go in, a decision comes out. The persistence and integration groups below use the real
    // settings.json and tasks.json, each restored byte-for-byte afterwards.

    /// <summary>All five filters on.</summary>
    static AppSettings AllFilters() {
        var s = JobImportFilter.NoFilters();
        foreach (var f in JobImportFilter.Switches) f.Set(s, true);
        return s;
    }

    /// <summary>The shipped defaults: LinkedIn, clearance and citizenship on; export and visa off.</summary>
    static AppSettings DefaultFilters() => new();

    /// <summary>Exactly one filter on, so a rule can be proved on its own.</summary>
    static AppSettings OnlyFilter(JobFilterReason reason) {
        var s = JobImportFilter.NoFilters();
        JobImportFilter.Switches.Single(f => f.Reason == reason).Set(s, true);
        return s;
    }

    static JobImportData FilterJob(string description, string? applyUrl = null,
                                   string title = "AI Engineer",
                                   string jobUrl = "https://jobright.ai/jobs/info/aaaaaaaaaaaaaaaaaaaaaaaa") =>
        new() {
            Company = "Example Co",
            Title = title,
            JobUrl = jobUrl,
            Description = description,
            ApplyUrl = applyUrl
        };

    /// <summary>Asserts the job is refused, and refused for exactly this reason.</summary>
    static void Rejects(JobFilterReason reason, AppSettings settings, string description,
                        string? applyUrl = null, string? note = null, string title = "AI Engineer") {
        var decision = JobImportFilter.Evaluate(FilterJob(description, applyUrl, title), settings);
        Check(!decision.Accepted, $"expected a rejection for {note ?? description}");
        Equal(reason, decision.Reason, "reason for " + (note ?? description));
    }

    /// <summary>Asserts the job is imported — the false-positive guard.</summary>
    static void Accepts(AppSettings settings, string description, string? applyUrl = null,
                        string? note = null, string title = "AI Engineer") {
        var decision = JobImportFilter.Evaluate(FilterJob(description, applyUrl, title), settings);
        Check(decision.Accepted,
              $"expected acceptance for {note ?? description} but it was refused as {decision.Reason}");
    }

    // ---------- rule: LinkedIn application platform ----------

    static void FilterLinkedInPlatform() {
        var on = OnlyFilter(JobFilterReason.LinkedInPlatform);

        Rejects(JobFilterReason.LinkedInPlatform, on, "Build models.",
                "https://www.linkedin.com/jobs/view/4012345678", "a LinkedIn job posting");
        Rejects(JobFilterReason.LinkedInPlatform, on, "Build models.",
                "https://linkedin.com/jobs/collections/recommended/?currentJobId=41", "linkedin.com/jobs");

        // The same job with the switch off imports.
        Accepts(JobImportFilter.NoFilters(), "Build models.",
                "https://www.linkedin.com/jobs/view/4012345678", "LinkedIn with the filter off");

        // Another ATS is never LinkedIn.
        Accepts(on, "Build models.", "https://boards.greenhouse.io/acme/jobs/7", "Greenhouse apply link");
    }

    static void FilterLinkedInIsAboutTheDestinationOnly() {
        var on = OnlyFilter(JobFilterReason.LinkedInPlatform);

        // Text mentioning LinkedIn is not an application destination.
        Accepts(on, "Share your LinkedIn profile. LinkedIn Learning budget provided. Follow us on LinkedIn.",
                "https://boards.greenhouse.io/acme/jobs/7", "LinkedIn mentioned in the description");
        Accepts(on, "We use LinkedIn Recruiter.", null, "LinkedIn in the text, no apply link");

        // No address at all is Unknown, and Unknown is never LinkedIn.
        foreach (var missing in new string?[] { null, "", "   ", "not a url", "javascript:alert(1)" })
            Accepts(on, "Build models.", missing, $"missing apply url '{missing ?? "null"}'");

        // A LinkedIn profile or company page is not an application platform either.
        Accepts(on, "Build models.", "https://www.linkedin.com/company/acme", "LinkedIn company page");
        Accepts(on, "Build models.", "https://www.linkedin.com/in/someone", "LinkedIn profile");

        Check(JobImportFilter.IsLinkedInApplication("https://www.linkedin.com/jobs/view/1"), "jobs path is LinkedIn");
        Check(!JobImportFilter.IsLinkedInApplication(null), "no address is never LinkedIn");
    }

    // ---------- rule: security clearance ----------

    static void FilterSecurityClearanceRequirements() {
        var on = OnlyFilter(JobFilterReason.SecurityClearance);

        string[] required = {
            "Security clearance required.",
            "Must have security clearance.",
            "Candidates need an active security clearance.",
            "Active Secret clearance required for this role.",
            "Secret clearance required.",
            "Top Secret clearance required.",
            "Applicants must hold a Top Secret/SCI clearance.",
            "TS/SCI required.",
            "SCI clearance required.",
            "DoD clearance required.",
            "You must possess a clearance before starting.",
            "The engineer must obtain a clearance within 90 days.",
            "Must maintain a clearance throughout employment.",
            "Must obtain and maintain a security clearance.",
            "Must be eligible for a security clearance.",
            "Ability to obtain a security clearance is required.",
            "Ability to obtain and maintain a security clearance.",
            "Public Trust clearance required.",
            "Must be eligible for Public Trust."
        };

        foreach (var text in required)
            Rejects(JobFilterReason.SecurityClearance, on, text);

        // Case, punctuation and hyphenation must not change the answer.
        Rejects(JobFilterReason.SecurityClearance, on, "ACTIVE TOP-SECRET / SCI CLEARANCE REQUIRED", note: "upper case, hyphens");
        Rejects(JobFilterReason.SecurityClearance, on, "Requirements:\n• Active Secret clearance\n• 5 years of Python", note: "a bullet list");

        // With the switch off the same text imports.
        Accepts(JobImportFilter.NoFilters(), "TS/SCI required.", note: "clearance with the filter off");
    }

    static void FilterSecurityIsNotClearance() {
        var on = OnlyFilter(JobFilterReason.SecurityClearance);

        string[] ordinary = {
            "Follow security best practices.",
            "5+ years of cybersecurity experience.",
            "Work on AI security and governance.",
            "Cloud security certification preferred.",
            "Certified in AWS.",
            "Design secure APIs and secure systems.",
            "Application security engineering and security monitoring.",
            "Security compliance, security standards and security governance.",
            "Experience with DevSecOps, FedRAMP, NIST and FISMA.",
            "You will partner with cybersecurity teams.",
            "Red teaming experience is a plus.",
            "Own security/privacy reviews for new features."
        };

        foreach (var text in ordinary)
            Accepts(on, text);

        // The real postings that must keep importing.
        Accepts(on, "Pacific Gas and Electric Company is building secure, compliant AI systems for a " +
                    "highly regulated industry. You will work with Cybersecurity stakeholders to ensure " +
                    "data protection and regulatory compliance across the enterprise.", note: "PG&E");

        Accepts(on, "Certified in at least one Cloud or AI-related Certification. Experience with " +
                    "enterprise security and governance frameworks is required.", note: "Ansell (certification)");

        Accepts(on, "SRE - Enterprise & Cloud Security - AI Driven Security. You will build AI driven " +
                    "security tooling and hold cloud security certifications.", note: "PwC (cybersecurity work)",
                title: "SRE - Enterprise & Cloud Security - AI Driven Security - Manager");

        Accepts(on, "Build secure backend services and follow security best practices across the " +
                    "storage platform.", note: "Pure Storage / Everpure");

        Accepts(on, "Sentry helps developers find and fix errors. You will build the AI features of our " +
                    "error monitoring and application security products.", note: "a security product company");

        // A job that says the opposite must never be read as a requirement.
        Accepts(on, "A security clearance is not required for this position.", note: "clearance explicitly not required");
        Accepts(on, "No clearance needed.", note: "no clearance needed");
    }

    // ---------- rule: citizenship / Public Trust ----------

    static void FilterCitizenshipRequirements() {
        var on = OnlyFilter(JobFilterReason.CitizenshipRequirement);

        string[] restricted = {
            "U.S. citizenship required.",
            "US citizenship required.",
            "Must be a U.S. citizen.",
            "Must be US citizen.",
            "U.S. citizens only.",
            "US citizens only.",
            "This role requires United States citizenship.",
            "Applicants must be American citizens."
        };

        foreach (var text in restricted)
            Rejects(JobFilterReason.CitizenshipRequirement, on, text);

        Accepts(JobImportFilter.NoFilters(), "US citizenship required.", note: "citizenship with the filter off");
    }

    static void FilterWorkAuthorizationIsNotCitizenship() {
        var on = OnlyFilter(JobFilterReason.CitizenshipRequirement);

        string[] fine = {
            "You must be authorized to work in the United States.",
            "Valid authorization to work in the U.S. is required.",
            "Must have valid work authorization.",
            "Candidates must be authorized to work in the US without restriction."
        };

        foreach (var text in fine)
            Accepts(on, text);

        // Equal-opportunity boilerplate names citizenship as a protected class, not a requirement.
        Accepts(on, "We are an equal opportunity employer and consider all applicants regardless of race, " +
                    "religion, national origin, citizenship status or veteran status, as required by law.",
                note: "EEO boilerplate");

        Accepts(on, "U.S. citizenship is not required for this role.", note: "citizenship explicitly not required");
    }

    static void FilterCitizenshipAndClearancePrecedence() {
        // The documented precedence is JobImportFilter.Switches order: clearance before citizenship.
        const string both = "US Citizenship required; must be eligible for a Public Trust clearance.";

        Rejects(JobFilterReason.SecurityClearance, DefaultFilters(), both, note: "both rules on -> clearance wins");
        Rejects(JobFilterReason.SecurityClearance, AllFilters(), both, note: "all rules on -> clearance wins");

        // With clearance off, the citizenship rule still catches the same posting.
        Rejects(JobFilterReason.CitizenshipRequirement, OnlyFilter(JobFilterReason.CitizenshipRequirement), both,
                note: "clearance off -> citizenship");

        // And the precedence is the declared switch order, not an accident of the code.
        Equal(JobFilterReason.LinkedInPlatform, JobImportFilter.Switches[0].Reason, "first switch");
        Equal(JobFilterReason.SecurityClearance, JobImportFilter.Switches[1].Reason, "second switch");
        Equal(JobFilterReason.CitizenshipRequirement, JobImportFilter.Switches[2].Reason, "third switch");
        Equal(JobFilterReason.ExportControl, JobImportFilter.Switches[3].Reason, "fourth switch");
        Equal(JobFilterReason.NoVisaSponsorship, JobImportFilter.Switches[4].Reason, "fifth switch");
    }

    // ---------- rule: export control ----------

    static void FilterExportControlRestrictions() {
        var on = OnlyFilter(JobFilterReason.ExportControl);

        const string coreweave =
            "This position requires access to export controlled information. To conform to U.S. " +
            "Government export regulations, applicant must either be a U.S. person as defined by " +
            "22 CFR 120.15, or eligible to obtain the required authorizations from the U.S. Department of State.";

        Rejects(JobFilterReason.ExportControl, on, coreweave, note: "CoreWeave-style export control");

        string[] restricted = {
            "U.S. person required.",
            "US person required.",
            "Must qualify as a U.S. person under export control regulations.",
            "ITAR restricted position.",
            "This is an export-controlled role; applicants must be U.S. persons or permanent residents."
        };

        foreach (var text in restricted)
            Rejects(JobFilterReason.ExportControl, on, text);

        // Default-off means the same postings import until the user turns the switch on.
        Accepts(DefaultFilters(), coreweave, note: "CoreWeave with export control off (default)");
        Accepts(JobImportFilter.NoFilters(), "ITAR restricted position.", note: "ITAR with the filter off");
    }

    static void FilterExportControlLeavesOrdinaryComplianceAlone() {
        var on = OnlyFilter(JobFilterReason.ExportControl);

        string[] ordinary = {
            "Strong security and compliance background.",
            "Experience in a regulated industry.",
            "Data governance and data protection experience.",
            "Partner with legal and compliance teams on governance.",
            "Familiarity with export control regulations is a plus."
        };

        foreach (var text in ordinary)
            Accepts(on, text);
    }

    // ---------- rule: visa sponsorship ----------

    static void FilterNoVisaSponsorship() {
        var on = OnlyFilter(JobFilterReason.NoVisaSponsorship);

        string[] refused = {
            "Visa sponsorship is not available for this position.",
            "No visa sponsorship.",
            "The company does not provide visa sponsorship.",
            "We do not offer sponsorship.",
            "We are unable to provide work visa sponsorship.",
            "Candidates are not eligible for visa sponsorship.",
            "This role is not eligible for Visa transfer or Sponsorship.",
            "Our company does not engage in immigration sponsorship.",
            "We can't sponsor visas for this role."
        };

        foreach (var text in refused)
            Rejects(JobFilterReason.NoVisaSponsorship, on, text);

        Accepts(JobImportFilter.NoFilters(), "Visa sponsorship is not available for this position.",
                note: "no sponsorship with the filter off");
        Accepts(DefaultFilters(), "This role is not eligible for Visa transfer or Sponsorship.",
                note: "AMD-style text with the default switches");
    }

    static void FilterSponsorshipOfferedIsAccepted() {
        var on = OnlyFilter(JobFilterReason.NoVisaSponsorship);

        Accepts(on, "Capital One will consider sponsoring a new qualified applicant for employment " +
                    "authorization for this position.", note: "Capital One offers sponsorship");
        Accepts(on, "Visa sponsorship is available for this role.", note: "sponsorship available");
        Accepts(on, "We sponsor H-1B and green card applications.", note: "sponsorship offered");
        Accepts(on, "Must have valid authorization to work in the U.S.", note: "work authorization only");
    }

    // ---------- the switch set itself ----------

    static void FilterSwitchesAreOneSharedDefinition() {
        Equal(5, JobImportFilter.Switches.Count, "five switches");

        // Every reason but None has exactly one switch, and every switch reads and writes its own field.
        foreach (var reason in Enum.GetValues<JobFilterReason>().Where(r => r != JobFilterReason.None))
            Equal(1, JobImportFilter.Switches.Count(s => s.Reason == reason), "switches for " + reason);

        var settings = JobImportFilter.NoFilters();
        foreach (var descriptor in JobImportFilter.Switches) {
            Check(!descriptor.Get(settings), descriptor.Reason + " starts off");
            descriptor.Set(settings, true);
            Check(descriptor.Get(settings), descriptor.Reason + " reads back what it wrote");

            // Only its own field moved.
            Equal(1, JobImportFilter.Switches.Count(s => s.Get(settings)), "only one switch on after setting " + descriptor.Reason);
            descriptor.Set(settings, false);

            Check(!string.IsNullOrWhiteSpace(descriptor.Label), descriptor.Reason + " has a caption");
            Check(descriptor.Label.StartsWith("Skip ", StringComparison.Ordinal), descriptor.Reason + " caption starts with Skip");
        }

        // Every reason has its own user-facing text, and none of it leaks a pattern.
        foreach (var reason in Enum.GetValues<JobFilterReason>().Where(r => r != JobFilterReason.None)) {
            var text = JobImportFilter.Describe(reason);
            Check(text.StartsWith("Not imported: ", StringComparison.Ordinal), reason + " message shape");
            Equal(reason.ToString(), JobImportFilter.LogReason(reason), "log token for " + reason);
        }

        Equal("Not imported: LinkedIn application platform", JobImportFilter.Describe(JobFilterReason.LinkedInPlatform), "LinkedIn text");
        Equal("Not imported: security clearance required", JobImportFilter.Describe(JobFilterReason.SecurityClearance), "clearance text");
        Equal("Not imported: U.S. citizenship requirement", JobImportFilter.Describe(JobFilterReason.CitizenshipRequirement), "citizenship text");
        Equal("Not imported: export-control restriction", JobImportFilter.Describe(JobFilterReason.ExportControl), "export text");
        Equal("Not imported: visa sponsorship unavailable", JobImportFilter.Describe(JobFilterReason.NoVisaSponsorship), "visa text");
    }

    static void FilterAllOffAcceptsEverything() {
        var off = JobImportFilter.NoFilters();

        string[] wouldReject = {
            "Active TS/SCI clearance required.",
            "US citizenship required.",
            "ITAR restricted position; must be a U.S. person.",
            "Visa sponsorship is not available."
        };

        foreach (var text in wouldReject)
            Accepts(off, text, "https://www.linkedin.com/jobs/view/4012345678", "all off: " + text);

        Equal(0, JobImportFilter.EnabledCount(off), "no switches on");
        Equal("Filters (0)", JobImportFilter.ButtonText(off), "button text with none on");

        // A job with nothing to read is never refused.
        Check(JobImportFilter.Evaluate(new JobImportData(), AllFilters()).Accepted, "an empty job is accepted");
        Check(JobImportFilter.Evaluate(null, AllFilters()).Accepted, "no data is accepted");
        Check(JobImportFilter.Evaluate(FilterJob("x"), null).Accepted, "no settings is accepted");
    }

    // ---------- persistence ----------

    /// <summary>Runs a test against the real settings.json and restores its exact bytes afterwards.</summary>
    static void WithSettingsFileRestored(Action body) => WithLiveStateFile(Storage.SettingsPath, body);

    /// <summary>
    /// Runs a body that writes a LIVE state file and puts everything back exactly: the file's bytes,
    /// its ".bak" (atomic saves create one since Phase 1), any "corrupt-*" copy the test caused, a
    /// stray ".tmp", and the per-session write refusal — so no test leaves a trace in the user's data.
    /// </summary>
    static void WithLiveStateFile(string live, Action body) {
        var bak = live + ".bak";
        var dir = Path.GetDirectoryName(live)!;
        var pattern = Path.GetFileNameWithoutExtension(live) + ".corrupt-*" + Path.GetExtension(live);
        byte[]? Snap(string p) => File.Exists(p) ? File.ReadAllBytes(p) : null;
        var main = Snap(live);
        var backup = Snap(bak);
        var kept = Directory.Exists(dir) ? Directory.GetFiles(dir, pattern).ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string>();

        try { body(); }
        finally {
            void Put(string p, byte[]? b) { if (b is not null) File.WriteAllBytes(p, b); else if (File.Exists(p)) File.Delete(p); }
            Put(live, main);
            Put(bak, backup);
            if (File.Exists(live + ".tmp")) File.Delete(live + ".tmp");
            if (Directory.Exists(dir))
                foreach (var copy in Directory.GetFiles(dir, pattern))
                    if (!kept.Contains(copy)) File.Delete(copy);
            Storage.ClearWriteBlock(live);
        }
    }

    static void FilterSettingsDefaults() {
        var fresh = new AppSettings();

        Check(fresh.SkipLinkedInApply, "SkipLinkedInApply defaults on");
        Check(fresh.SkipSecurityClearance, "SkipSecurityClearance defaults on");
        Check(fresh.SkipCitizenshipRequirement, "SkipCitizenshipRequirement defaults on");
        Check(!fresh.SkipExportControl, "SkipExportControl defaults off");
        Check(!fresh.SkipNoVisaSponsorship, "SkipNoVisaSponsorship defaults off");

        Equal(3, JobImportFilter.EnabledCount(fresh), "three filters on by default");
        Equal("Filters (3)", JobImportFilter.ButtonText(fresh), "the default button caption");

        var withExport = new AppSettings { SkipExportControl = true };
        Equal("Filters (4)", JobImportFilter.ButtonText(withExport), "export control makes it four");

        Equal("Filters (5)", JobImportFilter.ButtonText(AllFilters()), "all five on");
    }

    static void FilterSettingsOldFileLoadsWithDefaults() => WithSettingsFileRestored(() => {
        // Exactly the shape settings.json had before the filters existed.
        Directory.CreateDirectory(Storage.DataDir);
        File.WriteAllText(Storage.SettingsPath, """
        {
          "OriginalResume": "C:\\resume.docx",
          "MasterPrompt": "C:\\prompt.txt",
          "PromptMode": "Normal",
          "NormalPrompt": "C:\\normal.md",
          "IncomingFolder": "C:\\in",
          "ImportedFolder": "C:\\done",
          "ResumeRootFolder": "C:\\resumes",
          "Docx": true, "Pdf": true, "AutoSend": true, "FocusHotkey": false
        }
        """);

        var loaded = Storage.LoadSettings();

        Check(loaded.SkipLinkedInApply, "missing SkipLinkedInApply loads as on");
        Check(loaded.SkipSecurityClearance, "missing SkipSecurityClearance loads as on");
        Check(loaded.SkipCitizenshipRequirement, "missing SkipCitizenshipRequirement loads as on");
        Check(!loaded.SkipExportControl, "missing SkipExportControl loads as off");
        Check(!loaded.SkipNoVisaSponsorship, "missing SkipNoVisaSponsorship loads as off");
        Equal(3, JobImportFilter.EnabledCount(loaded), "an old file shows Filters (3)");

        // The settings that WERE in the file are untouched.
        Equal("C:\\normal.md", loaded.NormalPrompt, "NormalPrompt kept");
        Equal(PromptModes.Normal, loaded.PromptMode, "PromptMode kept");
        Check(!loaded.FocusHotkey, "an explicit false is kept");
    });

    static void FilterSettingsSurviveSaveAndReload() => WithSettingsFileRestored(() => {
        var saved = new AppSettings {
            OriginalResume = "C:\\me.docx", MasterPrompt = "C:\\p.txt", NormalPrompt = "C:\\n.md",
            PromptMode = PromptModes.Normal, IncomingFolder = "C:\\in", ImportedFolder = "C:\\done",
            ResumeRootFolder = "C:\\resumes", Docx = false, Pdf = true, AutoSend = false, ReadySound = false,
            SkipLinkedInApply = false, SkipSecurityClearance = true,
            SkipCitizenshipRequirement = false, SkipExportControl = true, SkipNoVisaSponsorship = true
        };
        Storage.SaveSettings(saved);

        var loaded = Storage.LoadSettings();
        Check(!loaded.SkipLinkedInApply, "LinkedIn off survived");
        Check(loaded.SkipSecurityClearance, "clearance on survived");
        Check(!loaded.SkipCitizenshipRequirement, "citizenship off survived");
        Check(loaded.SkipExportControl, "export on survived");
        Check(loaded.SkipNoVisaSponsorship, "visa on survived");
        Equal(3, JobImportFilter.EnabledCount(loaded), "count after reload");

        // The five fields really are in the file, by name.
        var json = File.ReadAllText(Storage.SettingsPath);
        foreach (var field in new[] { "SkipLinkedInApply", "SkipSecurityClearance", "SkipCitizenshipRequirement",
                                      "SkipExportControl", "SkipNoVisaSponsorship" })
            Check(json.Contains(field, StringComparison.Ordinal), field + " is written to settings.json");
    });

    static void FilterToggleKeepsUnrelatedSettings() => WithSettingsFileRestored(() => {
        // What the toolbar does on one tick: load, set one field, save.
        var original = new AppSettings {
            OriginalResume = "C:\\me.docx", CandidateProfile = "C:\\profile.json", MasterPrompt = "C:\\p.txt",
            PromptMode = PromptModes.Normal, NormalPrompt = "C:\\n.md",
            IncomingFolder = "C:\\in", ImportedFolder = "C:\\done", ResumeRootFolder = "C:\\resumes",
            Docx = false, Pdf = true, AutoFillComposer = false, AutoCaptureResult = false, AutoSend = false,
            ReadyToast = false, ReadySound = false, ReadyFlash = false, FocusHotkey = false
        };
        Storage.SaveSettings(original);

        foreach (var descriptor in JobImportFilter.Switches) {
            var settings = Storage.LoadSettings();
            descriptor.Set(settings, !descriptor.Get(settings));
            Storage.SaveSettings(settings);
        }

        var after = Storage.LoadSettings();

        Equal(original.OriginalResume, after.OriginalResume, "resume path kept");
        Equal(original.CandidateProfile, after.CandidateProfile, "candidate profile kept");
        Equal(original.MasterPrompt, after.MasterPrompt, "master prompt kept");
        Equal(original.NormalPrompt, after.NormalPrompt, "normal prompt kept");
        Equal(original.PromptMode, after.PromptMode, "prompt mode kept");
        Equal(original.IncomingFolder, after.IncomingFolder, "incoming folder kept");
        Equal(original.ImportedFolder, after.ImportedFolder, "imported folder kept");
        Equal(original.ResumeRootFolder, after.ResumeRootFolder, "resume root kept");
        Equal(original.Docx, after.Docx, "docx kept");
        Equal(original.Pdf, after.Pdf, "pdf kept");
        Equal(original.AutoSend, after.AutoSend, "auto send kept");
        Equal(original.ReadyToast, after.ReadyToast, "ready toast kept");
        Equal(original.FocusHotkey, after.FocusHotkey, "focus hotkey kept");

        // Each filter flipped exactly once, from its default.
        Check(!after.SkipLinkedInApply, "LinkedIn flipped off");
        Check(!after.SkipSecurityClearance, "clearance flipped off");
        Check(!after.SkipCitizenshipRequirement, "citizenship flipped off");
        Check(after.SkipExportControl, "export flipped on");
        Check(after.SkipNoVisaSponsorship, "visa flipped on");
        Equal(2, JobImportFilter.EnabledCount(after), "two on after flipping every switch");
    });

    // ---------- integration: the one gate ----------

    static void FilterGateCreatesNoTask() => WithTasksFileRestored(() => {
        Directory.CreateDirectory(Storage.DataDir);
        var before = File.Exists(Storage.TasksPath) ? File.ReadAllBytes(Storage.TasksPath) : null;

        var tasks = new List<JobTask> { Job("KEEP-ME-1") };
        var outcome = JobImporter.ImportOne(
            FilterJob("Active TS/SCI clearance required.", null, "Backend Engineer", "https://example.com/jobs/filtered"),
            JobImporter.BrowserSource, tasks, DefaultFilters());

        Equal(JobImportKind.Skipped, outcome.Kind, "the job is skipped, not failed");
        Equal(JobFilterReason.SecurityClearance, outcome.FilterReason, "the reason travels with the outcome");
        Equal(JobImportFilter.Describe(JobFilterReason.SecurityClearance), outcome.Reason, "a plain-words reason");
        Equal("", outcome.JobId, "no task id was handed out");

        // No task, and nothing written: the existing task list is exactly as it was.
        Equal(1, tasks.Count, "no task was created");
        Equal("KEEP-ME-1", tasks[0].JobId, "the existing task is untouched");

        var after = File.Exists(Storage.TasksPath) ? File.ReadAllBytes(Storage.TasksPath) : null;
        Check((before is null && after is null) || (before is not null && after is not null && before.SequenceEqual(after)),
              "tasks.json was not written for a filtered job");

        // Nothing reached the queue either: a skipped job has no Queued task to run.
        Check(!tasks.Any(t => t.Status == "Queued" && t.Title == "Backend Engineer"), "nothing was queued");
    });

    static void FilterGateStillImportsAcceptedJobs() => WithTasksFileRestored(() => {
        var tasks = new List<JobTask>();

        var outcome = JobImporter.ImportOne(
            FilterJob("Build AI services. Follow security best practices.", "https://boards.greenhouse.io/acme/jobs/7",
                     jobUrl: "https://example.com/jobs/good"),
            JobImporter.BrowserSource, tasks, DefaultFilters());

        Equal(JobImportKind.Imported, outcome.Kind, "an acceptable job still imports");
        Equal(JobFilterReason.None, outcome.FilterReason, "no filter reason on an import");
        Equal(1, tasks.Count, "one task created");
        Equal("Queued", tasks[0].Status, "the normal queue status");
        Equal(ApplicationStatus.Viewed, tasks[0].ApplicationStatus, "the normal application status");
        Equal(ApplicationPlatform.Greenhouse, tasks[0].ApplicationPlatform, "the existing ApplyUrl path still runs");
        Check(outcome.ApplyUrlRecorded, "the application link was still recorded");
        Equal(1, Storage.LoadTasks().Count, "it was saved");
    });

    static void FilterGateRunsAfterTheDuplicateDecision() => WithTasksFileRestored(() => {
        const string url = "https://jobright.ai/jobs/info/cccccccccccccccccccccccc";

        // A job imported while the filters were off.
        var tasks = new List<JobTask>();
        var first = JobImporter.ImportOne(FilterJob("Active TS/SCI clearance required.", jobUrl: url),
                                          JobImporter.BrowserSource, tasks, NoFilters);
        Equal(JobImportKind.Imported, first.Kind, "imported with the filters off");

        // The user then turns the filters on and the same job is imported again: it stays a
        // DUPLICATE. A filter must never make an existing job disappear.
        var again = JobImporter.ImportOne(
            FilterJob("Active TS/SCI clearance required.", "https://boards.greenhouse.io/acme/jobs/7", jobUrl: url),
            JobImporter.BrowserSource, tasks, AllFilters());

        Equal(JobImportKind.Duplicate, again.Kind, "a filtered re-import is still a duplicate");
        Equal(first.JobId, again.JobId, "the same existing task");
        Equal(1, tasks.Count, "no task was added or removed");

        // And the existing ApplyUrl backfill still happened, filters or not.
        Check(again.ApplyUrlRecorded, "the empty ApplyUrl was still filled");
        Equal("https://boards.greenhouse.io/acme/jobs/7", tasks[0].ApplyUrl, "the address was saved");
        Equal(ApplicationPlatform.Greenhouse, tasks[0].ApplicationPlatform, "and its platform derived");

        // An existing address is still never replaced.
        var third = JobImporter.ImportOne(
            FilterJob("Active TS/SCI clearance required.", "https://jobs.lever.co/acme/1", jobUrl: url),
            JobImporter.BrowserSource, tasks, AllFilters());
        Equal(JobImportKind.Duplicate, third.Kind, "still a duplicate");
        Check(!third.ApplyUrlRecorded, "an existing address is not replaced");
        Equal("https://boards.greenhouse.io/acme/jobs/7", tasks[0].ApplyUrl, "the first address stands");
    });

    static void FilterSettingsChangeAffectsTheNextImport() => WithTasksFileRestored(() => {
        var tasks = new List<JobTask>();
        const string amd = "This role is not eligible for Visa transfer or Sponsorship.";

        // Default switches: the visa rule is off, so it imports.
        var first = JobImporter.ImportOne(FilterJob(amd, jobUrl: "https://example.com/jobs/v1"),
                                          JobImporter.BrowserSource, tasks, DefaultFilters());
        Equal(JobImportKind.Imported, first.Kind, "imported while the visa filter is off");

        // The user turns the visa filter on; the very next call refuses a new job with the same text.
        var withVisa = DefaultFilters();
        withVisa.SkipNoVisaSponsorship = true;

        var second = JobImporter.ImportOne(FilterJob(amd, jobUrl: "https://example.com/jobs/v2"),
                                           JobImporter.BrowserSource, tasks, withVisa);
        Equal(JobImportKind.Skipped, second.Kind, "refused once the switch is on");
        Equal(JobFilterReason.NoVisaSponsorship, second.FilterReason, "for the visa reason");

        // The job imported earlier is still there — filters gate imports, they never clean up.
        Equal(1, tasks.Count, "the earlier job stayed");
        Equal(first.JobId, tasks[0].JobId, "and it is the same task");
    });

    static void FilterIncomingFolderSharesTheGate() {
        var root = NewDir("filter-incoming");
        var incoming = Path.Combine(root, "in");
        var imported = Path.Combine(root, "done");
        Directory.CreateDirectory(incoming);

        void WriteJob(string name, string description) =>
            File.WriteAllText(Path.Combine(incoming, name), JsonSerializer.Serialize(new {
                company = "Example Co", title = "AI Engineer",
                jobUrl = "https://example.com/jobs/" + Path.GetFileNameWithoutExtension(name),
                description
            }));

        WriteJob("good.json", "Build AI services and follow security best practices.");
        WriteJob("clearance.json", "Active TS/SCI clearance required.");

        WithTasksFileRestored(() => {
            var settings = DefaultFilters();
            settings.IncomingFolder = incoming;
            settings.ImportedFolder = imported;

            var tasks = new List<JobTask>();
            var result = JobImporter.Import(settings, tasks);

            Equal(1, result.JobsQueued, "one job queued");
            Equal(1, result.JobsSkipped, "one job filtered out");
            Equal(0, result.Errors.Count, "a filtered job is not an error: " + Join(result.Errors));
            Equal(1, tasks.Count, "only the accepted job became a task");
            Equal("https://example.com/jobs/good", tasks[0].Link, "and it is the right one");

            // Both files were handled, so neither is retried forever.
            Equal(2, result.FilesImported, "both files were archived");
            Equal(0, Directory.GetFiles(incoming, "*.json").Length, "Incoming is empty");
        });
    }

    static void FilterAutoImportTargetCountsImportsOnly() {
        // The Auto Import loop's own arithmetic, with the real gate and no browser: only an
        // Imported outcome counts toward the target, and the loop keeps going until it is met.
        var settings = DefaultFilters();

        var candidates = new List<(string Url, string Description)>();
        for (var i = 0; i < 12; i++)
            candidates.Add(($"https://example.com/jobs/auto-{i}",
                            i % 3 == 0 ? "Active TS/SCI clearance required." : "Build AI services."));

        WithTasksFileRestored(() => {
            var tasks = new List<JobTask>();

            // One candidate is already in Resume Builder, so it can only ever be a duplicate.
            JobImporter.ImportOne(FilterJob("Build AI services.", jobUrl: candidates[1].Url),
                                  JobImporter.BrowserSource, tasks, settings);

            const int target = 4;
            var imported = 0; var existing = 0; var filtered = 0; var examined = 0;
            var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (url, description) in candidates) {
                if (imported >= target) break;
                if (!processed.Add(JobUrls.Normalize(url) ?? url)) continue;

                examined++;
                var outcome = JobImporter.ImportOne(FilterJob(description, jobUrl: url),
                                                    JobImporter.BrowserSource, tasks, settings);
                switch (outcome.Kind) {
                    case JobImportKind.Imported: imported++; break;
                    case JobImportKind.Duplicate: existing++; break;
                    case JobImportKind.Skipped: filtered++; break;
                }
            }

            Equal(target, imported, "the target counts ACCEPTED imports");
            Check(filtered > 0, "some candidates were filtered out");
            Equal(1, existing, "the duplicate did not count");
            Check(examined > target, $"more candidates were examined ({examined}) than the target ({target})");

            // Every task in the list is a real import; nothing skipped leaked in.
            Equal(imported + existing, tasks.Count, "tasks = accepted imports only");
            foreach (var task in tasks)
                Check(!task.Jd.Contains("TS/SCI", StringComparison.OrdinalIgnoreCase), "no filtered job became a task");
        });
    }

    static void FilterSessionNeverRetriesTheSameRejection() {
        var settings = DefaultFilters();

        WithTasksFileRestored(() => {
            var tasks = new List<JobTask>();
            var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var attempts = 0;

            // The same rejected card offered again and again, as a scroll would.
            var repeated = new[] {
                "https://example.com/jobs/rejected",
                "https://example.com/jobs/rejected/",
                "https://example.com/jobs/rejected?utm_source=scroll",
                "https://example.com/jobs/rejected"
            };

            foreach (var url in repeated) {
                if (!processed.Add(JobUrls.Normalize(url) ?? url)) continue;
                attempts++;
                var outcome = JobImporter.ImportOne(FilterJob("US citizenship required.", jobUrl: url),
                                                    JobImporter.BrowserSource, tasks, settings);
                Equal(JobImportKind.Skipped, outcome.Kind, "refused every time it is tried");
            }

            Equal(1, attempts, "the same rejected job is opened once per session");
            Equal(0, tasks.Count, "and it never becomes a task");

            // A new session starts with an empty set, and a changed setting lets the job in.
            var laterSession = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Check(laterSession.Add(JobUrls.Normalize(repeated[0])!), "a later session may try it again");

            var relaxed = DefaultFilters();
            relaxed.SkipCitizenshipRequirement = false;
            Equal(JobImportKind.Imported,
                  JobImporter.ImportOne(FilterJob("US citizenship required.", jobUrl: repeated[0]),
                                        JobImporter.BrowserSource, tasks, relaxed).Kind,
                  "nothing is blacklisted: the job imports once the filter is off");
        });
    }

    // ---------- SQLite job store (Applications no longer walks the resume tree) ----------

    const string JobrightExternalId = "0123456789abcdef01234567";

    static void UsingStore(Action body) {
        var previous = JobStore.DatabasePathOverride;
        JobStore.DatabasePathOverride = Path.Combine(NewDir("jobstore-" + Guid.NewGuid().ToString("N")), "resumebuilder.db");
        JobStore.ResetDirectoryEnumerations();
        try { body(); }
        finally { JobStore.DatabasePathOverride = previous; }
    }

    static JobTask StoreJob(string id, string company, string title, string source, string link) {
        var job = Job(id, company, title, link);
        job.Source = source;
        job.Jd = "Build the product.";
        job.ApplicationStatus = ApplicationStatus.Ready;
        return job;
    }

    static void SqliteMigrationImportsJobs() {
        UsingStore(() => {
            var json = Path.Combine(NewDir("json-untouched"), "tasks.json");
            File.WriteAllText(json, """[{ "jobId": "RB-KEEP" }]""");
            var before = File.ReadAllBytes(json);
            var tasks = new List<JobTask> {
                StoreJob("RB-1", "Acme", "Engineer", JobImporter.BrowserSource,
                    "https://jobright.ai/jobs/info/" + JobrightExternalId),
                StoreJob("RB-2", "Other", "Analyst", JobImporter.IncomingSource, "https://example.com/jobs/2")
            };
            tasks[0].ApplyUrl = "https://boards.greenhouse.io/acme/jobs/9";
            Equal(JobStore.SchemaVersion, JobStore.UserVersion, "opening the database creates the schema");
            JobStore.SaveJobs(tasks);
            var report = JobStore.MigrateIfNeeded(tasks, NewDir("no-resumes-import"));
            Check(!report.Failed && report.AlreadyComplete, "startup does not import a task list");
            Equal(2, report.Jobs, "jobs stored");
            Equal(2, report.Applications, "applications stored");
            Equal(JobStore.SchemaVersion, JobStore.UserVersion, "version stays at the schema");
            var rows = JobStore.LoadApplications();
            Equal(2, rows.Count, "both jobs load");
            var jobright = rows.Single(r => r.JobId == "RB-1");
            Equal(JobrightExternalId, jobright.ExternalJobId, "Jobright external id");
            Equal(ApplicationStatus.Ready, jobright.ApplicationStatus, "application status");
            Equal("https://boards.greenhouse.io/acme/jobs/9", jobright.ApplyUrl, "apply url");
            Check(rows.Single(r => r.JobId == "RB-2").ExternalJobId is null, "a non-Jobright posting has no external id");
            Check(before.AsSpan().SequenceEqual(File.ReadAllBytes(json)), "tasks.json bytes were not rewritten");
        });
    }

    static void SqliteMigrationLinksByJobId() {
        UsingStore(() => {
            var root = NewDir("resume-root-link");
            var folder = Path.Combine(root, "2026-09-28", "Decoy Company - Decoy Role");
            Directory.CreateDirectory(folder);
            ResumeOutputManager.SaveMetadata(Path.Combine(folder, "resume-info.json"), new ResumeOutputMetadata {
                JobId = "RB-REAL",
                Company = "Decoy Company",
                Role = "Decoy Role",
                DocxFile = "Billy.docx",
                PdfFile = "Billy.pdf",
                GeneratedAt = "2026-09-28T12:00:00"
            });
            var tasks = new List<JobTask> {
                StoreJob("RB-DECOY", "Decoy Company", "Decoy Role", JobImporter.BrowserSource, "https://example.com/decoy"),
                StoreJob("RB-REAL", "Real Co", "Real Role", JobImporter.IncomingSource, "https://example.com/real")
            };
            JobStore.SaveJobs(tasks);
            JobStore.RecordResumeOutput("RB-REAL", Path.Combine(folder, "Billy.docx"), Path.Combine(folder, "Billy.pdf"), folder);
            var report = JobStore.MigrateIfNeeded(tasks, root);
            Equal(0, report.ResumeInfoFilesRead, "startup does not read resume-info files");
            Equal(1, report.ResumeOutputs, "one resume output");
            Equal(0, JobStore.DirectoryEnumerations, "startup lists no resume folders");
            var rows = JobStore.LoadApplications();
            Check(string.IsNullOrEmpty(rows.Single(r => r.JobId == "RB-DECOY").DocxPath),
                "company, title and folder name did not claim the resume");
            Equal(Path.Combine(folder, "Billy.docx"), rows.Single(r => r.JobId == "RB-REAL").DocxPath,
                "the internal job id owns the resume");
        });
    }

    static void SqliteMigrationIsIdempotent() {
        UsingStore(() => {
            var root = NewDir("resume-root-idempotent");
            var folder = Path.Combine(root, "2026-09-28", "Acme - Engineer");
            Directory.CreateDirectory(folder);
            ResumeOutputManager.SaveMetadata(Path.Combine(folder, "resume-info.json"), new ResumeOutputMetadata {
                JobId = "RB-1", Company = "Acme", Role = "Engineer", DocxFile = "Resume.docx",
                GeneratedAt = "2026-09-28T08:00:00"
            });
            var tasks = new List<JobTask> { StoreJob("RB-1", "Acme", "Engineer", JobImporter.IncomingSource, "https://example.com/1") };
            JobStore.SaveJobs(tasks);
            JobStore.RecordResumeOutput("RB-1", Path.Combine(folder, "Resume.docx"), null, folder);
            var first = JobStore.MigrateIfNeeded(tasks, root);
            Equal(1, first.Jobs, "one job");
            Equal(1, first.ResumeOutputs, "one output");
            JobStore.ResetDirectoryEnumerations();
            var second = JobStore.MigrateIfNeeded(tasks, root);
            Check(second.AlreadyComplete, "a finished migration does not run again");
            Equal(0, JobStore.DirectoryEnumerations, "the second startup does not list resume folders");
            Equal(1, JobStore.Counts().Jobs, "still one job");
            Equal(1, JobStore.Counts().Outputs, "still one output");
            JobStore.SetUserVersion(0);
            var third = JobStore.MigrateIfNeeded(tasks, root);
            Check(!third.Failed, "a rerun after a partial migration succeeds");
            Equal(1, JobStore.Counts().Jobs, "rerun did not duplicate the job");
            Equal(1, JobStore.Counts().Applications, "rerun did not duplicate the application");
            Equal(1, JobStore.Counts().Outputs, "rerun did not duplicate the resume output");
            Equal(0, third.ResumeInfoFilesRead, "startup does not read resume-info files");
        });
    }

    static void SqliteAlreadyMigratedStillUpsertsNewJobs() {
        UsingStore(() => {
            var first = JobStore.MigrateIfNeeded(Array.Empty<JobTask>(), NewDir("no-resumes-catchup"));
            Check(first.AlreadyComplete, "schema setup does not import a task file");
            var kept = StoreJob("RB-KEEP", "Kept", "Role", JobImporter.IncomingSource, "https://example.com/keep");
            JobStore.SyncJob(kept);
            var added = StoreJob("RB-NEW", "New", "Role", JobImporter.BrowserSource,
                "https://jobright.ai/jobs/info/" + JobrightExternalId);
            added.ApplicationStatus = ApplicationStatus.Viewed;
            JobStore.ResetDirectoryEnumerations();
            JobStore.UpsertTracked(added, "import");
            var second = JobStore.MigrateIfNeeded(new[] { added }, NewDir("no-resumes-catchup-2"));
            Check(second.AlreadyComplete, "startup does not migrate again");
            Equal(0, JobStore.DirectoryEnumerations, "catch-up lists no resume folders");
            var ids = JobStore.LoadApplications().Select(row => row.JobId).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            Equal("RB-KEEP,RB-NEW", string.Join(",", ids), "the new job is stored and the older row stays");
            added.ApplicationStatus = ApplicationStatus.Ready;
            added.ReadyAt = new DateTime(2026, 9, 28, 12, 0, 0);
            Check(JobStore.UpsertTracked(added, "resume-ready"), "ready updates the stored job");
            Equal(2, JobStore.Counts().Jobs, "ready does not create a second job");
            Equal(2, JobStore.Counts().Applications, "ready does not create a second application");
            Equal(ApplicationStatus.Ready,
                JobStore.LoadApplications().Single(row => row.JobId == "RB-NEW").ApplicationStatus,
                "the same application row is Ready");
        });
    }

    static void SqliteJobrightIdentityIsUnique() {
        UsingStore(() => {
            var link = "https://jobright.ai/jobs/info/" + JobrightExternalId;
            var first = StoreJob("RB-A", "Acme", "Engineer", JobImporter.BrowserSource, link);
            var second = StoreJob("RB-B", "Other", "Analyst", JobImporter.BrowserSource, link);
            Check(JobStore.TryAddJob(first), "the first Jobright job is stored");
            Check(!JobStore.TryAddJob(second), "the same source and external id is rejected");
            Equal(1, JobStore.Counts().Jobs, "the rejected job was not inserted");
            var otherSource = StoreJob("RB-C", "Other", "Analyst", JobImporter.IncomingSource, link);
            Check(JobStore.TryAddJob(otherSource), "a different source may carry the same external id");
            var plainA = StoreJob("RB-D", "One", "Role", JobImporter.IncomingSource, "https://example.com/d");
            var plainB = StoreJob("RB-E", "Two", "Role", JobImporter.IncomingSource, "https://example.com/e");
            Check(JobStore.TryAddJob(plainA) && JobStore.TryAddJob(plainB), "missing external ids are not a collision");
            Equal(4, JobStore.Counts().Jobs, "three accepted jobs plus the first");
        });
    }

    static void SqliteResumeOutputUpserts() {
        UsingStore(() => {
            var job = StoreJob("RB-GEN", "Acme", "Engineer", JobImporter.IncomingSource, "https://example.com/gen");
            JobStore.SyncJob(job);
            var folder = NewDir("generated");
            var first = Path.Combine(folder, "Resume.docx");
            var firstPdf = Path.Combine(folder, "Resume.pdf");
            JobStore.RecordResumeOutput(job.JobId, first, firstPdf, folder);
            Equal(1, JobStore.Counts().Outputs, "the first generation inserts one output");
            Equal(first, JobStore.LoadApplications().Single().DocxPath, "the first path is stored");
            var second = Path.Combine(folder, "Resume (2).docx");
            var secondPdf = Path.Combine(folder, "Resume (2).pdf");
            JobStore.RecordResumeOutput(job.JobId, second, secondPdf, folder);
            Equal(1, JobStore.Counts().Outputs, "a later generation updates that output");
            var row = JobStore.LoadApplications().Single();
            Equal(second, row.DocxPath, "the latest DOCX is stored");
            Equal(secondPdf, row.PdfPath, "the latest PDF is stored");
        });
    }

    static void SqliteApplicationsLoadDoesNotScan() {
        UsingStore(() => {
            var root = NewDir("resume-root-load");
            var folder = Path.Combine(root, "2026-09-28", "Acme - Engineer");
            Directory.CreateDirectory(folder);
            ResumeOutputManager.SaveMetadata(Path.Combine(folder, "resume-info.json"), new ResumeOutputMetadata {
                JobId = "RB-1", Company = "Someone Else", Role = "Other Role", DocxFile = "Resume.docx",
                GeneratedAt = "2026-09-28T08:00:00"
            });
            var tasks = new List<JobTask> { StoreJob("RB-1", "Acme", "Engineer", JobImporter.IncomingSource, "https://example.com/1") };
            JobStore.SaveJobs(tasks);
            JobStore.RecordResumeOutput("RB-1", Path.Combine(folder, "Resume.docx"), null, folder);
            var report = JobStore.MigrateIfNeeded(tasks, root);
            Equal(0, report.DirectoryEnumerations, "startup lists no resume folders");
            JobStore.ResetDirectoryEnumerations();
            var rows = JobStore.LoadApplications();
            JobStore.ApplyResumeLinks(tasks);
            Equal(0, JobStore.DirectoryEnumerations, "Applications loading lists no resume folders");
            Equal(1, rows.Count, "the job still loads from SQLite");
            Equal(Path.Combine(folder, "Resume.docx"), tasks[0].ResumePath, "the stored path is applied");
        });
    }

    static void SqliteIsTheOnlyJobStore() {
        UsingStore(() => {
            var list = new List<JobTask>();
            var url = "https://jobright.ai/jobs/info/" + JobrightExternalId;
            var imported = JobImporter.ImportOne(ImportData(url, null), JobImporter.BrowserSource, list, NoFilters);
            Equal(JobImportKind.Imported, imported.Kind, "a new job imports");
            var stored = JobStore.GetJobs().Single();
            Equal(imported.JobId, stored.JobId, "the imported job is the stored job");
            Equal(ApplicationStatus.Viewed, stored.ApplicationStatus, "import creates Viewed");
            Equal(1, JobStore.Counts().Applications, "import creates one application");

            var duplicate = JobImporter.ImportOne(ImportData(url, null), JobImporter.BrowserSource, list, NoFilters);
            Equal(JobImportKind.Duplicate, duplicate.Kind, "the same posting is a duplicate");
            Equal(1, JobStore.Counts().Jobs, "a duplicate import adds no job");
            Equal(1, JobStore.Counts().Applications, "a duplicate import adds no application");

            var folder = NewDir("only-store-resume");
            var docx = Path.Combine(folder, "Resume.docx");
            JobTracker.MarkResumeReady(stored, docx);
            JobStore.CommitResume(stored, docx, null, folder);
            var ready = JobStore.GetJobs().Single();
            Equal(ApplicationStatus.Ready, ready.ApplicationStatus, "a resume makes the same application Ready");
            Equal(docx, ready.ResumePath, "the resume path is stored");
            Equal(1, JobStore.Counts().Jobs, "resume generation adds no job");
            Equal(1, JobStore.Counts().Outputs, "one resume output");

            ready.Status = "Failed";
            ready.FailureReason = "GptInvalidOutput";
            JobStore.SaveJobs(new[] { ready });
            var failed = JobStore.GetJobs().Single();
            Equal("Failed", failed.Status, "queue failure is stored");
            Equal(ApplicationStatus.Ready, failed.ApplicationStatus, "queue failure does not change the application");

            failed.Status = "Completed";
            Check(JobTracker.MarkApplied(failed, new DateTime(2026, 9, 28, 15, 0, 0)), "mark applied");
            JobStore.SaveJobs(new[] { failed });
            var applied = JobStore.GetJobs().Single();
            Equal(ApplicationStatus.Applied, applied.ApplicationStatus, "mark applied stores Applied");
            Check(applied.AppliedAt is not null, "AppliedAt is stored");
            Equal("Completed", applied.Status, "mark applied leaves the queue status");

            var restarted = JobStore.GetJobs().Single();
            Equal(applied.JobId, restarted.JobId, "restart reads the same job");
            Equal(ApplicationStatus.Applied, restarted.ApplicationStatus, "restart reads the application");
            Equal(docx, restarted.ResumePath, "restart reads the resume");
            Equal("Completed", restarted.Status, "restart reads the queue status");

            JobStore.ResetDirectoryEnumerations();
            var opens = JobStore.ConnectionsOpened;
            Equal(1, JobStore.GetJobs().Count, "the query returns the job");
            Equal(0, JobStore.DirectoryEnumerations, "the query lists no folders");
            Equal(opens + 1, JobStore.ConnectionsOpened, "the query uses one connection");
        });

        WithLiveTasksFile(() => {
            var marker = "tasks-json-must-stay-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(Storage.TasksPath, "[{" + "\"company\":\"" + marker + "\"}]");
            var before = File.ReadAllBytes(Storage.TasksPath);
            var fromDb = Storage.LoadTasks();
            Check(fromDb.All(job => job.Company != marker), "an existing tasks.json is not loaded");
            Storage.SaveTasks(fromDb);
            Check(before.AsSpan().SequenceEqual(File.ReadAllBytes(Storage.TasksPath)), "saving jobs does not rewrite tasks.json");
            File.Delete(Storage.TasksPath);
            var withoutFile = Storage.LoadTasks();
            Equal(fromDb.Count, withoutFile.Count, "jobs still load when tasks.json is missing");
        });
    }

    static void ApplyButtonUsesOnlyTheApplicationUrl() {
        var job = StoreJob("RB-APPLY", "Acme", "Engineer", JobImporter.BrowserSource,
            "https://jobright.ai/jobs/info/" + JobrightExternalId);
        job.Status = "Failed";
        Check(JobTracker.IsOpenableUrl(ApplicationFields.JobUrl(job)), "Open Job uses the posting");
        Check(JobTracker.CanApply(job), "Ready enables Apply even before an application URL is stored");
        Equal(ApplyRoute.CaptureOnJobright, JobTracker.RouteApply(job), "a missing application URL uses the Jobright capture");
        Check(JobTracker.ApplyTarget(job) is null, "Apply does not open the Jobright posting");
        var line = ApplicationFields.BindLine(job);
        Check(line.Contains("jobId=RB-APPLY", StringComparison.Ordinal), "bind job id");
        Check(line.Contains("status=Ready", StringComparison.Ordinal), "bind application status");
        Check(line.Contains("jobUrl-present=yes", StringComparison.Ordinal), "posting is present");
        Check(line.Contains("applyUrl-present=no", StringComparison.Ordinal), "application URL is absent");
        Check(line.Contains("apply-enabled=yes", StringComparison.Ordinal), "Ready enables Apply");
        Check(line.EndsWith(" ats=", StringComparison.Ordinal), "Jobright is not shown as the ATS");
        Equal(ApplicationPlatform.Unknown, ApplicationFields.Ats(job), "the job site is not the ATS");

        job.ApplicationStatus = ApplicationStatus.Viewed;
        job.ApplyUrl = "https://boards.greenhouse.io/acme/jobs/9";
        Check(JobTracker.CanApply(job), "Viewed with an application URL still offers Apply");

        job.ApplicationStatus = ApplicationStatus.Ready;
        job.ApplyUrl = "https://boards.greenhouse.io/acme/jobs/9";
        job.ApplicationPlatform = ApplicationPlatform.Greenhouse;
        Check(JobTracker.CanApply(job), "Ready plus an application URL enables Apply");
        Equal(ApplyRoute.OpenStored, JobTracker.RouteApply(job), "a stored application URL opens directly");
        Equal("https://boards.greenhouse.io/acme/jobs/9", JobTracker.ApplyTarget(job), "Apply opens that URL, not the posting");
        Check(job.Status == "Failed", "queue Failed is untouched");
        Check(ApplicationFields.BindLine(job).Contains("apply-enabled=yes", StringComparison.Ordinal), "bind shows enabled");
        Check(ApplicationFields.BindLine(job).Contains("ats=Greenhouse", StringComparison.Ordinal), "bind shows the ATS");

        job.ApplicationStatus = ApplicationStatus.Applied;
        Check(JobTracker.CanApply(job), "Applied still offers Apply");
        Check(JobTracker.ApplyUrlToOpen(job) is not null, "Open Application still has the address");
        Equal(job.Link, ApplicationFields.JobUrl(job), "Open Job's field is the posting");
        Check(ApplicationFields.JobUrl(job) != ApplicationFields.ApplyUrl(job), "Open Job and Apply use different fields");
    }

    static void ApplyEnabledForEveryStatus() {
        const string posting = "https://jobright.ai/jobs/info/aaaaaaaaaaaaaaaaaaaaaaaa";
        const string apply = "https://boards.greenhouse.io/acme/jobs/9";

        void Expect(string status, string link, string applyUrl, bool enabled, string label) {
            var job = new JobTask { ApplicationStatus = status, Link = link, ApplyUrl = applyUrl };
            Equal(status, job.ApplicationStatus, label + " status");
            Check(JobTracker.CanApply(job) == enabled, label);
            Equal(status, job.ApplicationStatus, label + " status unchanged");
            if (!enabled) return;
            if (ApplyCapture.IsApplicationUrl(applyUrl))
                Equal(ApplyRoute.OpenStored, JobTracker.RouteApply(job), label + " opens the application URL");
            else if (JobrightPageExtractor.IsJobPage(link))
                Equal(ApplyRoute.CaptureOnJobright, JobTracker.RouteApply(job), label + " captures from the posting");
            Equal(status, job.ApplicationStatus, label + " routing leaves the status");
        }

        Expect(ApplicationStatus.Viewed, posting, "", true, "Viewed + JobUrl");
        Expect(ApplicationStatus.Ready, posting, "", true, "Ready + JobUrl");
        Expect(ApplicationStatus.Applied, "", apply, true, "Applied + ApplyUrl");
        Expect(ApplicationStatus.Interview, posting, apply, true, "Interview + ApplyUrl");
        Expect(ApplicationStatus.Failed, posting, "", true, "Failed + JobUrl");
        Expect(ApplicationStatus.Done, "", apply, true, "Done + ApplyUrl");
        Expect(ApplicationStatus.Ready, "", "", false, "no ApplyUrl + no JobUrl");
        Expect(ApplicationStatus.Viewed, "not a url", "javascript:alert(1)", false, "unusable addresses stay disabled");
    }

    static void ApplyCaptureFlow() {
        var page = "https://jobright.ai/jobs/info/" + JobrightExternalId;
        var external = "https://boards.greenhouse.io/acme/jobs/9";
        var job = StoreJob("RB-APPLY-FLOW", "Acme", "Engineer", JobImporter.BrowserSource, page);

        Equal(ApplyRoute.CaptureOnJobright, JobTracker.RouteApply(job), "no application URL starts the Jobright capture");
        Check(JobTracker.ApplyTarget(job) is null, "the posting is not opened as Apply");
        Equal(ApplyCaptureResult.NotApplicationUrl, ApplyCapture.Record(new[] { job }, page, page, DateTime.Now),
            "a Jobright URL is not an application address");
        Equal("", job.ApplyUrl, "the posting was not stored as ApplyUrl");
        Equal(ApplicationPlatform.Unknown, job.ApplicationPlatform, "platform stays Unknown without an external URL");

        var recorded = ApplyCapture.Record(new[] { job }, page, external + "?gh_src=track", DateTime.Now);
        Equal(ApplyCaptureResult.Recorded, recorded, "the external destination is stored");
        Equal(JobUrls.Normalize(external + "?gh_src=track"), job.ApplyUrl, "the external ATS URL is what gets stored");
        Check(job.ApplyUrl.Contains("boards.greenhouse.io", StringComparison.Ordinal), "it is the Greenhouse address");
        Check(!job.ApplyUrl.Contains("jobright.ai", StringComparison.Ordinal), "it is not the Jobright posting");
        Equal(ApplicationPlatform.Greenhouse, job.ApplicationPlatform, "the platform comes from that URL");
        Equal(ApplyRoute.OpenStored, JobTracker.RouteApply(job), "the next Apply opens the stored URL");
        Equal(job.ApplyUrl, JobTracker.ApplyTarget(job), "Apply opens the external URL");

        var again = ApplyCapture.Record(new[] { job }, page, "https://jobright.ai/jobs/info/" + JobrightExternalId, DateTime.Now);
        Equal(ApplyCaptureResult.NotApplicationUrl, again, "a later Jobright navigation does not replace it");
        Check(job.ApplyUrl.Contains("boards.greenhouse.io", StringComparison.Ordinal), "the exact URL stays");

        var kept = new DateTime(2026, 9, 18, 14, 0, 0);
        job.ApplyUrlCapturedAt = kept;
        Check(!ApplyCapture.FillIfEmpty(job, null, DateTime.Now), "a blank import does not write");
        Check(!ApplyCapture.FillIfEmpty(job, "https://jobs.lever.co/acme/2", DateTime.Now), "a second link does not replace a stored one");
        Check(job.ApplyUrl.Contains("boards.greenhouse.io", StringComparison.Ordinal), "duplicate import keeps the exact URL");
        Equal(kept, job.ApplyUrlCapturedAt, "the capture time stays");

        UsingProfileRoot(() => {
            JobStore.DatabasePathOverride = null;
            var billy = ProfileRegistry.Create("Billy", null);
            var luis = ProfileRegistry.Create("Luis", null);
            Check(ProfileContext.TryOpen(billy.ProfileId), "Billy opens");
            var billyJob = StoreJob("RB-BILLY-APPLY", "Acme", "Engineer", JobImporter.BrowserSource, page);
            var capture = ApplyCapture.Record(new[] { billyJob }, page, "https://jobs.lever.co/acme/9", DateTime.Now);
            Equal(ApplyCaptureResult.Recorded, capture, "Billy's external URL is recorded");
            Equal(ApplicationPlatform.Lever, billyJob.ApplicationPlatform, "Billy's platform is Lever");
            JobStore.SyncJob(billyJob);
            ProfileContext.Close();

            Check(ProfileContext.TryOpen(luis.ProfileId), "Luis opens");
            Equal(0, JobStore.GetJobs().Count, "Luis does not see Billy's application URL");
            ProfileContext.Close();

            Check(ProfileContext.TryOpen(billy.ProfileId), "Billy opens again");
            var stored = JobStore.GetJobs().Single();
            Equal("https://jobs.lever.co/acme/9", stored.ApplyUrl, "Billy's URL is still in his database");
            Equal(ApplicationPlatform.Lever, stored.ApplicationPlatform, "Billy's platform is still Lever");
        });
    }

    static void SqliteRepairFillsEmptyApplyUrlAndResumePath() {
        UsingStore(() => {
            var job = StoreJob("RB-REPAIR", "Acme", "Engineer", JobImporter.BrowserSource,
                "https://jobright.ai/jobs/info/" + JobrightExternalId);
            job.ApplyUrl = job.Link;
            JobStore.SyncJob(job);
            Check(string.IsNullOrEmpty(JobStore.LoadApplications().Single().ApplyUrl),
                "a Jobright address is not stored as the application URL");

            job.ApplyUrl = "https://boards.greenhouse.io/acme/jobs/9";
            JobStore.SyncJob(job);
            job.ApplyUrl = "";
            job.ApplicationPlatform = ApplicationPlatform.Unknown;
            JobStore.SyncJob(job);
            Equal("https://boards.greenhouse.io/acme/jobs/9", JobStore.LoadApplications().Single().ApplyUrl,
                "saving a blank application URL keeps the one already stored");

            JobStore.ResetDirectoryEnumerations();
            JobStore.ApplyResumeLinks(new[] { job });
            Equal("https://boards.greenhouse.io/acme/jobs/9", job.ApplyUrl, "SQLite fills the empty application URL");
            Equal(ApplicationPlatform.Greenhouse, job.ApplicationPlatform, "the ATS comes from that URL");
            Equal(1, JobStore.LastApplyUrlsFilled, "one application URL was backfilled");

            job.ApplyUrl = "https://jobs.lever.co/acme/1";
            job.ApplicationPlatform = ApplicationPlatform.Lever;
            JobStore.ApplyResumeLinks(new[] { job });
            Equal("https://jobs.lever.co/acme/1", job.ApplyUrl, "a recorded application URL is not replaced");
            Equal(0, JobStore.LastApplyUrlsFilled, "nothing was backfilled");

            var path = Path.Combine(NewDir("repair-resume"), "Resume.docx");
            job.ResumePath = path;
            Equal(1, JobStore.RepairResumeOutputs(new[] { job }), "the known DOCX path is stored");
            Equal(path, JobStore.LoadApplications().Single().DocxPath, "the stored path is that DOCX");
            Equal(0, JobStore.RepairResumeOutputs(new[] { job }), "the same path is not stored again");
            Equal(1, JobStore.Counts().Outputs, "still one output");
            Equal(0, JobStore.DirectoryEnumerations, "repair and backfill list no resume folders");

            var other = StoreJob("RB-BARE", "Other", "Analyst", JobImporter.IncomingSource, "https://example.com/jobs/2");
            JobStore.SyncJob(other);
            var integrity = JobStore.Inspect(new[] { job, other });
            Equal(2, integrity.Jobs, "both jobs");
            Equal(0, integrity.DuplicateInternalJobIds, "no duplicate internal ids");
            Equal(0, integrity.DuplicateSourceExternal, "no duplicate source and external id");
            Equal(0, integrity.MissingJobUrl, "both postings are present");
            Equal(1, integrity.MissingApplyUrl, "only the job without an application URL is counted");
            Equal(0, integrity.InvalidUrls, "no invalid URL");
            Equal(0, integrity.MissingApplications, "every job has an application row");
            Equal(0, integrity.OrphanApplications, "no orphan application");
            Equal(1, integrity.MissingResumeOutputs, "a job with no resume path has no output row");
            Equal(0, integrity.OrphanResumeOutputs, "no orphan output");
            Equal(0, integrity.StatusMismatches, "application status matches");
            Equal(0, integrity.JobSiteStoredAsAts, "Jobright is not stored as the ATS");
            Check(integrity.Describe().Contains("ats=Lever:1", StringComparison.Ordinal), "ATS distribution is the application platform");
            Check(integrity.AtsDistribution.Contains("Jobright", StringComparison.Ordinal) == false, "the job site is not in the ATS distribution");
        });
    }

    static void SqliteUpsertKeepsNonEmptyFields() {
        UsingStore(() => {
            var seen = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Local);
            var job = StoreJob("RB-KEEP", "Acme", "Engineer", JobImporter.BrowserSource,
                "https://jobright.ai/jobs/info/bbbbbbbbbbbbbbbbbbbbbbbb");
            job.Jd = "Build the service.";
            job.Location = "Austin";
            job.CompanyUrl = "https://acme.example";
            job.ApplyUrl = "https://boards.greenhouse.io/acme/jobs/1";
            job.ApplyUrlCapturedAt = seen;
            job.ApplicationPlatform = ApplicationPlatform.Greenhouse;
            job.ApplicationStatus = ApplicationStatus.Applied;
            job.AppliedAt = seen;
            job.Status = "Completed";
            JobStore.SyncJob(job);

            job.Company = "";
            job.Title = " ";
            job.Jd = "";
            job.Location = "";
            job.CompanyUrl = "";
            job.Link = "";
            job.ApplyUrl = "";
            job.ApplyUrlCapturedAt = null;
            job.ApplicationPlatform = ApplicationPlatform.Unknown;
            job.AppliedAt = null;
            job.Status = "";
            JobStore.SyncJob(job);

            var stored = JobStore.GetJobs().Single();
            Equal("Acme", stored.Company, "company kept");
            Equal("Engineer", stored.Title, "title kept");
            Equal("Build the service.", stored.Jd, "description kept");
            Equal("Austin", stored.Location, "location kept");
            Equal("https://acme.example", stored.CompanyUrl, "company URL kept");
            Equal("https://jobright.ai/jobs/info/bbbbbbbbbbbbbbbbbbbbbbbb", stored.Link, "job URL kept");
            Equal("https://boards.greenhouse.io/acme/jobs/1", stored.ApplyUrl, "apply URL kept");
            Equal(seen, stored.ApplyUrlCapturedAt, "apply time kept");
            Equal(ApplicationPlatform.Greenhouse, stored.ApplicationPlatform, "platform kept");
            Equal(ApplicationStatus.Applied, stored.ApplicationStatus, "application status kept");
            Equal(seen, stored.AppliedAt, "applied time kept");
            Equal("Completed", stored.Status, "queue status kept");

            job.Title = "Senior Engineer";
            job.Company = stored.Company;
            job.Jd = stored.Jd;
            job.Location = stored.Location;
            job.CompanyUrl = "https://new.example";
            job.Link = stored.Link;
            job.ApplyUrl = "https://jobs.lever.co/acme/2";
            job.ApplyUrlCapturedAt = seen.AddDays(1);
            job.ApplicationPlatform = ApplicationPlatform.Lever;
            job.ApplicationStatus = stored.ApplicationStatus;
            job.AppliedAt = stored.AppliedAt;
            job.Status = stored.Status;
            JobStore.SyncJob(job);

            stored = JobStore.GetJobs().Single();
            Equal("Senior Engineer", stored.Title, "a new title replaces the old one");
            Equal("https://new.example", stored.CompanyUrl, "a new company URL replaces the old one");
            Equal("https://jobs.lever.co/acme/2", stored.ApplyUrl, "a new apply URL replaces the old one");
            Equal(ApplicationPlatform.Lever, stored.ApplicationPlatform, "the platform follows the new apply URL");
            Equal(seen.AddDays(1), stored.ApplyUrlCapturedAt, "the apply time follows the new URL");
        });
    }

    static void UsingProfileRoot(Action body) {
        var root = ProfilePaths.RootOverride;
        var db = JobStore.DatabasePathOverride;
        ProfilePaths.RootOverride = NewDir("profiles-" + Guid.NewGuid().ToString("N"));
        try { body(); }
        finally {
            ProfileContext.Close();
            ProfilePaths.RootOverride = root;
            JobStore.DatabasePathOverride = db;
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    static void ProfileCreateIsUnique() => UsingProfileRoot(() => {
        var billy = ProfileRegistry.Create("Billy", null);
        var luis = ProfileRegistry.Create("Luis", null);
        Check(ProfileIds.IsValid(billy.ProfileId) && ProfileIds.IsValid(luis.ProfileId), "both ids are generated");
        Check(billy.ProfileId != luis.ProfileId, "the ids differ");
        Check(!billy.ProfileId.Contains("Billy", StringComparison.OrdinalIgnoreCase), "the name is not the id");
        Check(Directory.Exists(ProfilePaths.ProfileRoot(billy.ProfileId)), "Billy's folder exists");
        var db = ProfilePaths.DatabasePath(billy.ProfileId);
        Check(File.Exists(db), "a new database is created");
        Check(db.Contains(billy.ProfileId, StringComparison.Ordinal), "the database is inside that profile");
        Equal(db, Path.Combine(ProfilePaths.ProfileRoot(billy.ProfileId), "resumebuilder.db"), "database file name");
    });

    static void ProfileDatabasesAreIsolated() => UsingProfileRoot(() => {
        var billy = ProfileRegistry.Create("Billy", null);
        var luis = ProfileRegistry.Create("Luis", null);
        JobStore.DatabasePathOverride = null;

        Check(ProfileContext.TryOpen(billy.ProfileId), "Billy opens");
        Equal(ProfilePaths.DatabasePath(billy.ProfileId), ProfileContext.DatabasePath, "Billy's database path");
        Equal(ProfileContext.HtmlTailoringPath, HtmlTailor.RootDirectory, "HTML tailoring uses the profile folder");
        Equal(ProfileContext.EmailTasksPath, Storage.EmailTasksPath, "email tasks use the profile folder");
        Equal(ProfileContext.CandidateProfilePath, CandidateProfileStore.CandidateProfilePath, "the candidate profile uses the profile folder");
        Equal(Path.Combine(ProfileContext.ProfileRoot, "PromptAdaptation"), PromptConversion.Root, "prompt adaptation uses the profile folder");
        Equal(Path.Combine(ProfileContext.ProfileRoot, "WebView2"), Storage.WebViewUserDataFolder, "ChatGPT browser data uses the profile folder");
        Equal(Path.Combine(ProfileContext.ProfileRoot, "JobBrowserWebView2"), JobBrowser.UserDataFolder, "the job browser uses the profile folder");
        var license = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ResumeBuilder", "license.lic");
        Check(!license.StartsWith(ProfilePaths.UsersRoot, StringComparison.OrdinalIgnoreCase),
            "the license stays outside every profile");
        Check(!IconCache.Root.StartsWith(ProfilePaths.UsersRoot, StringComparison.OrdinalIgnoreCase),
            "icons stay in the shared cache");
        Check(ProfilePaths.RegistryPath.StartsWith(ProfilePaths.GlobalRoot, StringComparison.OrdinalIgnoreCase), "the profile list stays global");

        File.WriteAllText(ProfileContext.SettingsPath, """{ "OriginalResume": "D:\\Billy\\Resume.docx" }""");
        File.WriteAllText(ProfileContext.CandidateProfilePath, """{ "info": { "name": "Billy" } }""");
        File.WriteAllText(ProfileContext.EmailTasksPath, """[{ "jobId": "ET-BILLY" }]""");
        Directory.CreateDirectory(PromptConversion.Root);
        File.WriteAllText(Path.Combine(PromptConversion.Root, "adapted-prompt.txt"), "billy-prompt");
        Directory.CreateDirectory(HtmlTailor.RootDirectory);
        File.WriteAllText(Path.Combine(HtmlTailor.RootDirectory, "billy.html"), "billy-html");
        var job = StoreJob("RB-BILLY", "Acme", "Engineer", JobImporter.IncomingSource, "https://example.com/billy");
        job.ApplicationStatus = ApplicationStatus.Applied;
        JobStore.SyncJob(job);
        JobStore.RecordResumeOutput(job.JobId, @"D:\Billy\Resume.docx", @"D:\Billy\Resume.pdf", @"D:\Billy");
        var billyDb = ProfileContext.DatabasePath;
        var billyChat = Storage.WebViewUserDataFolder;
        var billyBrowser = JobBrowser.UserDataFolder;
        Check(ProfileContext.ProfileLogLine().Contains("id=" + billy.ProfileId, StringComparison.Ordinal), "the profile log names Billy");
        Check(ProfileContext.DatabaseLogLine().Contains(billyDb, StringComparison.Ordinal), "the database log names Billy's file");
        ProfileContext.Close();

        Check(ProfileContext.TryOpen(luis.ProfileId), "Luis opens");
        Check(billyDb != ProfileContext.DatabasePath, "the databases are different files");
        Check(billyChat != Storage.WebViewUserDataFolder, "ChatGPT browser folders differ");
        Check(billyBrowser != JobBrowser.UserDataFolder, "job browser folders differ");
        Equal(0, JobStore.GetJobs().Count, "Luis does not see Billy's queue or applications");
        Equal(0, JobStore.Counts().Outputs, "Luis does not see Billy's resume outputs");
        Check(!File.Exists(ProfileContext.SettingsPath), "Luis has no copy of Billy's settings");
        Check(!File.Exists(ProfileContext.CandidateProfilePath), "Luis has no copy of Billy's candidate profile");
        Check(!File.Exists(ProfileContext.EmailTasksPath), "Luis has no copy of Billy's email tasks");
        Check(!File.Exists(Path.Combine(PromptConversion.Root, "adapted-prompt.txt")), "Luis has no copy of Billy's prompt");
        Check(!File.Exists(Path.Combine(HtmlTailor.RootDirectory, "billy.html")), "Luis has no copy of Billy's HTML cache");
        JobStore.SyncJob(StoreJob("RB-LUIS", "Other", "Analyst", JobImporter.IncomingSource, "https://example.com/luis"));
        ProfileContext.Close();

        ProfileContext.TryOpen(billy.ProfileId);
        var jobs = JobStore.GetJobs();
        Equal(1, jobs.Count, "Billy still has one job");
        Equal("RB-BILLY", jobs[0].JobId, "it is Billy's job");
        Equal("Acme", jobs[0].Company, "Billy's company");
        Equal(ApplicationStatus.Applied, jobs[0].ApplicationStatus, "Billy's application status");
        Equal(@"D:\Billy\Resume.docx", JobStore.LoadApplications().Single().DocxPath, "Billy's resume output");
        Equal("billy-prompt", File.ReadAllText(Path.Combine(PromptConversion.Root, "adapted-prompt.txt")), "Billy's prompt is unchanged");
    });

    static void ProfileMigrationKeepsTheOriginal() => UsingProfileRoot(() => {
        JobStore.DatabasePathOverride = ProfilePaths.LegacyDatabasePath;
        JobStore.SyncJob(StoreJob("RB-OLD", "MigrateCo", "Engineer", JobImporter.IncomingSource, "https://example.com/migrate/1"));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var settings = Path.Combine(ProfilePaths.GlobalRoot, "settings.json");
        var profileJson = Path.Combine(ProfilePaths.GlobalRoot, "candidate-profile.json");
        File.WriteAllText(settings, """{ "ResumeRootFolder": "D:\\Kept" }""");
        File.WriteAllText(profileJson, """{ "info": { "name": "Kept" } }""");
        var prompts = Path.Combine(ProfilePaths.GlobalRoot, "PromptAdaptation");
        Directory.CreateDirectory(prompts);
        File.WriteAllText(Path.Combine(prompts, "note.txt"), "kept");
        var beforeDb = File.ReadAllBytes(ProfilePaths.LegacyDatabasePath);
        var beforeSettings = File.ReadAllBytes(settings);
        JobStore.DatabasePathOverride = null;

        var result = ProfileMigration.MigrateIfNeeded();
        Equal(ProfileMigrationStatus.Migrated, result.Status, "the existing database becomes one profile");
        Check(ProfileIds.IsValid(result.ProfileId), "the migrated profile has an id");
        Check(File.ReadAllBytes(ProfilePaths.LegacyDatabasePath).AsSpan().SequenceEqual(beforeDb), "the original database is unchanged");
        Check(File.ReadAllBytes(settings).AsSpan().SequenceEqual(beforeSettings), "the original settings are unchanged");
        Equal(ProfileMigrationStatus.Already, ProfileMigration.MigrateIfNeeded().Status, "a second launch does not copy again");

        Check(ProfileContext.TryOpen(result.ProfileId!), "the migrated profile opens");
        Equal("MigrateCo", JobStore.GetJobs().Single().Company, "the jobs were copied");
        Equal("""{ "ResumeRootFolder": "D:\\Kept" }""", File.ReadAllText(ProfileContext.SettingsPath), "settings were copied");
        Equal("""{ "info": { "name": "Kept" } }""", File.ReadAllText(ProfileContext.CandidateProfilePath), "the candidate profile was copied");
        Equal("kept", File.ReadAllText(Path.Combine(ProfileContext.ProfileRoot, "PromptAdaptation", "note.txt")), "prompt adaptation was copied");
        var migratedId = result.ProfileId!;
        ProfileContext.Close();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Check(ProfileRegistry.Delete(migratedId, out var deleteError), "the migrated profile can be removed: " + deleteError);
        Equal(ProfileMigrationStatus.Already, ProfileMigration.MigrateIfNeeded().Status, "deleting the profile does not migrate again");
        Check(File.ReadAllBytes(ProfilePaths.LegacyDatabasePath).AsSpan().SequenceEqual(beforeDb), "the backup is still untouched");
    });

    static void ProfileLockIsOneWorkspace() {
        var id = "USR-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var first = ProfileLock.TryAcquire(id);
        Check(first is not null, "the first workspace locks the profile");
        Check(ProfileLock.TryAcquire(id) is null, "a second workspace is refused");
        Check(ProfileLock.IsInUse(id), "the profile is reported as open");
        first!.Dispose();
        var again = ProfileLock.TryAcquire(id);
        Check(again is not null, "the profile can open after the first workspace exits");
        again!.Dispose();
        Check(!ProfileWindow.Activate(id), "a profile with no workspace window is not treated as open");
    }

    static void ProfileLocksAreIndependent() {
        var billy = "USR-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var luis = "USR-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var first = ProfileLock.TryAcquire(billy);
        var second = ProfileLock.TryAcquire(luis);
        Check(first is not null && second is not null, "Billy and Luis can both be open");
        first!.Dispose();
        second!.Dispose();
    }

    static void ProfileRenameKeepsData() => UsingProfileRoot(() => {
        var created = ProfileRegistry.Create("Billy", null);
        var marker = Path.Combine(ProfilePaths.ProfileRoot(created.ProfileId), "marker.txt");
        File.WriteAllText(marker, "keep");
        var db = ProfilePaths.DatabasePath(created.ProfileId);
        Check(ProfileRegistry.Rename(created.ProfileId, "William"), "rename saves");
        var loaded = ProfileRegistry.Load().Single();
        Equal("William", loaded.DisplayName, "the display name changed");
        Equal(created.ProfileId, loaded.ProfileId, "the id did not change");
        Check(File.Exists(db), "the database is still in the same folder");
        Equal("keep", File.ReadAllText(marker), "files in the folder stay");
        var outside = Path.Combine(NewDir("documents"), "Resume.docx");
        File.WriteAllText(outside, "resume");
        Check(ProfileRegistry.Delete(created.ProfileId, out var error), "delete removes the profile: " + error);
        Check(!Directory.Exists(ProfilePaths.ProfileRoot(created.ProfileId)), "the profile folder is gone");
        Check(File.Exists(outside), "a resume outside the profile folder is kept");
    });

    static void ProfileChooserReloads() => UsingProfileRoot(() => {
        Check(ProfileLaunch.ProfileId(Array.Empty<string>()) is null, "no argument shows the chooser");
        Check(ProfileLaunch.ProfileId(new[] { "--profile", "../USR-ABCDEF12" }) is null, "a path is not a profile id");
        var billy = ProfileRegistry.Create("Billy", null);
        var luis = ProfileRegistry.Create("Luis", null);
        var loaded = ProfileRegistry.Load();
        Equal(2, loaded.Count, "both profiles are stored");
        var cards = ProfileCatalog.Cards();
        Equal(2, cards.Count, "the chooser lists both");
        Check(cards.Any(c => c.ProfileId == billy.ProfileId && c.DisplayName == "Billy"), "Billy is listed");
        Check(cards.Any(c => c.ProfileId == luis.ProfileId && c.DisplayName == "Luis"), "Luis is listed");
        Equal(billy.ProfileId, ProfileLaunch.ProfileId(new[] { "ResumeBuilder.exe", "--profile", billy.ProfileId.ToLowerInvariant() }),
            "a profile argument selects that id");
    });

    static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    static void ProfileAvatarIsPerProfile() => UsingProfileRoot(() => {
        var billy = ProfileRegistry.Create("Billy", null);
        var luis = ProfileRegistry.Create("Luis", null);
        var source = Path.Combine(NewDir("pictures"), "face.png");
        File.WriteAllBytes(source, TinyPng);
        Check(ProfileRegistry.TrySetAvatar(billy.ProfileId, source, out var error), "Billy's picture is stored: " + error);
        var stored = Path.Combine(ProfilePaths.ProfileRoot(billy.ProfileId), "avatar.png");
        Check(File.Exists(stored), "the copy is in Billy's profile folder");
        Check(File.ReadAllBytes(stored).AsSpan().SequenceEqual(TinyPng), "the copy matches the chosen picture");
        File.Delete(source);
        Check(File.Exists(stored), "the copy does not depend on the original file");
        Equal("avatar.png", ProfileRegistry.Find(billy.ProfileId)!.Avatar, "profiles.json stores the file name");
        Check(ProfileRegistry.Find(luis.ProfileId)!.Avatar is null, "Luis has no avatar reference");
        Check(!File.Exists(Path.Combine(ProfilePaths.ProfileRoot(luis.ProfileId), "avatar.png")), "Luis's folder has no avatar");
        Equal(stored, ProfileCatalog.Cards().Single(c => c.ProfileId == billy.ProfileId).AvatarFile, "the chooser shows Billy's file");
        Check(ProfileCatalog.Cards().Single(c => c.ProfileId == luis.ProfileId).AvatarFile is null, "the chooser shows Luis's initial");

        var jpeg = Path.Combine(NewDir("pictures-jpg"), "face.jpg");
        File.WriteAllBytes(jpeg, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
        Check(ProfileRegistry.TrySetAvatar(billy.ProfileId, jpeg, out error), "a JPEG replaces the PNG: " + error);
        Check(!File.Exists(stored), "the previous PNG is removed");
        Check(File.Exists(Path.Combine(ProfilePaths.ProfileRoot(billy.ProfileId), "avatar.jpg")), "the JPEG is the only avatar");
        Check(ProfileRegistry.Find(luis.ProfileId)!.Avatar is null, "replacing Billy's avatar leaves Luis alone");

        Check(ProfileRegistry.Rename(billy.ProfileId, "William"), "rename saves");
        var renamed = ProfileRegistry.Find(billy.ProfileId)!;
        Equal("William", renamed.DisplayName, "the display name changed");
        Equal("avatar.jpg", renamed.Avatar, "the avatar still belongs to the profile id");
        Check(File.Exists(Path.Combine(ProfilePaths.ProfileRoot(billy.ProfileId), "avatar.jpg")), "rename does not move the file");

        var marker = Path.Combine(ProfilePaths.ProfileRoot(billy.ProfileId), "marker.txt");
        File.WriteAllText(marker, "keep");
        Check(ProfileRegistry.ClearAvatar(billy.ProfileId), "the avatar is removed");
        Check(ProfileRegistry.Find(billy.ProfileId)!.Avatar is null, "the registry reference is cleared");
        Check(!File.Exists(Path.Combine(ProfilePaths.ProfileRoot(billy.ProfileId), "avatar.jpg")), "the avatar file is gone");
        Check(File.Exists(marker), "another file in the profile folder stays");
        Check(ProfileCatalog.Cards().Single(c => c.ProfileId == billy.ProfileId).AvatarFile is null, "the chooser returns to the initial");

        Check(ProfileRegistry.TrySetAvatar(billy.ProfileId, jpeg, out _), "the picture is stored again");
        File.WriteAllText(Path.Combine(ProfilePaths.ProfileRoot(billy.ProfileId), "avatar.jpg"), "not a picture");
        Check(ProfileRegistry.LoadableAvatar(ProfileRegistry.Find(billy.ProfileId)!.AvatarFile) is null, "a corrupt avatar is not shown");
        Check(ProfileCatalog.Cards().Single(c => c.ProfileId == billy.ProfileId).AvatarFile is null, "a corrupt avatar falls back to the initial");

        var missing = Path.Combine(NewDir("missing"), "gone.png");
        Check(!ProfileRegistry.TrySetAvatar(billy.ProfileId, missing, out error), "a missing file is refused");
        Equal("Unable to use this image.", error, "the message is short");
        var huge = Path.Combine(NewDir("huge"), "big.png");
        var bytes = new byte[2 * 1024 * 1024 + 1];
        TinyPng.CopyTo(bytes, 0);
        File.WriteAllBytes(huge, bytes);
        Check(!ProfileRegistry.TrySetAvatar(luis.ProfileId, huge, out error), "an oversized image is refused");
        Equal("Unable to use this image.", error, "oversized uses the same message");
        Check(ProfileRegistry.Find(luis.ProfileId)!.Avatar is null, "Luis still has no avatar");
    });

    static void SqliteMissingDocxDoesNotScan() {
        UsingStore(() => {
            var job = StoreJob("RB-MISS", "Acme", "Engineer", JobImporter.IncomingSource, "https://example.com/miss");
            JobStore.SyncJob(job);
            var missing = Path.Combine(NewDir("absent"), "Resume.docx");
            Check(!File.Exists(missing), "the DOCX is not on disk");
            JobStore.RecordResumeOutput(job.JobId, missing, null, Path.GetDirectoryName(missing));
            job.ResumePath = "";
            JobStore.ResetDirectoryEnumerations();
            JobStore.ApplyResumeLinks(new[] { job });
            Equal(missing, job.ResumePath, "the stored path is kept");
            Equal(0, JobStore.DirectoryEnumerations, "a missing file does not search the resume tree");
        });
    }

    // ---------- HTML round trip (parallel prototype; production generation does not call it) ----------

    static void HtmlRoundTripKeepsConstructedText() {
        var source = Path.Combine(NewDir("html-source"), "synthetic.docx");
        WriteSyntheticResume(source);
        var before = File.ReadAllBytes(source);
        var directory = NewDir("html-out");
        var result = HtmlRoundTrip.ConvertFile(source, directory);
        Check(File.ReadAllBytes(source).AsSpan().SequenceEqual(before), "the source DOCX is left untouched");
        Check(result.DocxPath != source, "the round trip writes a new file");
        SameVisibleText(source, result.DocxPath);
        var html = File.ReadAllText(result.HtmlPath);
        Check(!html.Contains("<w:", StringComparison.Ordinal), "the HTML is not Word markup");
        Check(!html.Contains("flex", StringComparison.Ordinal) && !html.Contains("grid", StringComparison.OrdinalIgnoreCase)
              && !html.Contains("position:", StringComparison.Ordinal) && !html.Contains("<script", StringComparison.Ordinal),
              "the HTML stays inside the supported subset");
        using var word = WordprocessingDocument.Open(result.DocxPath, false);
        var errors = new OpenXmlValidator(FileFormatVersions.Office2019).Validate(word)
            .Select(error => error.Description).ToList();
        Check(errors.Count == 0, "round-trip DOCX should be schema-valid: " + string.Join(" | ", errors.Take(6)));
        var body = word.MainDocumentPart!.Document!.Body!;
        Check(body.Descendants<W.Justification>().Any(j => j.Val?.Value == W.JustificationValues.Center), "the name stays centered");
        Check(body.Descendants<W.BottomBorder>().Any(), "the section rule is a real border");
        Check(body.Descendants<W.Shading>().Any(s => string.Equals(s.Fill?.Value, "F5F6FA", StringComparison.OrdinalIgnoreCase)), "the shaded block is kept");
        Check(word.MainDocumentPart.HyperlinkRelationships.Any(rel => rel.Uri.ToString().Contains("example.com", StringComparison.Ordinal)), "the hyperlink is a Word link");
        Check(word.MainDocumentPart.FooterParts.Any(part => part.Footer?.InnerText.Contains("Luis O Torres") == true), "the footer is a footer");
        Check(body.Descendants<W.Table>().Any(), "the skills layout stays a table");
        Check(body.Descendants<W.NumberingProperties>().Any(), "numbering stays numbering");
        Check(body.Descendants<W.Spacing>().Any(s => s.Val?.Value == 60), "letter spacing is kept");
        Check(!body.InnerText.Contains("SOURCE-ONLY-FOOTNOTE"), "a source footnote is not copied into the body");
    }

    static void HtmlRoundTripIsDeterministic() {
        var source = Path.Combine(NewDir("html-stable"), "synthetic.docx");
        WriteSyntheticResume(source);
        var first = HtmlRoundTrip.ConvertFile(source, NewDir("html-stable-a"));
        var second = HtmlRoundTrip.ConvertFile(source, NewDir("html-stable-b"));
        Equal(File.ReadAllText(first.HtmlPath), File.ReadAllText(second.HtmlPath), "normalized HTML");
    }

    static void HtmlRoundTripSamples() {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var samples = new[] {
            Path.Combine(documents, "BillyLin.docx"),
            Path.Combine(documents, "Luis_Torres.docx"),
            Path.Combine(documents, "Jeff_Fermon.docx"),
            Path.Combine(documents, "Clifton Isiah Collins.docx"),
            Path.Combine(documents, "Brandon Liu.docx")
        };
        var present = samples.Where(File.Exists).ToList();
        if (present.Count == 0) return;
        var directory = HtmlRoundTrip.PrototypeDirectory;
        foreach (var sample in present) {
            var before = File.ReadAllBytes(sample);
            var result = HtmlRoundTrip.ConvertFile(sample, directory);
            Check(File.ReadAllBytes(sample).AsSpan().SequenceEqual(before), Path.GetFileName(sample) + " was modified");
            SameVisibleText(sample, result.DocxPath);
            var unsupported = result.Unsupported.Distinct(StringComparer.Ordinal).ToList();
            Console.WriteLine($"        {Path.GetFileName(sample)} docx-to-html {result.DocxToHtmlMs} ms, html-to-docx {result.HtmlToDocxMs} ms, {result.HtmlBytes} bytes, unsupported {result.Unsupported.Count}{(unsupported.Count == 0 ? "" : " " + string.Join(",", unsupported))}");
            Console.WriteLine("        " + result.DocxPath);
            using var output = WordprocessingDocument.Open(result.DocxPath, false);
            var errors = new OpenXmlValidator(FileFormatVersions.Office2019).Validate(output).Take(4).Select(error => error.Description);
            Check(!errors.Any(), Path.GetFileName(sample) + " round trip is not schema-valid: " + string.Join(" | ", errors));
        }
    }

    static void EmailHtmlTailoring() {
        var reply = """
        {"sender":"Ada Lovelace","company":"Analytical Engines","jobTitle":"Engineer","jobDescription":"Build the engine.","emailUrl":"https://boards.greenhouse.io/example/jobs/1"}
        """;
        var draft = EmailExtractor.ParseReply(reply, "original email body", "", out var error);
        Check(draft is not null, error);
        Equal("Ada Lovelace", draft!.Sender, "sender");
        Equal("Analytical Engines", draft.Company, "company");
        Equal("Engineer", draft.JobTitle, "job title");
        Equal("Build the engine.", draft.JobDescription, "job description");
        Equal("https://boards.greenhouse.io/example/jobs/1", draft.EmailUrl, "email url");
        Check(draft.Id.StartsWith("ET-", StringComparison.Ordinal), "extraction id");

        var email = new EmailTask {
            Id = draft.Id,
            CreatedAt = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero),
            Sender = draft.Sender,
            Company = draft.Company,
            JobTitle = draft.JobTitle,
            JobDescription = draft.JobDescription,
            EmailUrl = draft.EmailUrl,
            OriginalText = "original email body",
            Status = EmailTaskStatus.Pending
        };
        var job = EmailOutput.Bind(email);
        Equal(email.Id, job.JobId, "one internal id");
        Equal(EmailOutput.SourceName, job.Source, "source");
        Equal("Email", job.JobSite, "job site");
        Equal("Queued", job.Status, "queue status");
        Equal(ApplicationStatus.Viewed, job.ApplicationStatus, "application starts at Viewed");
        Equal(email.EmailUrl, job.Link, "job url");
        var stamp = job.CreatedAt;
        job.ApplicationStatus = ApplicationStatus.Ready;
        var again = EmailOutput.Bind(email, job);
        Equal(email.Id, again.JobId, "the same id is reused");
        Equal(stamp, again.CreatedAt, "import time is not rewritten");
        Equal(ApplicationStatus.Ready, again.ApplicationStatus, "a stored application status stays");
        Equal("Queued", again.Status, "another tailor starts queued");
        Equal("", EmailOutput.Bind(new EmailTask { Id = "ET-blank", JobTitle = "Role", EmailUrl = "not a url" }).Link,
            "a non-web email url is not stored");

        UsingStore(() => {
            JobStore.EnsureReady();
            var created = EmailOutput.Bind(email);
            JobStore.UpsertTracked(created, "email");
            JobStore.UpsertTracked(EmailOutput.Bind(email, created), "email");
            Equal(1, Storage.LoadTasks().Count(item => item.JobId == email.Id), "tailoring again does not add a job");
            Equal(ApplicationStatus.Viewed, Storage.LoadTasks().Single(item => item.JobId == email.Id).ApplicationStatus, "the first record is Viewed");

            created.Status = "Failed";
            created.FailureReason = "GptInvalidOutput";
            JobStore.UpsertTracked(created, "email-failed");
            var failed = Storage.LoadTasks().Single(item => item.JobId == email.Id);
            Equal("Failed", failed.Status, "queue failure is stored");
            Equal(ApplicationStatus.Viewed, failed.ApplicationStatus, "queue failure does not fail the application");

            var resumes = Path.Combine(NewDir("email-resumes"), "Resumes");
            Directory.CreateDirectory(resumes);
            var settings = new AppSettings { ResumeRootFolder = resumes, Docx = true, Pdf = true };
            failed.Status = "Queued";
            var generation = EmailOutput.WriteResume(email, failed, settings, HtmlTailorSample());
            Check(generation.DocxGenerated, generation.DocxError ?? "docx was not written");
            Check(generation.PdfGenerated, generation.PdfError ?? "pdf was not written");
            Check(File.Exists(generation.DocxPath), "docx file");
            Check(File.Exists(generation.PdfPath), "pdf file");
            Check((generation.OutputFolder ?? "").Contains("Manual", StringComparison.OrdinalIgnoreCase), "email resumes stay in the Manual folder");
            JobTracker.MarkResumeReady(failed, generation.DocxPath);
            JobStore.CommitResume(failed, generation.DocxPath, generation.PdfPath, generation.OutputFolder);
            var ready = Storage.LoadTasks().Single(item => item.JobId == email.Id);
            Equal(ApplicationStatus.Ready, ready.ApplicationStatus, "success moves Viewed to Ready");
            Check(ready.ReadyAt is not null, "ReadyAt is set");
            Equal(1, Storage.LoadTasks().Count(item => item.JobId == email.Id), "resume save does not add a job");
            email.ApplyOutput(null, null, null);
            Check(EmailOutput.ApplyStoredResume(email), "stored paths are found");
            Equal(generation.DocxPath, email.DocxPath, "resume action path");
            Equal(generation.PdfPath, email.PdfPath, "pdf action path");
            Equal(generation.OutputFolder, email.OutputFolder, "folder action path");
            Check(email.CanOpenResume && email.CanOpenPdf && email.CanOpenFolder, "email actions can open the stored files");

            var retry = EmailOutput.Bind(email, ready);
            Equal(email.Id, retry.JobId, "retry keeps the id");
            Equal(ApplicationStatus.Ready, retry.ApplicationStatus, "retry does not reset Ready");
            JobStore.UpsertTracked(retry, "email");
            Equal(1, Storage.LoadTasks().Count(item => item.JobId == email.Id), "retry does not add a job");
        });

        WithPreparedFiles(() => {
            var previous = HtmlTailor.OriginalCacheRoot;
            HtmlTailor.OriginalCacheRoot = NewDir("email-html-cache");
            try {
                var docx = Path.Combine(NewDir("email-original"), "original.docx");
                WriteSyntheticResume(docx);
                var prompt = Path.Combine(NewDir("email-prompt"), "prompt.txt");
                File.WriteAllText(prompt, "Tailor the resume to the job.");
                var profile = Path.Combine(NewDir("email-profile"), "profile.json");
                File.WriteAllText(profile, """{"info":{"name":"Ada Lovelace"},"summary":"","skills":[],"experience":[],"education":[],"certifications":[]}""");
                var settings = new AppSettings {
                    PromptMode = PromptModes.Resume,
                    MasterPrompt = prompt,
                    OriginalResume = docx,
                    StyleReferenceResume = Path.Combine(NewDir("email-style"), "style.docx"),
                    CandidateProfile = profile,
                    ResumeRootFolder = Path.Combine(NewDir("email-root2"), "Resumes"),
                    Docx = true,
                    Pdf = true
                };
                File.WriteAllBytes(settings.StyleReferenceResume, new byte[] { 1, 2, 3 });
                var prepared = EmailOutput.PrepareRun(EmailOutput.Bind(email), settings);
                Check(prepared.HtmlTailoring, "email tailoring uses HTML");
                Check(prepared.Text.Contains("===== RESUME HTML =====", StringComparison.Ordinal), "the HTML resume is sent");
                Check(prepared.Text.Contains("Do not return JSON.", StringComparison.Ordinal), "the shared HTML contract is sent");
                Check(!prepared.Text.Contains("===== COMPLETE JOB PAYLOAD =====", StringComparison.Ordinal), "the JSON resume payload is not sent");
                Equal(Path.GetFullPath(docx), HtmlTailor.OriginalResumeFile(settings), "Original Resume is the source");
                var second = EmailOutput.PrepareRun(EmailOutput.Bind(email), settings);
                Check(second.HtmlTailoring, "retry uses the HTML path");
                Equal(email.Id, second.JobId, "retry keeps the same job");
                var ordinary = new JobTask { JobId = "RB-ORDINARY", Company = "Acme", Title = "Engineer", Jd = "Build things.", Link = "https://example.com/job" };
                var jobPrepared = EmailOutput.PrepareRun(ordinary, settings);
                Check(jobPrepared.HtmlTailoring, "a job task uses HTML");
                Check(jobPrepared.Text.Contains("===== RESUME HTML =====", StringComparison.Ordinal), "a job task sends the resume HTML");
                Check(!jobPrepared.Text.Contains("===== COMPLETE JOB PAYLOAD =====", StringComparison.Ordinal), "a job task does not request resume JSON");
                var manual = EmailOutput.PrepareRun(new JobTask { JobId = "RB-MANUAL", Company = "Acme", Title = "Engineer", Jd = "Build things.", Link = "https://example.com/manual" }, settings);
                Check(manual.HtmlTailoring, "a selected job uses HTML");
                Check(!manual.Text.Contains("===== COMPLETE JOB PAYLOAD =====", StringComparison.Ordinal), "a selected job does not request resume JSON");
            } finally {
                HtmlTailor.OriginalCacheRoot = previous;
                foreach (var id in new[] { email.Id, "RB-ORDINARY", "RB-MANUAL" }) {
                    var dir = HtmlTailor.JobDirectory(id);
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                }
            }
        });
    }

    static void HtmlTailorBilly() {
        Check(!new PreparedRequest().HtmlTailoring, "only HtmlTailor marks a request as HTML");
        Check(ChatResponseReader.ScriptIsReadOnly(ChatResponseReader.ReadLastAssistantHtmlScript), "HTML reader is read-only");
        Check(ChatResponseReader.ReadLastAssistantHtmlScript.Contains("<html", StringComparison.Ordinal), "HTML reader looks for html");
        Check(ChatResponseReader.ReadLastAssistantScript.Contains("no-profile-json", StringComparison.Ordinal), "JSON reader is unchanged");

        var source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BillyLin.docx");
        Check(File.Exists(source), "BillyLin.docx was not found");
        var before = File.ReadAllBytes(source);
        var html = HtmlResumeHtml.Normalize(HtmlResumeHtml.Emit(DocxHtmlReader.Read(source)));
        Check(File.ReadAllBytes(source).AsSpan().SequenceEqual(before), "reading the source DOCX modified it");

        const string jobId = "BillyLin";
        HtmlTailor.WriteSource(jobId, html);
        var folder = HtmlTailor.JobDirectory(jobId);
        var sourceHtml = Path.Combine(folder, "source.html");
        var sourceHtmlBytes = File.ReadAllBytes(sourceHtml);

        var tailored = html
            .Replace("Senior Full-Stack &amp; AI/ML Software Engineer", "TAILORED_HEADLINE")
            .Replace("Senior AI/ML and software engineering leader", "TAILORED_SUMMARY")
            .Replace("Prompt Engineering", "TAILORED_SKILL")
            .Replace("Caterpillar Inc. | Senior AI Software Engineer | Sep 2025 - Apr 2026",
                "Changed Employer Inc. | TAILORED_TITLE | Jan 1999 - Apr 2026")
            .Replace("California, United States | Contract | Remote", "California, United States | Contract | ONSITE")
            .Replace("University of California, Berkeley", "Changed School")
            .Replace(">BILLY LIN<", ">CHANGED NAME<")
            .Replace("reducing experiment turnaround time by 40%", "TAILORED_BULLET reducing experiment turnaround time by 40%");
        Check(tailored.Contains("TAILORED_HEADLINE") && tailored.Contains("TAILORED_BULLET"), "the sample text was not found to tailor");

        var accepted = HtmlTailor.Accept(tailored, jobId);
        Check(accepted.Ok, accepted.Error);
        var result = accepted.Html;
        Check(result.Contains("TAILORED_HEADLINE"), "headline was not tailored");
        Check(result.Contains("TAILORED_SUMMARY"), "summary was not tailored");
        Check(result.Contains("TAILORED_SKILL"), "skills were not tailored");
        Check(result.Contains("TAILORED_TITLE"), "job title was not tailored");
        Check(result.Contains("TAILORED_BULLET"), "bullet was not tailored");
        Check(result.Contains("BILLY LIN"), "name was not restored");
        Check(!result.Contains("CHANGED NAME"), "changed name remained");
        Check(result.Contains("Caterpillar Inc."), "company was not restored");
        Check(!result.Contains("Changed Employer Inc."), "changed company remained");
        Check(result.Contains("Sep 2025"), "start date was not restored");
        Check(!result.Contains("Jan 1999"), "changed date remained");
        Check(result.Contains("California, United States | Contract | Remote"), "work arrangement was not restored");
        Check(!result.Contains("ONSITE"), "changed work arrangement remained");
        Check(result.Contains("University of California, Berkeley"), "education was not restored");
        Check(!result.Contains("Changed School"), "changed school remained");
        Check(result.Contains("border-bottom: 1pt solid #2F5597"), "heading border CSS was lost");
        Check(result.Contains("font-family: Arial"), "Arial was lost");
        Check(result.Contains("size: 612pt 792pt"), "page size was lost");
        Check(File.ReadAllBytes(sourceHtml).AsSpan().SequenceEqual(sourceHtmlBytes), "source.html was overwritten");

        var goodTailored = File.ReadAllBytes(accepted.SavedHtmlPath);
        var sentinel = Path.Combine(folder, "already-good.docx");
        File.WriteAllBytes(sentinel, new byte[] { 1, 2, 3, 4 });
        var rejected = html.Replace("</body>", "<script>bad()</script></body>");
        var refused = HtmlTailor.Accept(rejected, jobId);
        Check(!refused.Ok, "a script was accepted");
        Check(File.ReadAllBytes(accepted.SavedHtmlPath).AsSpan().SequenceEqual(goodTailored), "a failed answer overwrote tailored.html");
        Check(File.ReadAllBytes(sourceHtml).AsSpan().SequenceEqual(sourceHtmlBytes), "a failed answer overwrote source.html");
        Check(File.ReadAllBytes(sentinel).AsSpan().SequenceEqual(new byte[] { 1, 2, 3, 4 }), "a failed answer overwrote an existing resume");
        File.Delete(sentinel);

        var styled = html.Replace("body { font-family: Arial; font-size: 12pt; }",
            "body { font-family: Arial; font-size: 12pt; position: absolute; }");
        var styledRefusal = HtmlTailor.Accept(styled, jobId);
        Check(!styledRefusal.Ok, "unsupported CSS was accepted");
        Check(File.ReadAllBytes(accepted.SavedHtmlPath).AsSpan().SequenceEqual(goodTailored), "unsupported CSS overwrote tailored.html");

        Check(HtmlTailor.TryExtract("```html\n" + result + "\n```", out var fenced, out var fenceError), fenceError);
        Check(fenced.Contains("<html", StringComparison.OrdinalIgnoreCase), "fenced HTML was not extracted");
        Check(!HtmlTailor.TryExtract("```html\n<html></html>\n```\n```html\n<html></html>\n```", out _, out _), "two HTML blocks were accepted");
        Check(!HtmlTailor.TryValidate("<html><p></body>", out _), "malformed HTML was accepted");

        var docx = Path.Combine(folder, "BILLY LIN.docx");
        var pdf = Path.Combine(folder, "BILLY LIN.pdf");
        if (File.Exists(docx)) File.Delete(docx);
        if (File.Exists(pdf)) File.Delete(pdf);
        HtmlTailor.WriteDocx(result, docx);
        using (var output = WordprocessingDocument.Open(docx, false)) {
            var errors = new OpenXmlValidator(FileFormatVersions.Office2019).Validate(output).Take(4).Select(error => error.Description);
            Check(!errors.Any(), "tailored DOCX is not schema-valid: " + string.Join(" | ", errors));
        }
        var visible = HtmlRoundTrip.VisibleText(docx);
        Check(visible.Contains("TAILORED_SUMMARY"), "DOCX is missing the tailored summary");
        Check(visible.Contains("TAILORED_TITLE"), "DOCX is missing the tailored job title");
        Check(visible.Contains("BILLY LIN"), "DOCX is missing the name");
        Check(visible.Contains("Caterpillar Inc."), "DOCX is missing the company");
        Check(!visible.Contains("Changed Employer"), "DOCX kept the changed company");
        Check(File.ReadAllBytes(source).AsSpan().SequenceEqual(before), "the original DOCX was modified");
        PdfWriter.ConvertDocx(docx, pdf);
        Check(new FileInfo(pdf).Length > 0, "PDF was empty");
        var rejectedFile = Path.Combine(folder, "tailored.rejected.html");
        if (File.Exists(rejectedFile)) File.Delete(rejectedFile);
        Console.WriteLine("        " + sourceHtml);
        Console.WriteLine("        " + accepted.SavedHtmlPath);
        Console.WriteLine("        " + docx);
        Console.WriteLine("        " + pdf);
    }

    static void WordPdfConversionClosesWord() {
        var before = WinwordPids();
        var dir = NewDir("word-pdf");
        var docx = Path.Combine(dir, "resume.docx");
        WriteSyntheticResume(docx);
        for (var i = 1; i <= 3; i++) {
            var pdf = Path.Combine(dir, "resume-" + i + ".pdf");
            PdfWriter.ConvertDocx(docx, pdf);
            Check(new FileInfo(pdf).Length > 0, "PDF " + i + " was empty");
        }
        var orphan = WinwordPids().Where(id => !before.Contains(id)).ToList();
        Check(orphan.Count == 0, "Word stayed open after PDF conversion: " + string.Join(",", orphan));
    }

    static HashSet<int> WinwordPids() {
        var ids = new HashSet<int>();
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("WINWORD")) {
            try { ids.Add(process.Id); }
            finally { process.Dispose(); }
        }
        return ids;
    }

    static void HtmlOriginalResumeIsTheOnlySource() {
        var style = Path.Combine(NewDir("style-ref"), "style.docx");
        File.WriteAllBytes(style, new byte[] { 1, 2, 3, 4 });
        var original = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BillyLin.docx");
        Check(File.Exists(original), "BillyLin.docx was not found");
        var settings = new AppSettings { OriginalResume = original, StyleReferenceResume = style };
        Equal(Path.GetFullPath(original), HtmlTailor.OriginalResumeFile(settings), "the Original Resume is the HTML source");
        Check(settings.StyleReferenceResume == style, "a stored style reference is not the HTML source");
        try {
            HtmlTailor.OriginalResumeFile(new AppSettings { OriginalResume = "", StyleReferenceResume = style });
            Check(false, "a style reference must not supply the HTML resume");
        } catch (InvalidOperationException ex) {
            Check(ex.Message.Contains("Original Resume", StringComparison.Ordinal), ex.Message);
        }
    }

    static void HtmlOriginalResumeRegenerates() {
        var previous = HtmlTailor.OriginalCacheRoot;
        HtmlTailor.OriginalCacheRoot = NewDir("html-original");
        try {
            var docx = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BillyLin.docx");
            Check(File.Exists(docx), "BillyLin.docx was not found");
            var first = HtmlTailor.NormalizedOriginal(docx);
            var htmlPath = Path.Combine(HtmlTailor.OriginalCacheRoot, "original-resume.html");
            var metaPath = Path.Combine(HtmlTailor.OriginalCacheRoot, "original-resume.json");
            Check(File.Exists(htmlPath) && File.Exists(metaPath), "normalized HTML was stored");
            Equal(first, File.ReadAllText(htmlPath), "the stored HTML is the normalized resume");
            File.SetLastWriteTimeUtc(htmlPath, DateTime.UtcNow.AddDays(-2));
            var stamp = File.GetLastWriteTimeUtc(htmlPath);
            Equal(first, HtmlTailor.NormalizedOriginal(docx), "an unchanged resume reuses its HTML");
            Equal(stamp.Ticks.ToString(), File.GetLastWriteTimeUtc(htmlPath).Ticks.ToString(), "an unchanged resume does not rewrite its HTML");
            var hash = PromptConversion.HashFile(docx);
            File.WriteAllText(metaPath, File.ReadAllText(metaPath).Replace(hash, "0000", StringComparison.Ordinal));
            Equal(first, HtmlTailor.NormalizedOriginal(docx), "a changed resume regenerates the same document");
            Check(File.ReadAllText(metaPath).Contains(hash, StringComparison.Ordinal), "the regenerated HTML records the new file hash");
        } finally {
            HtmlTailor.OriginalCacheRoot = previous;
        }
    }

    static void HtmlTailorLocksStyleAndSectionOrder() {
        var source = HtmlTailorSample();
        Check(HtmlTailor.TryValidate(source, out var valid), valid);
        var facts = new HtmlLockedFacts();
        var fewerBullets = source.Replace("<p class=\"a\">• Second bullet.</p>", "", StringComparison.Ordinal);
        var restored = HtmlTailor.Restore(source, fewerBullets, facts);
        Check(!restored.Contains("• Second bullet.", StringComparison.Ordinal), "a removed bullet was put back");
        Check(restored.Contains("• First bullet.", StringComparison.Ordinal), "the remaining bullet was kept");
        Check(restored.Contains("font-size: 11pt", StringComparison.Ordinal), "the style block stayed");

        var recolored = source.Replace("font-size: 11pt", "font-size: 14pt", StringComparison.Ordinal);
        var styled = HtmlTailor.Restore(source, recolored, facts);
        Check(styled.Contains("font-size: 11pt", StringComparison.Ordinal), "a changed style block was restored");
        Check(!styled.Contains("font-size: 14pt", StringComparison.Ordinal), "the changed style remained");

        var extraSkill = source.Replace("<p class=\"a\">Prompt Engineering</p>",
            "<p class=\"a\">Prompt Engineering</p><p class=\"a\">Added skill</p>", StringComparison.Ordinal);
        var skills = HtmlTailor.Restore(source, extraSkill, facts);
        Check(skills.Contains("Added skill", StringComparison.Ordinal), "a new skill item was rejected");

        try {
            HtmlTailor.Restore(source, source.Replace("class=\"a\">Added", "class=\"zz\">Added", StringComparison.Ordinal)
                .Replace("<p class=\"a\">Prompt Engineering</p>", "<p class=\"a\">Prompt Engineering</p><p class=\"zz\">Added skill</p>", StringComparison.Ordinal), facts);
            Check(false, "a new CSS class was accepted");
        } catch (InvalidDataException ex) {
            Equal("new-class", ex.Message, "a new class is rejected");
        }

        var swapped = source.Replace("<h1 class=\"a\">PROFESSIONAL EXPERIENCE</h1>", "<h1 class=\"a\">MARKER</h1>", StringComparison.Ordinal)
            .Replace("<h1 class=\"a\">EDUCATION</h1>", "<h1 class=\"a\">PROFESSIONAL EXPERIENCE</h1>", StringComparison.Ordinal)
            .Replace("<h1 class=\"a\">MARKER</h1>", "<h1 class=\"a\">EDUCATION</h1>", StringComparison.Ordinal);
        var reordered = HtmlTailor.Restore(source, swapped, facts);
        Check(reordered.Contains("EDUCATION", StringComparison.Ordinal) && reordered.Contains("PROFESSIONAL EXPERIENCE", StringComparison.Ordinal),
            "a section edit was rejected");
        Check(reordered.Contains("font-size: 11pt", StringComparison.Ordinal), "style was not restored after a section edit");
    }

    static void HtmlLineHeightUsesMultipleSpacing() {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ResumeBuilder", "HtmlTailoring", "RB-20260927-010753-7ee075d6");
        var sourceHtml = Path.Combine(folder, "source.html");
        Check(File.Exists(sourceHtml), "the overlapping resume HTML is missing");
        var docx = Path.Combine(folder, "line-spacing.docx");
        if (File.Exists(docx)) File.Delete(docx);
        HtmlTailor.WriteDocx(File.ReadAllText(sourceHtml), docx);
        string xml;
        using (var output = WordprocessingDocument.Open(docx, false)) {
            var document = output.MainDocumentPart?.Document;
            Check(document is not null, "the DOCX has no document");
            xml = document?.OuterXml ?? "";
        }
        Check(!xml.Contains("lineRule=\"exact\"", StringComparison.Ordinal), "unitless line-height was written as exact spacing");
        Check(!xml.Contains("w:line=\"20\"", StringComparison.Ordinal), "line-height 1 became 1pt");
        Check(xml.Contains("w:line=\"240\"", StringComparison.Ordinal), "line-height 1 is single spacing");
        Check(xml.Contains("w:line=\"247\"", StringComparison.Ordinal), "line-height 1.029 is a multiplier");
        Check(xml.Contains("w:before=", StringComparison.Ordinal) && xml.Contains("w:after=", StringComparison.Ordinal),
            "paragraph spacing was dropped");
        Check(!xml.Contains("trHeight", StringComparison.Ordinal), "a table row height was written");
        Console.WriteLine("        " + docx);
    }

    static string HtmlTailorSample() =>
        "<html><head><style>\n"
        + "body { font-family: Arial; font-size: 12pt; }\n"
        + ".a { font-size: 11pt; }\n"
        + "@page { size: 612pt 792pt; margin: 36pt; }\n"
        + "</style></head><body>\n"
        + "<p class=\"a\">BILLY LIN</p>\n"
        + "<h1 class=\"a\">PROFESSIONAL SUMMARY</h1>\n"
        + "<p class=\"a\">Summary text here.</p>\n"
        + "<h1 class=\"a\">TECHNICAL SKILLS</h1>\n"
        + "<p class=\"a\">Prompt Engineering</p>\n"
        + "<h1 class=\"a\">PROFESSIONAL EXPERIENCE</h1>\n"
        + "<p class=\"a\">Caterpillar Inc. | Engineer | Sep 2025 - Apr 2026</p>\n"
        + "<p class=\"a\">• First bullet.</p>\n"
        + "<p class=\"a\">• Second bullet.</p>\n"
        + "<p class=\"a\">California, United States | Contract | Remote</p>\n"
        + "<h1 class=\"a\">EDUCATION</h1>\n"
        + "<p class=\"a\">University of California, Berkeley</p>\n"
        + "<h1 class=\"a\">CERTIFICATIONS</h1>\n"
        + "<p class=\"a\">• Sample certification</p>\n"
        + "</body></html>";

    static void HtmlTailorRejectedResume() {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ResumeBuilder", "HtmlTailoring", "RB-20260927-010753-7ee075d6");
        var sourceHtml = Path.Combine(folder, "source.html");
        var rejectedHtml = Path.Combine(folder, "tailored.rejected.html");
        Check(File.Exists(sourceHtml) && File.Exists(rejectedHtml), "the rejected HTML sample is missing");
        var sourceBytes = File.ReadAllBytes(sourceHtml);
        var accepted = HtmlTailor.Accept(File.ReadAllText(rejectedHtml), "RB-20260927-010753-7ee075d6");
        Check(accepted.Ok, accepted.Error);
        Check(File.ReadAllBytes(sourceHtml).AsSpan().SequenceEqual(sourceBytes), "source.html was overwritten");
        Check(accepted.Html.Contains("Senior Backend Engineer"), "the tailored headline was removed");
        Check(accepted.Html.Contains("Brandon Liu"), "the name was lost");
        Check(accepted.Html.Contains("ServiceNow"), "the employer was lost");
        Check(accepted.Html.Contains("July 2026"), "the employment date was lost");
        Check(accepted.Html.Contains("San Diego, CA"), "the location was lost");
        Check(accepted.Html.Contains("University of California, San Diego"), "education was lost");
        Check(accepted.Html.Contains("Google Cloud Professional Machine Learning Engineer"), "a certification was lost");
        var docx = Path.Combine(folder, "tailored.docx");
        if (File.Exists(docx)) File.Delete(docx);
        HtmlTailor.WriteDocx(accepted.Html, docx);
        using (var output = WordprocessingDocument.Open(docx, false)) {
            var errors = new OpenXmlValidator(FileFormatVersions.Office2019).Validate(output).Take(4).Select(error => error.Description);
            Check(!errors.Any(), "tailored DOCX is not schema-valid: " + string.Join(" | ", errors));
        }
        var visible = HtmlRoundTrip.VisibleText(docx);
        Check(visible.Contains("Senior Backend Engineer"), "DOCX is missing the tailored headline");
        Check(visible.Contains("Brandon Liu"), "DOCX is missing the name");
        Check(visible.Contains("ServiceNow"), "DOCX is missing the employer");
        Check(visible.Contains("July 2026"), "DOCX is missing the date");
        Console.WriteLine("        source-elements=" + CountTags(sourceHtml) + " returned-elements=" + CountTags(rejectedHtml));
        Console.WriteLine("        " + accepted.SavedHtmlPath);
        Console.WriteLine("        " + docx);
    }

    static int CountTags(string path) {
        var html = File.ReadAllText(path);
        var body = html.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        if (body < 0) return 0;
        return System.Text.RegularExpressions.Regex.Matches(html[body..], @"<[A-Za-z][\w:-]*").Count;
    }

    static void SameVisibleText(string source, string output) {
        var left = HtmlRoundTrip.VisibleText(source);
        var right = HtmlRoundTrip.VisibleText(output);
        if (left == right) return;
        var index = 0;
        while (index < left.Length && index < right.Length && left[index] == right[index]) index++;
        string Snip(string text) {
            var start = Math.Max(0, index - 24);
            var length = Math.Min(48, text.Length - start);
            return length <= 0 ? "" : text.Substring(start, length).Replace("\n", "\\n").Replace("\t", "\\t");
        }
        throw new Exception($"text differs at {index} (source {left.Length}, output {right.Length}): [{Snip(left)}] vs [{Snip(right)}]");
    }

    static void WriteSyntheticResume(string path) {
        using var word = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = word.AddMainDocumentPart();
        var numbering = main.AddNewPart<NumberingDefinitionsPart>();
        numbering.Numbering = new W.Numbering(
            new W.AbstractNum(
                new W.Nsid { Val = "00ABCDEF" },
                new W.MultiLevelType { Val = W.MultiLevelValues.HybridMultilevel },
                new W.Level(
                    new W.StartNumberingValue { Val = 1 },
                    new W.NumberingFormat { Val = W.NumberFormatValues.Bullet },
                    new W.LevelText { Val = "•" },
                    new W.LevelJustification { Val = W.LevelJustificationValues.Left }
                ) { LevelIndex = 0 }
            ) { AbstractNumberId = 1 },
            new W.NumberingInstance(new W.AbstractNumId { Val = 1 }) { NumberID = 1 });
        var styles = main.AddNewPart<StyleDefinitionsPart>();
        styles.Styles = new W.Styles(
            new W.DocDefaults(new W.RunPropertiesDefault(new W.RunPropertiesBaseStyle(
                new W.RunFonts { Ascii = "Arial", HighAnsi = "Arial" },
                new W.FontSize { Val = "22" }))),
            new W.Style(
                new W.StyleName { Val = "Normal" },
                new W.StyleRunProperties(new W.RunFonts { Ascii = "Arial", HighAnsi = "Arial" }, new W.FontSize { Val = "22" })
            ) { Type = W.StyleValues.Paragraph, StyleId = "Normal", Default = true },
            new W.Style(
                new W.StyleName { Val = "heading 1" },
                new W.BasedOn { Val = "Normal" },
                new W.StyleParagraphProperties(new W.OutlineLevel { Val = 0 }),
                new W.StyleRunProperties(new W.Bold(), new W.Color { Val = "2F5597" }, new W.FontSize { Val = "24" })
            ) { Type = W.StyleValues.Paragraph, StyleId = "Heading1" });
        var footer = main.AddNewPart<FooterPart>();
        footer.Footer = new W.Footer(new W.Paragraph(
            new W.Run(new W.Text("Luis O Torres | Page ") { Space = SpaceProcessingModeValues.Preserve }),
            new W.Run(new W.FieldChar { FieldCharType = W.FieldCharValues.Begin }),
            new W.Run(new W.FieldCode(" PAGE ") { Space = SpaceProcessingModeValues.Preserve }),
            new W.Run(new W.FieldChar { FieldCharType = W.FieldCharValues.Separate }),
            new W.Run(new W.Text("1")),
            new W.Run(new W.FieldChar { FieldCharType = W.FieldCharValues.End })));
        var footnote = main.AddNewPart<FootnotesPart>();
        footnote.Footnotes = new W.Footnotes(new W.Footnote(
            new W.Paragraph(new W.Run(new W.Text("SOURCE-ONLY-FOOTNOTE")))
        ) { Id = 1 });
        var body = new W.Body();
        body.Append(new W.Paragraph(
            new W.ParagraphProperties(new W.Justification { Val = W.JustificationValues.Center },
                new W.SpacingBetweenLines { Before = "0", After = "0", Line = "240", LineRule = W.LineSpacingRuleValues.Auto }),
            new W.Run(new W.RunProperties(new W.Bold(), new W.FontSize { Val = "32" }, new W.RunFonts { Ascii = "Arial", HighAnsi = "Arial" }),
                new W.Text("BILLY LIN"))));
        body.Append(new W.Paragraph(new W.ParagraphProperties(
            new W.SpacingBetweenLines { Before = "0", After = "0", Line = "240", LineRule = W.LineSpacingRuleValues.Auto })));
        body.Append(new W.Paragraph(
            new W.ParagraphProperties(
                new W.ParagraphStyleId { Val = "Heading1" },
                new W.ParagraphBorders(new W.BottomBorder { Val = W.BorderValues.Single, Size = 8, Space = 1, Color = "2F5597" })),
            new W.Run(new W.RunProperties(new W.RunFonts { Ascii = "Arial", HighAnsi = "Arial" }, new W.FontSize { Val = "24" }),
                new W.Text("PROFESSIONAL SUMMARY"))));
        body.Append(new W.Paragraph(
            new W.ParagraphProperties(new W.Shading { Val = W.ShadingPatternValues.Clear, Fill = "F5F6FA" }),
            new W.Run(new W.RunProperties(new W.Italic(), new W.FontSize { Val = "18" }, new W.RunFonts { Ascii = "Roboto", HighAnsi = "Roboto" }),
                new W.Text("Staff-level ") { Space = SpaceProcessingModeValues.Preserve }),
            new W.Run(new W.RunProperties(new W.Bold(), new W.Italic(), new W.FontSize { Val = "18" }, new W.RunFonts { Ascii = "Roboto", HighAnsi = "Roboto" }),
                new W.Text("AI/ML Engineer")),
            new W.Run(new W.RunProperties(new W.Italic(), new W.FontSize { Val = "18" }, new W.RunFonts { Ascii = "Roboto", HighAnsi = "Roboto" }),
                new W.Text(" who builds systems."))));
        body.Append(new W.Paragraph(new W.Run(
            new W.RunProperties(new W.Spacing { Val = 60 }, new W.Bold(), new W.FontSize { Val = "28" }, new W.RunFonts { Ascii = "Roboto", HighAnsi = "Roboto" }),
            new W.Text("CLIFTON"))));
        body.Append(new W.Paragraph(
            new W.ParagraphProperties(new W.NumberingProperties(new W.NumberingLevelReference { Val = 0 }, new W.NumberingId { Val = 1 })),
            new W.Run(new W.Text("Shipped the platform"))));
        body.Append(new W.Paragraph(new W.Run(new W.Text("• React, TypeScript"))));
        var link = main.AddHyperlinkRelationship(new Uri("https://example.com/portfolio"), true);
        body.Append(new W.Paragraph(new W.Hyperlink(
            new W.Run(new W.RunProperties(new W.Underline { Val = W.UnderlineValues.Single }, new W.Color { Val = "0563C1" }),
                new W.Text("portfolio"))
        ) { Id = link.Id, History = true }));
        body.Append(new W.Paragraph(
            new W.ParagraphProperties(new W.Tabs(new W.TabStop { Val = W.TabStopValues.Right, Position = 9360 })),
            new W.Run(new W.Text("Role")),
            new W.Run(new W.TabChar()),
            new W.Run(new W.Text("2024"))));
        body.Append(new W.Table(
            new W.TableProperties(new W.TableWidth { Width = "9000", Type = W.TableWidthUnitValues.Dxa }),
            new W.TableGrid(new W.GridColumn { Width = "2400" }, new W.GridColumn { Width = "6600" }),
            new W.TableRow(
                new W.TableCell(new W.TableCellProperties(new W.TableCellWidth { Width = "2400", Type = W.TableWidthUnitValues.Dxa }),
                    new W.Paragraph(new W.Run(new W.RunProperties(new W.Bold()), new W.Text("Languages:")))),
                new W.TableCell(new W.TableCellProperties(new W.TableCellWidth { Width = "6600", Type = W.TableWidthUnitValues.Dxa }),
                    new W.Paragraph(new W.Run(new W.Text("C#, Python")))))));
        body.Append(new W.SectionProperties(
            new W.FooterReference { Type = W.HeaderFooterValues.Default, Id = main.GetIdOfPart(footer) },
            new W.PageSize { Width = 12240, Height = 15840 },
            new W.PageMargin { Top = 720, Bottom = 720, Left = 720, Right = 720, Header = 360, Footer = 360, Gutter = 0 }));
        main.Document = new W.Document(body);
        main.Document.Save();
    }

    static string Fixture(string name) => Path.Combine(Root(), "tests", "fixtures", name);

    static string NewDir(string name) {
        var path = Path.Combine(TempRoot, name);
        Directory.CreateDirectory(path);
        return path;
    }

    static string Join(IEnumerable<string> values) => string.Join(" | ", values);

    static void Test(string name, Action body) {
        if (OnlyTest is not null && name.IndexOf(OnlyTest, StringComparison.OrdinalIgnoreCase) < 0) return;
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

    /// <summary>
    /// Every filter off. The import tests that predate the import filter say so explicitly rather
    /// than relying on a default, so a future default change cannot silently alter what they prove.
    /// </summary>
    static AppSettings NoFilters => JobImportFilter.NoFilters();

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
