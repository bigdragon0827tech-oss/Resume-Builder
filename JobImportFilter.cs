using System.Text;

namespace ResumeBuilder;

// ---------------------------------------------------------------------------
// The job import filter: the ONE place that decides whether an extracted job is wanted.
//
// It is pure — data and settings in, a decision out. It never touches storage, the queue, a JobTask
// or the UI, so every import path can share it and every rule can be tested without a browser.
// JobImporter.ImportOne is the single gate that calls it; nothing else re-implements a rule.
//
// Deliberately NOT keyword matching. "security" is a whole industry, and a cloud security
// certification is not a clearance. Every rule needs a specific SUBJECT (a clearance name, the word
// citizen, a sponsorship term) and, for the weaker subjects, applicant-directed REQUIREMENT
// language close to it in the same sentence.
// ---------------------------------------------------------------------------

/// <summary>Why an extracted job was not imported. <see cref="None"/> means it was accepted.</summary>
public enum JobFilterReason {
    None,
    LinkedInPlatform,
    SecurityClearance,
    CitizenshipRequirement,
    ExportControl,
    NoVisaSponsorship
}

/// <summary>The filter's answer: accepted, or rejected with exactly one reason.</summary>
public readonly struct JobFilterDecision {
    public bool Accepted { get; }
    public JobFilterReason Reason { get; }

    JobFilterDecision(bool accepted, JobFilterReason reason) { Accepted = accepted; Reason = reason; }

    public static JobFilterDecision Accept() => new(true, JobFilterReason.None);
    public static JobFilterDecision Reject(JobFilterReason reason) => new(false, reason);
}

/// <summary>
/// One switch, defined once and used by every screen: the Job Browser toolbar popup and the
/// Settings "Job Import Filters" section both build their checkboxes from this list, so the two can
/// never drift apart or invent a second setting.
/// </summary>
public sealed class JobFilterSwitch {
    public JobFilterReason Reason { get; init; }

    /// <summary>The checkbox caption, identical in both screens.</summary>
    public string Label { get; init; } = "";

    /// <summary>Reads this switch from the one persisted settings object.</summary>
    public Func<AppSettings, bool> Get { get; init; } = _ => false;

    /// <summary>Writes this switch to the one persisted settings object.</summary>
    public Action<AppSettings, bool> Set { get; init; } = (_, _) => { };
}

public static class JobImportFilter {
    /// <summary>How far from a subject applicant-requirement language still counts, in words.</summary>
    const int ContextWindow = 10;

    // ---------- the five switches, in evaluation order ----------

    /// <summary>
    /// The five filters, in the order <see cref="Evaluate"/> applies them. That order IS the
    /// precedence: a job whose text matches two enabled rules is reported under the first one, so
    /// the same job always produces the same reason.
    /// </summary>
    public static readonly IReadOnlyList<JobFilterSwitch> Switches = new[] {
        new JobFilterSwitch {
            Reason = JobFilterReason.LinkedInPlatform,
            Label = "Skip LinkedIn application platform",
            Get = s => s.SkipLinkedInApply,
            Set = (s, v) => s.SkipLinkedInApply = v
        },
        new JobFilterSwitch {
            Reason = JobFilterReason.SecurityClearance,
            Label = "Skip security clearance requirements",
            Get = s => s.SkipSecurityClearance,
            Set = (s, v) => s.SkipSecurityClearance = v
        },
        new JobFilterSwitch {
            Reason = JobFilterReason.CitizenshipRequirement,
            Label = "Skip U.S. citizenship / Public Trust requirements",
            Get = s => s.SkipCitizenshipRequirement,
            Set = (s, v) => s.SkipCitizenshipRequirement = v
        },
        new JobFilterSwitch {
            Reason = JobFilterReason.ExportControl,
            Label = "Skip export-control / U.S.-person restrictions",
            Get = s => s.SkipExportControl,
            Set = (s, v) => s.SkipExportControl = v
        },
        new JobFilterSwitch {
            Reason = JobFilterReason.NoVisaSponsorship,
            Label = "Skip jobs with no visa sponsorship",
            Get = s => s.SkipNoVisaSponsorship,
            Set = (s, v) => s.SkipNoVisaSponsorship = v
        }
    };

