namespace ResumeBuilder;

/// <summary>
/// Synthetic job batches for testing the queue. Everything here is invented sample data — no real
/// company, posting or candidate information — and a batch only reaches the Incoming folder when the
/// user explicitly clicks one of the Development actions.
/// </summary>
public static class SampleJobs {
    public const string SamplePrefix = "SAMPLE-";
    public const string StressPrefix = "STRESS-";

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

    static readonly string[] Locations = {
        "Mountain View, CA", "Seattle, WA", "Austin, TX", "Remote", "Boston, MA",
        "Denver, CO", "Chicago, IL", "New York, NY", "San Diego, CA", "Portland, OR"
    };

    /// <summary>The original three-job batch, kept so the existing Development action is unchanged.</summary>
    public static JobBatch CreateSampleBatch() => new() {
        SchemaVersion = "1.0",
        Source = "sample",
        Jobs = new() {
            new() { JobId = SamplePrefix + "001", Company = "Google", Title = "Senior Software Engineer", Location = "Mountain View, CA", Jd = "Sample JD for a senior software engineer role." },
            new() { JobId = SamplePrefix + "002", Company = "Stripe", Title = "Backend Engineer", Location = "Remote", Jd = "Sample JD for a backend engineer role." },
            new() { JobId = SamplePrefix + "003", Company = "Amazon", Title = "Software Engineer II", Location = "Seattle, WA", Jd = "Sample JD for a software engineer role." }
        }
    };

    /// <summary>
    /// A larger synthetic batch for queue stress testing: unique ids, varied company and role names,
    /// and a complete payload for every job so preparation behaves exactly as it would in real use.
    /// </summary>
    public static JobBatch CreateStressBatch(int count = 50, string? runId = null) {
        runId ??= DateTime.Now.ToString("HHmmss");
        var batch = new JobBatch { SchemaVersion = "1.0", Source = "stress-sample" };

        for (var i = 0; i < count; i++) {
            var company = Companies[i % Companies.Length];
            var role = Roles[(i / Companies.Length + i) % Roles.Length];
            var location = Locations[i % Locations.Length];

            batch.Jobs.Add(new JobInput {
                JobId = $"{StressPrefix}{runId}-{i + 1:D3}",
                Company = company,
                Title = role,
                Location = location,
                Jd = $"Synthetic job description {i + 1} of {count}. {company} is hiring a {role} in {location}. " +
                     "Responsibilities include designing, building and operating backend services, data pipelines " +
                     "and cloud infrastructure, with an emphasis on reliability, testing, observability and CI/CD. " +
                     "This is sample data generated for queue testing and is not a real posting.",
                Link = $"https://example.com/sample/{i + 1}",
                About = $"{company} is a fictional company used only for ResumeBuilder queue testing."
            });
        }
        return batch;
    }

    /// <summary>True for any id created by the Development actions, so test queues can be cleared.</summary>
    public static bool IsTestJobId(string jobId) =>
        jobId.StartsWith(SamplePrefix, StringComparison.OrdinalIgnoreCase) ||
        jobId.StartsWith(StressPrefix, StringComparison.OrdinalIgnoreCase);
}
