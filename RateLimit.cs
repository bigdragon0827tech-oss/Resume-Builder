using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace ResumeBuilder;

// ---------------------------------------------------------------------------
// ChatGPT's temporary "Too many requests" rate limit.
//
// It is not a failed answer: sending again only extends it. So when ChatGPT shows it, the app stops
// sending, waits the configured cooldown (Settings, default 10 minutes), and then re-sends the SAME job through the existing
// send path — the attempt that hit the limit is not counted against the three-attempt budget, so a
// rate limit alone never marks a job Failed. Seen again, it simply starts another cooldown.
//
// Separately, a successful job is followed by the configured job delay (default 30 s) before the queue sends
// the next one, which keeps request volume conservative in the first place.
//
// Detection reads UI chrome only: the probe skips the user's own message, the composer, and the
// assistant's rendered answer (.markdown / code), and returns a fixed token. It clicks nothing,
// copies nothing and sends nothing.
// ---------------------------------------------------------------------------

public static class RateLimit {
    // Both values come from Settings (AppSettings.GptJobDelaySeconds / RateLimitCooldownMinutes) and
    // are read when a delay or cooldown STARTS, so a change applies to the next one.
    public const int DefaultJobDelaySeconds = 30, MinJobDelaySeconds = 0, MaxJobDelaySeconds = 600;
    public const int DefaultCooldownMinutes = 10, MinCooldownMinutes = 1, MaxCooldownMinutes = 120;

    /// <summary>The configured pause between jobs. A value outside the range (a hand-edited file) is clamped.</summary>
    public static TimeSpan JobDelay(AppSettings settings) =>
        TimeSpan.FromSeconds(Math.Clamp(settings.GptJobDelaySeconds, MinJobDelaySeconds, MaxJobDelaySeconds));

    /// <summary>The configured rate-limit cooldown. A value outside the range is clamped.</summary>
    public static TimeSpan Cooldown(AppSettings settings) =>
        TimeSpan.FromMinutes(Math.Clamp(settings.RateLimitCooldownMinutes, MinCooldownMinutes, MaxCooldownMinutes));

    /// <summary>Settings → Save check for the job delay: a whole number 0–600. Null when valid.</summary>
    public static string? ValidateJobDelay(string? text, out int seconds) =>
        ValidateWhole(text, MinJobDelaySeconds, MaxJobDelaySeconds, "GPT Job Delay", "seconds", out seconds);

    /// <summary>Settings → Save check for the cooldown: a whole number 1–120. Null when valid.</summary>
    public static string? ValidateCooldown(string? text, out int minutes) =>
        ValidateWhole(text, MinCooldownMinutes, MaxCooldownMinutes, "Rate Limit Cooldown", "minutes", out minutes);

    static string? ValidateWhole(string? text, int min, int max, string name, string unit, out int value) {
        value = 0;
        if (!int.TryParse((text ?? "").Trim(), System.Globalization.NumberStyles.None,
                          System.Globalization.CultureInfo.InvariantCulture, out value) || value < min || value > max)
            return $"{name} must be a whole number from {min} to {max} {unit}.";
        return null;
    }

    /// <summary>ChatGPT's rate-limit wording, lower case. Matched as substrings, case-insensitively.</summary>
    public static readonly string[] Phrases = {
        "too many requests",
        "making requests too quickly",
        "please wait a few minutes before trying again"
    };

    /// <summary>
    /// The pause after a successful job, before the queue sends the next one. Returns false when it
    /// was cancelled (Stop / Skip / close), in which case the caller does not advance.
    /// </summary>
    public static async Task<bool> WaitBetweenJobsAsync(TimeSpan jobDelay, CancellationToken cancellation,
                                                        Func<TimeSpan, CancellationToken, Task>? delay = null) {
        delay ??= (span, ct) => Task.Delay(span, ct);
        try {
            // 0 seconds = start the next job immediately.
            if (jobDelay > TimeSpan.Zero) await delay(jobDelay, cancellation);
            return !cancellation.IsCancellationRequested;
        } catch (OperationCanceledException) {
            return false;
        }
    }