    /// <summary>The footer shown under the switches: this is an import gate, not a cleanup tool.</summary>
    public const string AppliesToFutureImports = "Applies to future imports only.";

    /// <summary>How many switches are on — the number in the toolbar's "Filters (N)".</summary>
    public static int EnabledCount(AppSettings? settings) =>
        settings is null ? 0 : Switches.Count(f => f.Get(settings));

    /// <summary>The toolbar button's caption.</summary>
    public static string ButtonText(AppSettings? settings) => $"Filters ({EnabledCount(settings)})";

    /// <summary>A settings object with every filter off, for callers that deliberately want none.</summary>
    public static AppSettings NoFilters() => new() {
        SkipLinkedInApply = false,
        SkipSecurityClearance = false,
        SkipCitizenshipRequirement = false,
        SkipExportControl = false,
        SkipNoVisaSponsorship = false
    };

    // ---------- user-facing and log text ----------

    /// <summary>The short phrase for a reason, without the "Not imported: " lead-in.</summary>
    public static string ShortText(JobFilterReason reason) => reason switch {
        JobFilterReason.LinkedInPlatform => "LinkedIn application platform",
        JobFilterReason.SecurityClearance => "security clearance required",
        JobFilterReason.CitizenshipRequirement => "U.S. citizenship requirement",
        JobFilterReason.ExportControl => "export-control restriction",
        JobFilterReason.NoVisaSponsorship => "visa sponsorship unavailable",
        _ => "accepted"
    };

    /// <summary>
    /// THE one message the user sees when a manual Import Current Job is refused. It names the rule
    /// and nothing else — never a pattern, a job description or an application address.
    /// </summary>
    public static string Describe(JobFilterReason reason) => "Not imported: " + ShortText(reason);

    /// <summary>The fixed token a diagnostics line carries, e.g. "reason=SecurityClearance".</summary>
    public static string LogReason(JobFilterReason reason) => reason.ToString();

    // ---------- the gate ----------

    /// <summary>
    /// Evaluates one extracted job against the five switches, in <see cref="Switches"/> order. A
    /// disabled switch is not consulted at all, so turning everything off always accepts. Pure and
    /// non-throwing: a malformed description can never fail an import by accident.
    /// </summary>
    public static JobFilterDecision Evaluate(JobImportData? data, AppSettings? settings) {
        if (data is null || settings is null) return JobFilterDecision.Accept();

        try {
            // The application destination decides the LinkedIn rule; the posting text decides the rest.
            if (settings.SkipLinkedInApply && IsLinkedInApplication(data.ApplyUrl))
                return JobFilterDecision.Reject(JobFilterReason.LinkedInPlatform);

            var sentences = Sentences(data.Title + ". " + data.Description);
            if (sentences.Count == 0) return JobFilterDecision.Accept();

            if (settings.SkipSecurityClearance && sentences.Any(RequiresClearance))
                return JobFilterDecision.Reject(JobFilterReason.SecurityClearance);

            if (settings.SkipCitizenshipRequirement && sentences.Any(RequiresCitizenship))
                return JobFilterDecision.Reject(JobFilterReason.CitizenshipRequirement);

            if (settings.SkipExportControl && sentences.Any(RestrictsByExportControl))
                return JobFilterDecision.Reject(JobFilterReason.ExportControl);

            if (settings.SkipNoVisaSponsorship && sentences.Any(RefusesSponsorship))
                return JobFilterDecision.Reject(JobFilterReason.NoVisaSponsorship);

            return JobFilterDecision.Accept();
        } catch {
            // A rule must never cost the user a job. Anything unexpected accepts.
            return JobFilterDecision.Accept();
        }
    }

