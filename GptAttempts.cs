namespace ResumeBuilder;

/// <summary>
/// Why one ChatGPT attempt ended. Only the retryable ones reach <see cref="GptAttempts"/>; local
/// failures (prepare, storage, documents) and a capture timeout are handled where they happen and
/// are never re-sent.
/// </summary>
public enum GptFailure {
    /// <summary>No WebView, fresh-chat navigation failed, the composer could not be filled, or Send failed.</summary>
    SendSide,
    /// <summary>Nothing was generating within the response-start budget after a confirmed Send.</summary>
    ResponseStartTimeout,
    /// <summary>Generation started, then no progress was seen for the inactivity budget.</summary>
    ResponseStalled,
    /// <summary>One answer took longer than the absolute ceiling.</summary>
    ResponseCeiling,
    /// <summary>The captured answer could not be extracted, normalized or strictly validated.</summary>
    InvalidOutput
}

public enum AttemptDecision { Retry, Fail }

/// <summary>
/// The whole retry policy for ChatGPT, in one place: how many sends a job may have, whether a given
/// attempt retries or gives up, the `FailureReason` recorded on the job, and the exact log lines.
///
/// No I/O, no UI, no WebView2 — MainWindow performs the effects, as it does for QueueRunner and
/// CaptureWatchdog. Logs carry the job id, the attempt and a fixed reason only: never prompt text,
/// answer text or a URL.
/// </summary>
public static class GptAttempts {
    /// <summary>Total ChatGPT sends allowed for one job, across every retryable failure.</summary>
    public const int MaxAttempts = 3;

    /// <summary>The first attempt is 1. A retry is allowed while attempts remain.</summary>
    public static AttemptDecision Decide(int attempt) =>
        attempt < MaxAttempts ? AttemptDecision.Retry : AttemptDecision.Fail;

    public static bool CanRetry(int attempt) => Decide(attempt) == AttemptDecision.Retry;

    /// <summary>The fixed phrase used in the log and in the status line.</summary>
    public static string Reason(GptFailure failure) => failure switch {
        GptFailure.SendSide => "send failed",
        GptFailure.ResponseStartTimeout => "response-start timeout",
        GptFailure.ResponseStalled => "response stalled",
        GptFailure.ResponseCeiling => "response ceiling reached",
        _ => "invalid output"
    };

    /// <summary>Recorded on the job when the attempts are exhausted. Additive: FailureReason already exists.</summary>
    public static string FailureReason(GptFailure failure) => failure switch {
        GptFailure.SendSide => "GptSendFailed",
        GptFailure.ResponseStartTimeout => "GptNoResponse",
        GptFailure.ResponseStalled => "GptStalled",
        GptFailure.ResponseCeiling => "GptStalled",
        _ => "GptInvalidOutput"
    };

    public static string AttemptLog(int attempt, GptFailure failure, string jobId) =>
        $"GPT attempt {attempt}/{MaxAttempts} {Reason(failure)} {jobId}";

    public static string ExhaustedLog(string jobId) =>
        $"GPT retries exhausted {jobId}; queue job marked Failed";

    public static string CaptureTimeoutLog(string jobId) =>
        $"GPT capture timeout {jobId}; queue job marked Failed";

    /// <summary>The queue status line: plain on the first attempt, numbered afterwards.</summary>
    public static string AttemptStatus(int attempt) =>
        attempt <= 1 ? "Processing" : $"Processing — GPT attempt {attempt}/{MaxAttempts}";

    /// <summary>What the user is told when a retry is about to run.</summary>
    public static string RetryMessage(int nextAttempt, GptFailure failure) =>
        $"ChatGPT {Reason(failure)} — starting a fresh conversation and sending the same request again " +
        $"(attempt {nextAttempt} of {MaxAttempts}).";

    /// <summary>What the user is told when the job is given up on.</summary>
    public static string ExhaustedMessage(GptFailure failure) =>
        $"ChatGPT {Reason(failure)} on all {MaxAttempts} attempts — the job is marked Failed. " +
        "Retry Failed re-queues it.";
}

/// <summary>
/// "The copy keystroke has already been asked for on this attempt."
///
/// The ambiguous ReadyUnconfirmed state (idle at the start budget, generation never observed) may be
/// a very fast answer that finished before the first poll. It gets ONE capture opportunity — the same
/// keystroke the confirmed Ready path sends — so a successful fast answer is not later mistaken for
/// "no response" and re-sent. If generation then appears and the answer is confirmed Ready, the
/// keystroke must NOT be sent a second time for the same attempt.
///
/// A plain latch, kept here so the rule is unit-tested without WPF or a real keyboard.
/// </summary>
public sealed class CaptureRequestGate {
    bool _requested;

    /// <summary>True the first time it is called for an attempt; false every time after that.</summary>
    public bool TryRequest() {
        if (_requested) return false;
        _requested = true;
        return true;
    }

    public bool Requested => _requested;

    /// <summary>Called when a new attempt starts (first send, or a retry).</summary>
    public void ResetForAttempt() => _requested = false;
}