    /// <summary>
    /// Waits out the gate's cooldown, then calls <paramref name="resend"/> exactly once — the caller
    /// passes a re-send of the SAME job at the SAME attempt. Returns false (and re-sends nothing) when
    /// cancelled.
    /// </summary>
    public static async Task<bool> CooldownThenResumeAsync(RateLimitGate gate, Func<Task> resend, CancellationToken cancellation,
                                                           Func<TimeSpan, CancellationToken, Task>? delay = null) {
        if (!await gate.WaitAsync(cancellation, delay)) return false;
        await resend();
        return true;
    }

    public static bool IsRateLimitText(string? text) =>
        !string.IsNullOrWhiteSpace(text) &&
        Phrases.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));

    public static string DetectedLog(string jobId, TimeSpan cooldown) =>
        $"RATE LIMIT detected — queue paused for {(int)cooldown.TotalMinutes} minutes {jobId}";

    public static string StatusText(DateTime resumeAt, TimeSpan cooldown) =>
        $"RATE LIMIT detected — queue paused for {(int)cooldown.TotalMinutes} minutes (resumes about {resumeAt:HH:mm}).";

    /// <summary>Shown while a cooldown already in progress is being waited out.</summary>
    public static string WaitingText(DateTime resumeAt) =>
        $"RATE LIMIT — queue paused until about {resumeAt:HH:mm}.";

    /// <summary>
    /// Returns only 'rate-limited' or 'ok'. Walks visible text, skipping the user's message, the
    /// composer and the assistant's rendered answer, so a job description or a resume bullet that
    /// happens to mention "Too Many Requests" (HTTP 429) is never mistaken for the popup.
    /// </summary>
    public const string ProbeScript = """
(function () {
  var phrases = ['too many requests', 'making requests too quickly', 'please wait a few minutes before trying again'];
  var skip = '[data-message-author-role="user"], .markdown, pre, code, textarea, [contenteditable="true"], #prompt-textarea';
  if (!document.body) return 'ok';
  var walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
  var node, visited = 0;
  while ((node = walker.nextNode()) && visited < 20000) {
    visited++;
    var value = (node.nodeValue || '').toLowerCase();
    if (value.length < 12) continue;
    for (var i = 0; i < phrases.length; i++) {
      if (value.indexOf(phrases[i]) >= 0) {
        var owner = node.parentElement;
        if (owner && !owner.closest(skip)) return 'rate-limited';
      }
    }
  }
  return 'ok';
})();
""";

    /// <summary>Runs <see cref="ProbeScript"/>; any failure reads as "not rate-limited".</summary>
    public static async Task<bool> IsShownAsync(CoreWebView2? web) {
        if (web is null) return false;
        try {
            var raw = await web.ExecuteScriptAsync(ProbeScript);
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.ValueKind == JsonValueKind.String && doc.RootElement.GetString() == "rate-limited";
        } catch {
            return false;
        }
    }
}

/// <summary>
/// The cooldown itself: pure state with an injectable clock and delay, so "nothing is sent until the
/// cooldown is over" is testable without a browser. MainWindow awaits <see cref="WaitAsync"/> at the
/// very start of every send attempt — before a fresh chat, a fill or a Send — so no path can send
/// during a cooldown.
/// </summary>
public sealed class RateLimitGate {
    readonly Func<DateTime> _now;
    DateTime? _until;

    public RateLimitGate(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.Now);

    public bool IsActive => _until is DateTime until && _now() < until;
    public DateTime? Until => IsActive ? _until : null;

    /// <summary>Starts (or restarts) a full cooldown of <paramref name="duration"/> from now. Returns when it ends.</summary>
    public DateTime Start(TimeSpan duration) {
        _until = _now() + duration;
        return _until.Value;
    }

    public void Clear() => _until = null;

    /// <summary>
    /// Waits until the cooldown is over, in bounded steps so a restarted cooldown is honoured and the
    /// wait stays cancellable. Returns false if cancelled. Sends nothing and polls nothing remote.
    /// </summary>
    public async Task<bool> WaitAsync(CancellationToken cancellation, Func<TimeSpan, CancellationToken, Task>? delay = null) {
        delay ??= (span, ct) => Task.Delay(span, ct);
        try {
            while (IsActive) {
                cancellation.ThrowIfCancellationRequested();
                var remaining = _until!.Value - _now();
                await delay(remaining < TimeSpan.FromSeconds(30) ? remaining : TimeSpan.FromSeconds(30), cancellation);
            }
            return !cancellation.IsCancellationRequested;
        } catch (OperationCanceledException) {
            return false;
        }
    }
}