    // ---------- rule 1: the LinkedIn application platform ----------

    /// <summary>
    /// True only when the real APPLICATION destination is a LinkedIn job posting. It reuses the
    /// existing detector, so "LinkedIn" in the job text, a LinkedIn profile link and a company page
    /// are all irrelevant — and a missing or unusable address is Unknown, which is never rejected.
    /// </summary>
    public static bool IsLinkedInApplication(string? applyUrl) =>
        ApplyCapture.IsApplicationUrl(applyUrl) &&
        ApplicationPlatformDetector.Detect(applyUrl) == ApplicationPlatform.LinkedIn;

    // ---------- rule 2: a security clearance demanded of the applicant ----------

    /// <summary>
    /// Clearance names that are a requirement on their own. A job description does not mention a
    /// Secret clearance or TS/SCI for any reason other than requiring one.
    /// </summary>
    static readonly string[] StrongClearance = {
        "ts sci", "tssci", "top secret sci",
        "secret clearance",            // also covers "top secret clearance" and "active secret clearance"
        "sci clearance",
        "dod clearance", "doe clearance",
        "public trust clearance",
        "interim secret", "interim top secret",
        "security clearance required", "clearance required"
    };

    /// <summary>
    /// Clearance subjects that need applicant-requirement language nearby. "security clearance" on
    /// its own can describe a customer, a product or a partner; "clearance" alone even more so.
    /// </summary>
    static readonly string[] WeakClearance = {
        "security clearance", "security clearances",
        "government clearance", "federal clearance",
        "clearance eligibility", "clearable",
        "public trust",
        "clearance"
    };

    /// <summary>
    /// Applicant-directed requirement words. Deliberately excludes "applicants", "candidates" and
    /// "you", which appear throughout ordinary prose and in equal-opportunity boilerplate.
    /// </summary>
    static readonly string[] RequirementWords = {
        "must", "required", "require", "requires", "requirement", "requirements",
        "eligible", "eligibility",
        "obtain", "obtaining", "maintain", "maintaining", "possess", "possessing",
        "hold", "holds", "holding", "held",
        "active", "current", "currently", "existing",
        "able", "ability", "willing", "need", "needs", "qualify", "qualified"
    };

    /// <summary>
    /// A sentence that says the opposite — "security clearance is not required", "no clearance
    /// needed" — must never be read as a requirement. Accepting a job wrongly costs the user
    /// nothing; rejecting one wrongly loses it silently, so the guard errs toward accepting.
    /// </summary>
    static readonly string[] RequirementDenied = {
        "not required", "not require", "not requiring", "does not require", "do not require",
        "not necessary", "not needed", "no clearance", "no security clearance",
        "without a clearance", "without clearance", "not mandatory"
    };

    static bool RequiresClearance(Sentence sentence) {
        if (sentence.ContainsAny(RequirementDenied)) return false;

        foreach (var subject in StrongClearance)
            if (sentence.Contains(subject)) return true;

        foreach (var subject in WeakClearance)
            if (sentence.HasNear(subject, RequirementWords)) return true;

        return false;
    }

    // ---------- rule 3: a citizenship or Public Trust restriction on the applicant ----------

    /// <summary>Citizenship restrictions that carry their own requirement.</summary>
    static readonly string[] StrongCitizenship = {
        "us citizens only", "citizens only",
        "us citizenship required", "citizenship required",
        "us citizenship is required"
    };

    /// <summary>
    /// The word "citizen" itself is the subject, so "authorized to work in the United States" and
    /// "valid work authorization" — which say nothing about citizenship — are never matched.
    /// </summary>
    static readonly string[] WeakCitizenship = {
        "us citizen", "us citizens", "us citizenship",
        "united states citizen", "united states citizens", "united states citizenship",
        "american citizen", "american citizens", "american citizenship",
        "citizenship",
        "public trust"
    };

