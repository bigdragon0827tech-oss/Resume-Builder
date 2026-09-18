using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace ResumeBuilder;

public static class Storage {
    static readonly JsonSerializerOptions Opt = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ResumeBuilder");
    public static string SettingsPath => Path.Combine(DataDir, "settings.json");
    public static string TasksPath => Path.Combine(DataDir, "tasks.json");
    /// <summary>The WebView2 profile: shared by every recycled browser so the ChatGPT login survives.</summary>
    public static string WebViewUserDataFolder => Path.Combine(DataDir, "WebView2");

    public static AppSettings LoadSettings() {
        try { return File.Exists(SettingsPath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Opt) ?? new() : new(); }
        catch { return new(); }
    }
    public static void SaveSettings(AppSettings s) {
        Directory.CreateDirectory(DataDir); File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s, Opt));
    }
    public static List<JobTask> LoadTasks() {
        try { return File.Exists(TasksPath) ? JsonSerializer.Deserialize<List<JobTask>>(File.ReadAllText(TasksPath), Opt) ?? new() : new(); }
        catch { return new(); }
    }
    public static void SaveTasks(IEnumerable<JobTask> tasks) {
        Directory.CreateDirectory(DataDir); File.WriteAllText(TasksPath, JsonSerializer.Serialize(tasks, Opt));
    }
}

public sealed class ImportResult {
    public int FilesImported { get; set; }
    public int JobsQueued { get; set; }
    public int JobsExisting { get; set; }
    public List<string> Errors { get; } = new();
}

/// <summary>
/// The one place a job URL is made comparable. Duplicate detection never compares raw strings.
/// </summary>
public static class JobUrls {
    /// <summary>
    /// Query parameters that only record how the visitor arrived. Verified on Jobright, whose job
    /// addresses are /jobs/info/&lt;id&gt; and whose only query parameter seen is utm_source. Anything
    /// not on this list is kept, because on another site a parameter can be what names the job.
    /// </summary>
    static readonly string[] TrackingParameters = {
        "gclid", "gbraid", "wbraid", "fbclid", "msclkid", "yclid", "igshid",
        "mc_cid", "mc_eid", "_hsenc", "_hsmi", "li_fat_id"
    };

    static bool IsTracking(string key) =>
        key.StartsWith("utm_", StringComparison.OrdinalIgnoreCase)
        || TrackingParameters.Contains(key, StringComparer.OrdinalIgnoreCase);

    /// <summary>True for an absolute http or https address.</summary>
    public static bool IsWebUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// A job URL in one comparable form, or null when it is not a web address: trimmed, scheme and
    /// host lower-cased, default port dropped, fragment dropped, trailing slash dropped (except for
    /// the root), tracking parameters dropped and the rest sorted so their order does not matter.
    /// The path keeps its case — on the web it can be significant.
    /// </summary>
    public static string? Normalize(string? url) {
        if (!IsWebUrl(url)) return null;
        var uri = new Uri(url!.Trim());

        var path = uri.AbsolutePath;
        if (path.Length > 1) path = path.TrimEnd('/');
        if (path.Length == 0) path = "/";

        var query = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(pair => !IsTracking(pair.Split('=', 2)[0]))
            .OrderBy(pair => pair, StringComparer.Ordinal)
            .ToList();

        var authority = uri.IsDefaultPort ? uri.Host : uri.Host + ":" + uri.Port;
        return uri.Scheme.ToLowerInvariant() + "://" + authority.ToLowerInvariant() + path
               + (query.Count == 0 ? "" : "?" + string.Join("&", query));
    }
}

/// <summary>
/// Turns ONE job into ONE task. Incoming JSON files and the built-in job browser both come through
/// <see cref="ImportOne"/>, so validation and duplicate handling cannot drift apart. There is no
/// batch path: a file holding more than one job is refused, never partly imported.
/// </summary>
public static class JobImporter {
    public const string IncomingSource = "incoming-json";
    public const string BrowserSource = "jobright-browser";
    public const string OneJobOnly = "Only one job per input file is supported.";

