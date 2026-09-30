namespace ResumeBuilder;

/// <summary>Who may use the AI Workspace. Job Tasks and Email Tasks never hold it together.</summary>
public enum TailorMode {
    Idle,
    JobTasks,
    EmailTasks,
    PromptConversion
}

public enum TailorDecision {
    /// <summary>The workspace was free, or this mode is idle and may start its one loop.</summary>
    StartNow,
    /// <summary>This mode already owns the workspace, or that switch is already waiting. Do not start another loop.</summary>
    AlreadyActive,
    /// <summary>The other mode is active. Stop it after the current task, then start.</summary>
    WaitForCurrent
}

/// <summary>
/// The only tailoring mode. Both Start buttons ask here. Pending work stays on the tasks;
/// this object never rewrites a task.
/// </summary>
public sealed class TailorController {
    public TailorMode Mode { get; private set; } = TailorMode.Idle;

    /// <summary>The mode to start once the current processor has no task in progress.</summary>
    public TailorMode? Pending { get; private set; }

    public TailorDecision Request(TailorMode requested, bool requestedModeBusy) {
        if (requested == TailorMode.Idle)
            throw new ArgumentOutOfRangeException(nameof(requested));

        if (Pending == requested)
            return TailorDecision.AlreadyActive;

        if (Mode == requested) {
            if (requestedModeBusy || Pending is not null)
                return TailorDecision.AlreadyActive;
            return TailorDecision.StartNow;
        }

        if (Mode == TailorMode.Idle) {
            Mode = requested;
            Pending = null;
            return TailorDecision.StartNow;
        }

        Pending = requested;
        return TailorDecision.WaitForCurrent;
    }

    /// <summary>
    /// The active processor has no task in progress.
    /// Returns the mode that should start, or null when the workspace goes idle.
    /// </summary>
    public TailorMode? Release() {
        if (Pending is TailorMode next) {
            Mode = next;
            Pending = null;
            return next;
        }
        Mode = TailorMode.Idle;
        Pending = null;
        return null;
    }
}