    /// <summary>
    /// Equal-opportunity boilerplate lists citizenship as a protected class — the opposite of a
    /// restriction. A sentence that reads as non-discrimination language is never a citizenship rule.
    /// </summary>
    static readonly string[] EqualOpportunityMarkers = {
        "regardless of", "without regard to", "equal opportunity", "equal employment",
        "discriminate", "discrimination", "protected veteran", "protected class",
        "protected characteristic", "affirmative action", "diverse workforce"
    };

    static bool RequiresCitizenship(Sentence sentence) {
        if (sentence.ContainsAny(EqualOpportunityMarkers)) return false;
        if (sentence.ContainsAny(RequirementDenied)) return false;

        foreach (var subject in StrongCitizenship)
            if (sentence.Contains(subject)) return true;

        foreach (var subject in WeakCitizenship)
            if (sentence.HasNear(subject, RequirementWords)) return true;

        return false;
    }

    // ---------- rule 4: export control / U.S.-person eligibility ----------

    static readonly string[] ExportSubjects = {
        "export control", "export controls", "export controlled", "export controlled information",
        "itar", "ear99", "us person", "us persons", "export licence", "export license"
    };

    /// <summary>
    /// Eligibility language, not the generic requirement set: "experience with export control
    /// compliance required" is a skill, while "applicant must be a U.S. person" is a restriction.
    /// </summary>
    static readonly string[] ExportEligibilityWords = {
        "us person", "us persons", "citizen", "citizens", "citizenship",
        "permanent resident", "green card", "protected individual", "asylee", "refugee",
        "eligible", "eligibility", "qualify", "qualifies", "qualifying",
        "restricted", "restriction", "restrictions", "must", "national", "nationals"
    };

    static bool RestrictsByExportControl(Sentence sentence) {
        if (sentence.ContainsAny(EqualOpportunityMarkers)) return false;

        foreach (var subject in ExportSubjects)
            if (sentence.HasNear(subject, ExportEligibilityWords)) return true;

        return false;
    }

    // ---------- rule 5: sponsorship explicitly refused ----------

    static readonly string[] SponsorshipSubjects = {
        "sponsorship", "sponsor", "sponsoring", "sponsored", "visa", "visas"
    };

    /// <summary>
    /// A sponsorship statement only rejects when it is a REFUSAL. "will consider sponsoring" carries
    /// no negation and is accepted; "not eligible for visa transfer or sponsorship" carries one.
    /// Contractions reach here with their apostrophes already folded away ("doesn't" -> "doesnt").
    /// </summary>
    static readonly string[] NegationWords = {
        "not", "no", "never", "without", "unable", "cannot", "cant", "wont",
        "ineligible", "neither", "nor", "unwilling", "doesnt", "dont", "wouldnt", "isnt", "arent"
    };

    static bool RefusesSponsorship(Sentence sentence) =>
        SponsorshipSubjects.Any(subject => sentence.HasNear(subject, NegationWords));

    // ---------- text handling ----------

    /// <summary>
    /// One sentence, reduced to lower-case words. Matching happens on the word list, so punctuation,
    /// hyphens, slashes and casing cannot change a decision: "Top Secret/SCI", "top-secret / SCI"
    /// and "TOP SECRET SCI" are the same three words.
    /// </summary>
    internal sealed class Sentence {
        readonly string[] _words;
        readonly string _joined;

        public Sentence(string[] words) {
            _words = words;
            _joined = " " + string.Join(" ", words) + " ";
        }

        public IReadOnlyList<string> Words => _words;

        /// <summary>Whole-word phrase match — "ear" never matches inside "year" or "research".</summary>
        public bool Contains(string phrase) => _joined.Contains(" " + phrase + " ", StringComparison.Ordinal);

