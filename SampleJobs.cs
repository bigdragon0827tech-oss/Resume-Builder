namespace ResumeBuilder;

/// <summary>
/// Synthetic jobs for testing the queue. Everything here is invented sample data — no real company,
/// posting or candidate information — and a job only reaches the Incoming folder when the user
/// explicitly clicks one of the Development actions.
///
/// Each job is written as its own file in the one-job input format, because the importer takes
/// exactly one job per file. Every job URL is under <see cref="SampleUrlRoot"/>, which is how the test
/// queue is recognised and cleared, and each run gets its own URLs so a second run is not a duplicate.
/// </summary>
public static class SampleJobs {
    /// <summary>Ids given to test jobs before the one-job contract; still recognised when clearing.</summary>
    public const string SamplePrefix = "SAMPLE-";
    public const string StressPrefix = "STRESS-";

    /// <summary>Every synthetic job URL starts here. example.com is reserved for exactly this use.</summary>
    public const string SampleUrlRoot = "https://example.com/sample/";

    static readonly string[] Companies = {
        "Northwind Systems", "Contoso Cloud", "Fabrikam Robotics", "Litware Analytics", "Adventure Labs",
        "Tailspin Networks", "Proseware Health", "Wingtip Energy", "Coho Financial", "Lucerne Mobility",
        "Alpine Data Works", "Blue Harbor Media", "Cedar Peak Software", "Delta Grove Logistics", "Everline Payments",
        "Fairmont Devices", "Granite Bay Security", "Harbour Point AI", "Ironwood Retail", "Juniper Transit",
        "Kestrel Biotech", "Lakeshore Gaming", "Meridian Telecom", "Northgate Insurance", "Orchard Street Labs"
    };

    static readonly string[] Roles = {
        "Senior Software Engineer", "Software Engineer", "Lead Software Engineer",
        "Senior Data Engineer", "Data Engineer", "Machine Learning Engineer",
        "Senior Machine Learning Engineer", "Platform Engineer", "Senior Platform Engineer",
        "Cloud Infrastructure Engineer"
    };

    /// <summary>The original three sample jobs.</summary>
    public static List<JobImportData> CreateSampleJobs(string? runId = null) {
        runId ??= DateTime.Now.ToString("yyyyMMdd-HHmmss");
        JobImportData Job(int n, string company, string title, string description) => new() {
            Company = company, Title = title, Description = description,
            JobUrl = $"{SampleUrlRoot}{runId}/{n}", CompanyUrl = null
        };
        return new() {
            Job(1, "Google", "Senior Software Engineer", "Sample JD for a senior software engineer role."),
            Job(2, "Stripe", "Backend Engineer", "Sample JD for a backend engineer role."),
            Job(3, "Amazon", "Software Engineer II", "Sample JD for a software engineer role.")
        };
    }

    /// <summary>
    /// A larger synthetic set for queue stress testing: varied company and role names and a complete
    /// description for every job, so preparation behaves exactly as it would in real use.
    /// </summary>
    public static List<JobImportData> CreateStressJobs(int count = 50, string? runId = null) {
        runId ??= DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var jobs = new List<JobImportData>();

        for (var i = 0; i < count; i++) {
            var company = Companies[i % Companies.Length];
            var role = Roles[(i / Companies.Length + i) % Roles.Length];

            jobs.Add(new JobImportData {
                Company = company,
                Title = role,
                JobUrl = $"{SampleUrlRoot}{runId}/{i + 1}",
                CompanyUrl = "https://example.com/company/" + company.Replace(' ', '-').ToLowerInvariant(),
                Description = $"Synthetic job description {i + 1} of {count}. {company} is hiring a {role}. " +
                              "Responsibilities include designing, building and operating backend services, data pipelines " +
                              "and cloud infrastructure, with an emphasis on reliability, testing, observability and CI/CD. " +
                              "This is sample data generated for queue testing and is not a real posting."
            });
        }
        return jobs;
    }

    /// <summary>
    /// True for any job created by the Development actions, so the test queue can be cleared: by its
    /// sample URL (the one-job contract) or by the id prefix older test jobs were saved with.
    /// </summary>
    public static bool IsTestJob(JobTask task) =>
        (task.Link ?? "").StartsWith(SampleUrlRoot, StringComparison.OrdinalIgnoreCase)
        || task.JobId.StartsWith(SamplePrefix, StringComparison.OrdinalIgnoreCase)
        || task.JobId.StartsWith(StressPrefix, StringComparison.OrdinalIgnoreCase);
}
