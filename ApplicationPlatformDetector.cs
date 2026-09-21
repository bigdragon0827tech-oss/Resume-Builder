using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResumeBuilder;

/// <summary>
/// The application platform (ATS) a job's <see cref="JobTask.ApplyUrl"/> points at. Always derived
/// from that address by <see cref="ApplicationPlatformDetector"/>, never entered by hand.
/// Unknown = no application address yet; Other = an address on a site not recognised below.
/// Stored in tasks.json as the name, so the order here can change without scrambling saved data.
/// </summary>
public enum ApplicationPlatform {
    Unknown,
    Greenhouse,
    Workday,
    Lever,
    LinkedIn,
    Ashby,
    SmartRecruiters,
    ICims,
    Other
}

/// <summary>
/// Maps an application address to its platform. Pure: address in, value out, never throws.
/// Hosts match exactly or as a subdomain, so a look-alike such as notgreenhouse.io is not Greenhouse.
/// </summary>
public static class ApplicationPlatformDetector {
    static readonly (string Domain, ApplicationPlatform Platform)[] Hosts = {
        ("greenhouse.io", ApplicationPlatform.Greenhouse),
        ("myworkdayjobs.com", ApplicationPlatform.Workday),
        ("myworkdaysite.com", ApplicationPlatform.Workday),
        ("myworkday.com", ApplicationPlatform.Workday),
        ("lever.co", ApplicationPlatform.Lever),
        ("ashbyhq.com", ApplicationPlatform.Ashby),
        ("smartrecruiters.com", ApplicationPlatform.SmartRecruiters),
        ("icims.com", ApplicationPlatform.ICims),
    };

    public static ApplicationPlatform Detect(string? applyUrl) {
        if (string.IsNullOrWhiteSpace(applyUrl) || !Uri.TryCreate(applyUrl.Trim(), UriKind.Absolute, out var uri))
            return ApplicationPlatform.Unknown;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return ApplicationPlatform.Unknown;

        var host = uri.Host.ToLowerInvariant();
        foreach (var (domain, platform) in Hosts)
            if (HostIs(host, domain)) return platform;

        // LinkedIn is a platform only for its job postings; a profile or company page is not one.
        if (HostIs(host, "linkedin.com"))
            return uri.AbsolutePath.StartsWith("/jobs/", StringComparison.OrdinalIgnoreCase)
                ? ApplicationPlatform.LinkedIn : ApplicationPlatform.Other;

        // A company careers page that embeds the ATS carries its job id parameter.
        if (HasQueryKey(uri, "gh_jid")) return ApplicationPlatform.Greenhouse;
        if (HasQueryKey(uri, "ashby_jid")) return ApplicationPlatform.Ashby;

        return ApplicationPlatform.Other;
    }

    /// <summary>
    /// Re-derives every task's platform from its ApplyUrl. Returns how many changed, so the caller
    /// saves only when something did. Used at startup to fill in older jobs and apply rule updates.
    /// </summary>
    public static int Refresh(IEnumerable<JobTask> tasks) {
        var changed = 0;
        foreach (var task in tasks) {
            var platform = Detect(task.ApplyUrl);
            if (task.ApplicationPlatform == platform) continue;
            task.ApplicationPlatform = platform;
            changed++;
        }
        return changed;
    }

    static bool HostIs(string host, string domain) =>
        host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);

    static bool HasQueryKey(Uri uri, string key) =>
        uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Any(pair => pair.Split('=', 2)[0].Equals(key, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Reads <see cref="ApplicationPlatform"/> without ever throwing. A missing, null, misspelled or
/// future value becomes Unknown instead of an exception — Storage.LoadTasks turns any exception into
/// an empty task list, which the next save would write over the user's jobs.
/// </summary>
public sealed class TolerantPlatformConverter : JsonConverter<ApplicationPlatform> {
    public override bool HandleNull => true;

    public override ApplicationPlatform Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        switch (reader.TokenType) {
            case JsonTokenType.String:
                var text = reader.GetString();
                return Enum.TryParse<ApplicationPlatform>(text, ignoreCase: true, out var named)
                       && Enum.IsDefined(named) && !int.TryParse(text, out _)
                    ? named : ApplicationPlatform.Unknown;
            case JsonTokenType.Number:
                return reader.TryGetInt32(out var number) && Enum.IsDefined((ApplicationPlatform)number)
                    ? (ApplicationPlatform)number : ApplicationPlatform.Unknown;
            case JsonTokenType.StartObject:
            case JsonTokenType.StartArray:
                reader.Skip();
                return ApplicationPlatform.Unknown;
            default:
                return ApplicationPlatform.Unknown;
        }
    }

    public override void Write(Utf8JsonWriter writer, ApplicationPlatform value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