        public bool ContainsAny(IEnumerable<string> phrases) => phrases.Any(Contains);

        /// <summary>
        /// True when <paramref name="subject"/> appears and at least one of <paramref name="context"/>
        /// words sits within <see cref="ContextWindow"/> words of it, on either side. That is what
        /// keeps "must obtain and maintain a security clearance" apart from a product description
        /// that happens to use the same words paragraphs away.
        /// </summary>
        public bool HasNear(string subject, IReadOnlyList<string> context) {
            var subjectWords = subject.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (subjectWords.Length == 0) return false;

            for (var i = 0; i + subjectWords.Length <= _words.Length; i++) {
                var matched = true;
                for (var j = 0; j < subjectWords.Length && matched; j++)
                    matched = _words[i + j] == subjectWords[j];
                if (!matched) continue;

                var from = Math.Max(0, i - ContextWindow);
                var to = Math.Min(_words.Length - 1, i + subjectWords.Length - 1 + ContextWindow);

                for (var k = from; k <= to; k++) {
                    // The subject's own words never count as their own context.
                    if (k >= i && k < i + subjectWords.Length) continue;

                    foreach (var word in context) {
                        if (word.Contains(' ')) { if (Contains(word)) return true; continue; }
                        if (_words[k] == word) return true;
                    }
                }
            }

            return false;
        }

        public override string ToString() => _joined.Trim();
    }

    /// <summary>
    /// Splits job text into sentences AFTER the abbreviations that carry full stops are folded away,
    /// so "must be a U.S. citizen" stays one sentence instead of breaking after "U.".
    /// A colon is kept inside its sentence ("Required: US citizenship" is one statement); a
    /// semicolon, a line break and a bullet all end one.
    /// </summary>
    internal static List<Sentence> Sentences(string? text) {
        var result = new List<Sentence>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var normalized = FoldAbbreviations(text.ToLowerInvariant());

        var word = new StringBuilder();
        var words = new List<string>();

        void EndWord() {
            if (word.Length == 0) return;
            words.Add(word.ToString());
            word.Clear();
        }

        void EndSentence() {
            EndWord();
            if (words.Count > 0) result.Add(new Sentence(words.ToArray()));
            words = new List<string>();
        }

        foreach (var c in normalized) {
            if (char.IsLetterOrDigit(c)) { word.Append(c); continue; }

            switch (c) {
                case '.': case '!': case '?': case ';':
                case '\n': case '\r': case '•': case '▪': case '·': case '|':
                    EndSentence();
                    break;
                default:
                    // Everything else — spaces, hyphens, slashes, colons, quotes — is a word break.
                    EndWord();
                    break;
            }
        }

        EndSentence();
        return result;
    }

    /// <summary>
    /// Folds the abbreviations whose full stops would otherwise split a sentence in the middle of a
    /// requirement. "U.S." is the one that matters — "must be a U.S. citizen" must survive intact.
    /// </summary>
    static string FoldAbbreviations(string lower) {
        var replacements = new (string From, string To)[] {
            ("u.s.a.", "usa"), ("u. s. a.", "usa"), ("u.s.a", "usa"),
            ("u.s.", "us"), ("u. s.", "us"), ("u.s", "us"),
            ("u.k.", "uk"), ("e.g.", "eg"), ("i.e.", "ie"), ("etc.", "etc"),
            ("ph.d.", "phd"), ("b.s.", "bs"), ("m.s.", "ms"),
            ("t.s.", "ts"), ("s.c.i.", "sci"), ("d.o.d.", "dod")
        };

        var text = lower;
        foreach (var (from, to) in replacements)
            text = text.Replace(from, to, StringComparison.Ordinal);

        // Contractions become single words, so "doesn't" is one token and not "doesn" + "t".
        return text.Replace("'", "", StringComparison.Ordinal)
                   .Replace("’", "", StringComparison.Ordinal);
    }
}