    static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };
    static readonly JsonDocumentOptions ParseOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public static ImportResult Import(AppSettings settings, List<JobTask> tasks) {
        var result = new ImportResult();
        if (string.IsNullOrWhiteSpace(settings.IncomingFolder) || !Directory.Exists(settings.IncomingFolder)) {
            result.Errors.Add("Incoming folder is not configured or does not exist."); return result;
        }
        if (string.IsNullOrWhiteSpace(settings.ImportedFolder)) {
            result.Errors.Add("Imported folder is not configured."); return result;
        }
        Directory.CreateDirectory(settings.ImportedFolder);

        foreach (var file in Directory.GetFiles(settings.IncomingFolder, "*.json")) {
            try {
                var outcome = ImportOne(ReadSingleJob(File.ReadAllText(file)), IncomingSource, tasks);
                switch (outcome.Kind) {
                    case JobImportKind.Imported: result.JobsQueued++; break;
                    case JobImportKind.Duplicate: result.JobsExisting++; break;
                    // A refused file stays in Incoming, so it can be fixed and picked up again.
                    default: throw new InvalidDataException(outcome.Reason);
                }

                var dest = UniqueDestination(settings.ImportedFolder, Path.GetFileName(file));
                File.Move(file, dest);
                result.FilesImported++;
            } catch (Exception ex) {
                result.Errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        return result;
    }

    /// <summary>
    /// Reads one Incoming file as exactly one job. An array, or an object carrying a "jobs" list (the
    /// retired batch format), is refused outright rather than having its first job quietly imported.
    /// </summary>
    public static JobImportData ReadSingleJob(string json) {
        JsonNode? node;
        try { node = JsonNode.Parse(json, null, ParseOptions); }
        catch (JsonException) { throw new InvalidDataException("The file is not valid JSON."); }

        switch (node) {
            case JsonArray:
                throw new InvalidDataException(OneJobOnly);
            case JsonObject o when o.Any(p => p.Key.Equals("jobs", StringComparison.OrdinalIgnoreCase)):
                throw new InvalidDataException(OneJobOnly +
                    " This file uses the old batch format; write one { company, title, jobUrl, companyUrl, description } object per file.");
            case JsonObject o:
                return o.Deserialize<JobImportData>(ReadOptions) ?? throw new InvalidDataException("The file does not contain a job.");
            default:
                throw new InvalidDataException("The file must contain one job object.");
        }
    }

    /// <summary>
    /// Validate, normalize the job URL, refuse a duplicate, otherwise create one task with a fresh
    /// internal id and save. The task starts Queued and, by JobTask's own default, application-Viewed.
    /// Nothing is written for an invalid job or a duplicate.
    /// </summary>
    public static JobImportOutcome ImportOne(JobImportData data, string source, List<JobTask> tasks) {
        var company = (data.Company ?? "").Trim();
        var title = (data.Title ?? "").Trim();
        var description = (data.Description ?? "").Trim();

        string? Refuse() =>
            company.Length == 0 ? "Company is required."
            : title.Length == 0 ? "Job title is required."
            : string.IsNullOrWhiteSpace(data.JobUrl) ? "Job URL is required."
            : !JobUrls.IsWebUrl(data.JobUrl) ? "Job URL is not a valid web address."
            : description.Length == 0 ? "Job description is required."
            : null;

        if (Refuse() is string reason)
            return new JobImportOutcome { Kind = JobImportKind.Invalid, Title = title, Company = company, Reason = reason };

        var jobUrl = JobUrls.Normalize(data.JobUrl)!;

        // One job URL, one task — however the address was written.
        var existing = tasks.FirstOrDefault(t => JobUrls.Normalize(t.Link) == jobUrl);
        if (existing is not null)
            return new JobImportOutcome {
                Kind = JobImportKind.Duplicate, JobId = existing.JobId, Title = existing.Title,
                Company = existing.Company, ApplicationStatus = existing.ApplicationStatus
            };

        var task = new JobTask {
            JobId = NewInternalId(tasks),
            Source = source,
            Company = company,
            Title = title,
            Location = "",                   // not part of the input; never invented
            Jd = description,
            Link = jobUrl,
            // Optional: kept only when it is a real web address, so a bad value cannot fail the job.
            CompanyUrl = JobUrls.IsWebUrl(data.CompanyUrl) ? data.CompanyUrl!.Trim() : "",
            About = "",
            Status = "Queued"
        };

        tasks.Add(task);
        Storage.SaveTasks(tasks);

        return new JobImportOutcome {
            Kind = JobImportKind.Imported, JobId = task.JobId, Title = task.Title,
            Company = task.Company, ApplicationStatus = task.ApplicationStatus
        };
    }

    /// <summary>
    /// A new internal task id: "RB-yyyyMMdd-HHmmss-xxxxxxxx". The time makes it readable in a folder
    /// listing, the random part makes it collision-safe, and it is checked against the queue anyway.
    /// </summary>
    public static string NewInternalId(IEnumerable<JobTask> tasks) {
        var taken = new HashSet<string>(tasks.Select(t => t.JobId), StringComparer.OrdinalIgnoreCase);
        string id;
        do { id = $"RB-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..27]; }
        while (taken.Contains(id));
        return id;
    }

    static string UniqueDestination(string folder,string name) {
        var p=Path.Combine(folder,name); if(!File.Exists(p)) return p;
        var stem=Path.GetFileNameWithoutExtension(name); var ext=Path.GetExtension(name); int n=2;
        do { p=Path.Combine(folder,$"{stem}_{n++}{ext}"); } while(File.Exists(p));
        return p;
    }
}

public static class RequestPreparation {
 public static string PreparedPath => Path.Combine(Storage.DataDir,"prepared-request.json");
 /// <summary>Plain-text copy of the prepared input, so the request survives any clipboard failure.</summary>
 public static string PreparedTextPath => Path.Combine(Storage.DataDir,"prepared-request.txt");

 /// <summary>Persists the prepared request as JSON plus a plain-text sidecar. Used by every prepare path.</summary>
 public static void Save(PreparedRequest prepared) {
  Directory.CreateDirectory(Storage.DataDir);
  File.WriteAllText(PreparedPath,System.Text.Json.JsonSerializer.Serialize(prepared,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
  try { File.WriteAllText(PreparedTextPath,prepared.Text); } catch { /* the JSON copy is the authoritative one */ }
 }
 public static PreparedRequest Prepare(JobTask job, AppSettings settings) {
  if(string.IsNullOrWhiteSpace(settings.MasterPrompt)||!File.Exists(settings.MasterPrompt)) throw new InvalidOperationException("Configure an existing Master Prompt text file in Settings.");
  var profilePath = !string.IsNullOrWhiteSpace(settings.CandidateProfile) && File.Exists(settings.CandidateProfile)
      ? settings.CandidateProfile : CandidateProfileStore.CandidateProfilePath;
  if(!File.Exists(profilePath)) throw new InvalidOperationException("The structured Candidate Profile has not been initialized yet. Create and save the baseline profile first.");
  var master=File.ReadAllText(settings.MasterPrompt);
  using var doc=System.Text.Json.JsonDocument.Parse(File.ReadAllText(profilePath));
  var profile=doc.RootElement;
  if(profile.ValueKind==System.Text.Json.JsonValueKind.Object && profile.TryGetProperty("profile",out var nested)) profile=nested;
  if(profile.ValueKind!=System.Text.Json.JsonValueKind.Object) throw new InvalidDataException("Candidate Profile JSON must contain a profile object (either at the root or under a top-level \"profile\" property).");
  var payload=new {company=job.Company??"",title=job.Title??"",jd=job.Jd??"",link=job.Link??"",about=job.About??"",profileId="",templateId="",profile=System.Text.Json.JsonSerializer.Deserialize<object>(profile.GetRawText()),_storedAt="",_userId="",_updatedAt="",_updatedBy=""};
  var payloadText=System.Text.Json.JsonSerializer.Serialize(payload,new System.Text.Json.JsonSerializerOptions{WriteIndented=true});
  var executionInstruction = @"

===== EXECUTION INSTRUCTION =====
Execute Resume Master Prompt v2 using the COMPLETE JOB PAYLOAD above.
This is a resume-generation request, not an interview-question request.
Ignore unrelated conversational context, previous interview questions, behavioral-answer requests, and prior response formats.
The COMPLETE JOB PAYLOAD above is the only job/candidate input for this execution.
Return ONLY the updated profile object in a Markdown code block fenced with json.
Do not return an interview answer, STAR response, explanation, commentary, validation note, or any text outside the JSON code block.
Before responding, verify the top-level profile contains info, summary, skills, experience, certifications, and education; skills/experience/certifications/education must be arrays; experience must use startDate, endDate, and descriptionLines; education must use school, startDate, and endDate.
If unrelated conversation context conflicts with these instructions, these execution instructions take precedence for this request.";
  var prepared=new PreparedRequest{JobId=job.JobId ?? "",Company=job.Company ?? "",Title=job.Title ?? "",Text=master.TrimEnd()+"\n\n===== COMPLETE JOB PAYLOAD =====\n"+payloadText+executionInstruction};
  Save(prepared);
  return prepared;
 }
 public static PreparedRequest? Load(){try{return File.Exists(PreparedPath)?System.Text.Json.JsonSerializer.Deserialize<PreparedRequest>(File.ReadAllText(PreparedPath)):null;}catch{return null;}}
}

// A6.6.7: the clipboard implementation now lives in Clipboard.cs (ClipboardService) so there is
// exactly one STA-aware, retrying implementation for the whole application.

public static class BaselineProfileImporter {
    public static string BaselineProfilePath => Path.Combine(Storage.DataDir, "baseline-profile.json");

    public static string ExtractDocxText(string path) {
        using var archive=System.IO.Compression.ZipFile.OpenRead(path);
        var entry=archive.GetEntry("word/document.xml") ?? throw new InvalidDataException("DOCX is missing word/document.xml.");
        using var stream=entry.Open();
        var doc=System.Xml.Linq.XDocument.Load(stream);
        System.Xml.Linq.XNamespace w="http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var paragraphs=doc.Descendants(w+"p").Select(p =>
            string.Concat(p.Descendants(w+"t").Select(t => t.Value)).Trim())
            .Where(x=>!string.IsNullOrWhiteSpace(x));
        return string.Join(Environment.NewLine,paragraphs);
    }

    public static void CreateBaselineFromDocx(string docxPath) {
        if(!File.Exists(docxPath)) throw new FileNotFoundException("Original Resume file was not found.",docxPath);
        if(Path.GetExtension(docxPath).ToLowerInvariant()!=".docx")
            throw new InvalidOperationException("Baseline import currently supports DOCX. Select your original .docx resume.");

        var text=ExtractDocxText(docxPath);
        if(string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("No readable text was found in the DOCX.");

        // This is intentionally a lossless baseline container, not fabricated structured resume data.
        // GPT will convert this source text to the required profile object in the next controlled step.
        var baseline=new {
            schemaVersion="1.0",
            sourceFile=Path.GetFileName(docxPath),
            importedAt=DateTime.Now,
            sourceText=text
        };
        Directory.CreateDirectory(Storage.DataDir);
        File.WriteAllText(BaselineProfilePath,
            System.Text.Json.JsonSerializer.Serialize(baseline,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
    }

    public static string BuildProfileCreationRequest(string masterPromptPath) {
        if(!File.Exists(BaselineProfilePath)) throw new InvalidOperationException("Import the Original Resume first.");
        var baseline=File.ReadAllText(BaselineProfilePath);
        return
@"Create the initial candidate profile JSON from the original resume source below.
This is a baseline-import operation, not job tailoring.
Preserve factual information from the source. Do not invent employers, dates, schools, contact details, certifications, technologies, or metrics.
Use empty strings or arrays where the source does not support a value.

STRICT OUTPUT CONTRACT
Return the profile object itself. Do NOT wrap it in { ""profile"": ... }.
Return valid JSON only; do not add prose or comments.
Use EXACTLY this schema and field spelling:

{
  ""info"": {
    ""name"": """",
    ""title"": """",
    ""location"": """",
    ""email"": """",
    ""phone"": """",
    ""linkedin"": """"
  },
  ""summary"": """",
  ""skills"": [
    {
      ""category"": """",
      ""skills"": []
    }
  ],
  ""experience"": [
    {
      ""title"": """",
      ""company"": """",
      ""startDate"": """",
      ""endDate"": """",
      ""location"": """",
      ""descriptionLines"": []
    }
  ],
  ""certifications"": [
    {
      ""name"": """",
      ""issuer"": """",
      ""date"": """"
    }
  ],
  ""education"": [
    {
      ""degree"": """",
      ""major"": """",
      ""school"": """",
      ""startDate"": """",
      ""endDate"": """"
    }
  ]
}

SCHEMA IS IMMUTABLE
The JSON template above is not an example. It is the exact serialization contract consumed by the application.
Do not rename, combine, omit, or invent properties. Do not convert arrays into objects.
For every source field that does not map directly, map its value into the canonical field or omit it; never preserve the source field name.

MANDATORY VALIDATION RULES
- The first six top-level properties must be info, summary, skills, experience, certifications, education.
- skills MUST begin with [ and be an array, never an object/dictionary keyed by category.
- Every skills item contains only category and skills; skills is an array of strings.
- Experience uses startDate and endDate, NEVER start_date or end_date.
- Experience uses descriptionLines, NEVER bullets.
- Do NOT output dates, start_date, end_date, bullets, employment_type, work_arrangement, work_mode, or institution.
- Education uses school, NEVER institution.
- Education uses startDate and endDate, NEVER start_date or end_date.
- Every education item includes major; use an empty string if the source does not establish it.
- Email and LinkedIn values must be plain text, NEVER Markdown links or mailto syntax.
- Do not add fields outside the schema.
- Before answering, internally verify info is an object, summary is a string, and skills, experience, certifications, and education are arrays.
- Before answering, search your draft for these forbidden property names and remove/convert every occurrence: dates, start_date, end_date, bullets, employment_type, work_arrangement, work_mode, institution.
- If your draft contains ""skills"": { then it is INVALID. Rewrite it as ""skills"": [ { ""category"": ""..."", ""skills"": [...] } ].
- Return the canonical JSON even if the source resume itself uses a different structure.

===== ORIGINAL RESUME SOURCE =====
"+baseline;
    }
}

public static class CandidateProfileStore {
    public static string CandidateProfilePath => Path.Combine(Storage.DataDir,"candidate-profile.json");

    public static string ExtractJson(string text) {
        if(string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("ChatGPT result is empty.");
        var t=text.Trim();
        var fenceStart=t.IndexOf("```",StringComparison.Ordinal);
        if(fenceStart>=0) {
            var firstNewline=t.IndexOf('\n',fenceStart);
            var fenceEnd=t.IndexOf("```",firstNewline>=0?firstNewline+1:fenceStart+3,StringComparison.Ordinal);
            if(firstNewline>=0 && fenceEnd>firstNewline) t=t.Substring(firstNewline+1,fenceEnd-firstNewline-1).Trim();
        }
        var a=t.IndexOf('{'); var b=t.LastIndexOf('}');
        if(a<0||b<=a) throw new InvalidDataException("No JSON object was found in the ChatGPT result.");
        return t.Substring(a,b-a+1);
    }

    /// <summary>
    /// Normalizes the AI response into the canonical schema, validates the normalized result, then saves it.
    /// Known AI output variations (skills as an object, bullets, dates, institution, start_date/end_date,
    /// Markdown contact links, extra non-schema fields) are converted instead of rejected.
    /// </summary>
    public static NormalizationReport NormalizeAndSave(string resultText) => NormalizeAndSaveTo(resultText,CandidateProfilePath);

    /// <summary>
    /// Same pipeline, writing to a caller-chosen file. A6.6.8 uses this so a tailored per-job result
    /// lands in results\&lt;jobId&gt;.json while candidate-profile.json stays the baseline that every
    /// future job is tailored from.
    /// </summary>
    public static NormalizationReport NormalizeAndSaveTo(string resultText,string targetPath) {
        var json=ExtractJson(resultText);
        System.Text.Json.Nodes.JsonNode root;
        try { root=ProfileNormalizer.Parse(json); }
        catch(System.Text.Json.JsonException ex) { throw new InvalidDataException("The result is not valid JSON: "+ex.Message); }

        var report=ProfileNormalizer.Normalize(root);
        var canonical=ProfileNormalizer.ToCanonicalJson(report.Profile);
        using(var doc=System.Text.Json.JsonDocument.Parse(canonical)) Validate(doc.RootElement);

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? Storage.DataDir);
        File.WriteAllText(targetPath,canonical);
        return report;
    }

    /// <summary>Kept for callers that only need pass/fail behaviour.</summary>
    public static void ValidateAndSave(string resultText) => NormalizeAndSave(resultText);

    /// <summary>Strict canonical-schema check, run against the normalized profile.</summary>
    public static void Validate(System.Text.Json.JsonElement profile) {
        if(profile.ValueKind!=System.Text.Json.JsonValueKind.Object)
            throw new InvalidDataException("Result must be a JSON object.");

        var errors=new List<string>();
        string[] required={"info","summary","skills","experience","certifications","education"};
        foreach(var name in required)
            if(!profile.TryGetProperty(name,out _)) errors.Add($"Missing top-level field: {name}");

        if(profile.TryGetProperty("info",out var info) && info.ValueKind!=System.Text.Json.JsonValueKind.Object) errors.Add("info must be an object");
        if(profile.TryGetProperty("summary",out var summary) && summary.ValueKind!=System.Text.Json.JsonValueKind.String) errors.Add("summary must be a string");
        foreach(var name in new[]{"skills","experience","certifications","education"})
            if(profile.TryGetProperty(name,out var value) && value.ValueKind!=System.Text.Json.JsonValueKind.Array) errors.Add($"{name} must be an array");

        if(profile.TryGetProperty("skills",out var skillsRoot) && skillsRoot.ValueKind==System.Text.Json.JsonValueKind.Array) {
            int i=0; foreach(var item in skillsRoot.EnumerateArray()) {
                if(item.ValueKind!=System.Text.Json.JsonValueKind.Object) errors.Add($"skills[{i}] must be an object");
                else {
                    if(!item.TryGetProperty("category",out var cat) || cat.ValueKind!=System.Text.Json.JsonValueKind.String) errors.Add($"skills[{i}].category is missing or not a string");
                    if(!item.TryGetProperty("skills",out var vals) || vals.ValueKind!=System.Text.Json.JsonValueKind.Array) errors.Add($"skills[{i}].skills is missing or not an array");
                } i++;
            }
        }

        if(profile.TryGetProperty("experience",out var expRoot) && expRoot.ValueKind==System.Text.Json.JsonValueKind.Array) {
            int i=0; foreach(var item in expRoot.EnumerateArray()) {
                if(item.ValueKind!=System.Text.Json.JsonValueKind.Object) { errors.Add($"experience[{i}] must be an object"); i++; continue; }
                foreach(var f in new[]{"title","company","startDate","endDate","location","descriptionLines"}) if(!item.TryGetProperty(f,out _)) errors.Add($"experience[{i}].{f} is missing");
                foreach(var f in new[]{"dates","start_date","end_date","bullets","employment_type","work_arrangement","work_mode"}) if(item.TryGetProperty(f,out _)) errors.Add($"experience[{i}].{f} is not allowed");
                if(item.TryGetProperty("descriptionLines",out var dl)) {
                    if(dl.ValueKind!=System.Text.Json.JsonValueKind.Array) errors.Add($"experience[{i}].descriptionLines must be an array");
                    else ValidateDescriptionLines(dl,i,errors);
                }
                i++;
            }
        }

        if(profile.TryGetProperty("education",out var eduRoot) && eduRoot.ValueKind==System.Text.Json.JsonValueKind.Array) {
            int i=0; foreach(var item in eduRoot.EnumerateArray()) {
                if(item.ValueKind!=System.Text.Json.JsonValueKind.Object) { errors.Add($"education[{i}] must be an object"); i++; continue; }
                foreach(var f in new[]{"degree","major","school","startDate","endDate"}) if(!item.TryGetProperty(f,out _)) errors.Add($"education[{i}].{f} is missing");
                foreach(var f in new[]{"institution","dates","start_date","end_date"}) if(item.TryGetProperty(f,out _)) errors.Add($"education[{i}].{f} is not allowed");
                i++;
            }
        }

        // Style system: the optional style block, checked against the same limits the normalizer clamps to.
        if(profile.TryGetProperty("style",out var style)) {
            if(style.ValueKind!=System.Text.Json.JsonValueKind.Object) errors.Add("style must be an object");
            else errors.AddRange(StyleValidator.Validate(System.Text.Json.Nodes.JsonNode.Parse(style.GetRawText())));
        }

        if(errors.Count>0) throw new InvalidDataException("Profile schema errors:\n\n• "+string.Join("\n• ",errors.Take(30))+(errors.Count>30?$"\n• ...and {errors.Count-30} more":""));
    }

    /// <summary>
    /// Style system: a description line is either plain text or a segmented line carrying inline emphasis.
    /// Markdown is never accepted in resume text — emphasis is structural, so it cannot be mistaken
    /// for content.
    /// </summary>
    static void ValidateDescriptionLines(System.Text.Json.JsonElement lines,int experienceIndex,List<string> errors) {
        int i=0;
        foreach(var line in lines.EnumerateArray()) {
            var path=$"experience[{experienceIndex}].descriptionLines[{i}]";
            if(line.ValueKind==System.Text.Json.JsonValueKind.String) { i++; continue; }
            if(line.ValueKind!=System.Text.Json.JsonValueKind.Object) {
                errors.Add($"{path} must be a string or a {{ \"segments\": [...] }} object");
                i++; continue;
            }
            if(!line.TryGetProperty("segments",out var segments) || segments.ValueKind!=System.Text.Json.JsonValueKind.Array) {
                errors.Add($"{path}.segments is missing or not an array");
                i++; continue;
            }
            int s=0;
            foreach(var segment in segments.EnumerateArray()) {
                var segmentPath=$"{path}.segments[{s}]";
                if(segment.ValueKind!=System.Text.Json.JsonValueKind.Object) errors.Add($"{segmentPath} must be an object");
                else {
                    if(!segment.TryGetProperty("text",out var text) || text.ValueKind!=System.Text.Json.JsonValueKind.String)
                        errors.Add($"{segmentPath}.text is missing or not a string");
                    if(segment.TryGetProperty("bold",out var bold)
                       && bold.ValueKind!=System.Text.Json.JsonValueKind.True && bold.ValueKind!=System.Text.Json.JsonValueKind.False)
                        errors.Add($"{segmentPath}.bold must be true or false");
                }
                s++;
            }
            i++;
        }
    }

    /// <summary>
    /// The documented contract check: content and style together, reported as a list instead of an
    /// exception. Used by the tests and by anyone checking an AI answer before it is rendered.
    /// Unlike the capture pipeline, nothing here is repaired — an out-of-range style is an error.
    /// </summary>
    public static List<string> ValidateResumeJson(string json) {
        var errors=new List<string>();
        System.Text.Json.JsonDocument doc;
        try { doc=System.Text.Json.JsonDocument.Parse(json,new System.Text.Json.JsonDocumentOptions{AllowTrailingCommas=true,CommentHandling=System.Text.Json.JsonCommentHandling.Skip}); }
        catch(System.Text.Json.JsonException ex) { errors.Add("The resume JSON could not be parsed: "+ex.Message); return errors; }

        using(doc) {
            try { Validate(doc.RootElement); }
            catch(InvalidDataException ex) {
                foreach(var line in ex.Message.Split('\n')) {
                    var trimmed=line.TrimStart().TrimStart('•').Trim();
                    if(trimmed.Length>0 && !trimmed.StartsWith("Profile schema errors",StringComparison.Ordinal)) errors.Add(trimmed);
                }
            }
        }
        return errors;
    }
}
