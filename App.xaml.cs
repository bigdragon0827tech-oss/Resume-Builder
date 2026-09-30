using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace ResumeBuilder;

/// <summary>
/// Startup, in a fixed order:
///   1. crash handlers, so nothing after this can fail silently;
///   2. a profile argument opens that profile; otherwise the profile chooser is shown.
///      The same profile cannot be open twice. Different profiles can run together.
///   3. rotate diagnostics.log (the previous session's log is kept, not cleared);
///   4. the WebView2 Runtime check, with Retry / Exit instead of a raw exception;
///   5. only then the chooser, or that profile's MainWindow.
/// </summary>
public partial class App : System.Windows.Application {
    SingleInstance? _instance;
    SingleInstance? _profileLock;

    protected override void OnStartup(StartupEventArgs e) {
        base.OnStartup(e);
        InstallCrashHandlers();

        var requested = ProfileLaunch.ProfileId(e.Args);
        if (requested is null && e.Args.Any(a => string.Equals(a, "--profile", StringComparison.OrdinalIgnoreCase))) {
            System.Windows.MessageBox.Show("That profile was not found.", "Resume Builder",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        if (requested is not null) {
            _profileLock = ProfileLock.TryAcquire(requested);
            if (_profileLock is null) {
                if (!ProfileWindow.Activate(requested))
                    System.Windows.MessageBox.Show("This profile is already open.", "Resume Builder",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
            if (!ProfileContext.TryOpen(requested)) {
                System.Windows.MessageBox.Show("That profile was not found.", "Resume Builder",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
            ProfileLock.Remember(requested);
        }

        // The first workspace holds the installer mutex. Another profile still starts.
        if (requested is not null)
            _instance = SingleInstance.TryAcquire();

        PerfLog.StartSession();
        if (ProfileContext.IsOpen) {
            PerfLog.Line(ProfileContext.ProfileLogLine());
            PerfLog.Line(ProfileContext.DatabaseLogLine());
        }

        for (var status = WebView2Runtime.Check(); !status.Available; status = WebView2Runtime.Check()) {
            PerfLog.Line("STARTUP WebView2 runtime unavailable: " + status.Reason);
            var answer = System.Windows.MessageBox.Show(WebView2Runtime.Message(status), "Resume Builder",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) { Shutdown(); return; }
        }

        // Release requires a verified license. Debug skips it so a local run can open
        // the main window without a signed license. Release verification is unchanged.
#if !DEBUG
        // Verify the locally stored license before opening the main application.
        var machineId = MachineId.Current();
        if (machineId.Length > 0)
            PerfLog.Line("LICENSE machine-id-ready");

        var verifier = new LicenseVerifier(LicenseKeys.ProductionPublicKey, currentMachineId: machineId);
        var storedLicense = LicenseStore.Load();
        var licenseResult = verifier.Verify(storedLicense);
        LicenseReport.Write(licenseResult, PerfLog.Line);

        if (!licenseResult.IsValid) {
            PerfLog.Line("STARTUP license status: " + licenseResult.Status);

            var activation = new ActivationWindow();
            var activated = activation.ShowDialog();

            if (activated != true) {
                PerfLog.Line("STARTUP activation cancelled.");
                Shutdown();
                return;
            }

            // Verify again from disk. Do not trust the dialog result alone.
            storedLicense = LicenseStore.Load();
            licenseResult = verifier.Verify(storedLicense);
            LicenseReport.Write(licenseResult, PerfLog.Line);

            if (!licenseResult.IsValid) {
                PerfLog.Line("STARTUP license verification after activation failed: " + licenseResult.Status);

                System.Windows.MessageBox.Show(
                    "ResumeBuilder could not verify the activated license.",
                    "ResumeBuilder",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                Shutdown();
                return;
            }
        }

        PerfLog.Line(
            $"STARTUP license valid: {licenseResult.License!.LicenseId}");
#else
        PerfLog.Line("STARTUP license skipped (Debug build).");
#endif

        if (requested is null) {
            var migration = ProfileMigration.MigrateIfNeeded();
            if (migration.Failed)
                System.Windows.MessageBox.Show(migration.Error, "Resume Builder",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            var chooser = new ProfileChooserWindow();
            MainWindow = chooser;
            chooser.Show();
            return;
        }

        JobStore.EnsureReady();

        var main = new MainWindow();
        MainWindow = main;
        main.Show();
    }

    protected override void OnExit(ExitEventArgs e) {
        if (ProfileContext.ProfileId is string id) ProfileLock.Forget(id);
        _profileLock?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Every unhandled exception is written to crash-*.log in %LOCALAPPDATA%\ResumeBuilder before
    /// anything else happens. A UI-thread crash still ends the app, as it always did — continuing
    /// after an unknown failure could corrupt a job — but now it says where the details are.
    /// </summary>
    void InstallCrashHandlers() {
        DispatcherUnhandledException += (_, args) => {
            var file = CrashLog.Write(args.Exception, "UI thread");
            try {
                System.Windows.MessageBox.Show(
                    "Resume Builder hit an unexpected error and has to close.\n\n" +
                    (file is null ? "" : "Details were saved to:\n" + file),
                    "Resume Builder", MessageBoxButton.OK, MessageBoxImage.Error);
            } catch { /* the message is best effort */ }
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) => {
            if (args.ExceptionObject is Exception ex) CrashLog.Write(ex, "background thread");
        };

        // A faulted task nobody awaited: record it, and keep it from ending the process.
        TaskScheduler.UnobservedTaskException += (_, args) => {
            CrashLog.Write(args.Exception, "unobserved task");
            args.SetObserved();
        };
    }
}
