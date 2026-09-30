using System;
using System.IO;
using System.Windows;

namespace ResumeBuilder;

public partial class ActivationWindow : Window {
    readonly string _machineId;

    public ActivationWindow() {
        InitializeComponent();
        _machineId = MachineId.Current();
        MachineIdBox.Text = _machineId.Length == 0
            ? "Machine ID could not be read."
            : _machineId;
        Loaded += (_, _) => LicenseTextBox.Focus();
    }

    private void CopyMachineIdButton_Click(object sender, RoutedEventArgs e) {
        if (_machineId.Length == 0) {
            StatusText.Text = "Machine ID could not be read.";
            return;
        }

        var copied = ClipboardService.SetText(_machineId);
        StatusText.Text = copied.Success
            ? "Machine ID copied."
            : copied.Message;
    }

    private void ActivateButton_Click(object sender, RoutedEventArgs e) {
        StatusText.Text = "";

        var licenseText = LicenseTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(licenseText)) {
            var dialog = new Microsoft.Win32.OpenFileDialog {
                Title = "Import ResumeBuilder license",
                Filter = "License files (*.lic)|*.lic|All files (*.*)|*.*",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(this) != true) {
                StatusText.Text = "Please enter a license key.";
                LicenseTextBox.Focus();
                return;
            }

            try {
                licenseText = File.ReadAllText(dialog.FileName).Trim();
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                StatusText.Text = "The license file could not be read.";
                return;
            }

            LicenseTextBox.Text = licenseText;
        }

        if (string.IsNullOrWhiteSpace(licenseText)) {
            StatusText.Text = "Please enter a license key.";
            LicenseTextBox.Focus();
            return;
        }

        try {
            var verifier = new LicenseVerifier(
                LicenseKeys.ProductionPublicKey,
                currentMachineId: _machineId);

            var result = verifier.Verify(licenseText);
            LicenseReport.Write(result, PerfLog.Line);

            if (!result.IsValid) {
                StatusText.Text = result.Status switch {
                    LicenseStatus.Malformed =>
                        "The license key is malformed.",

                    LicenseStatus.InvalidSignature =>
                        "The license key is invalid.",

                    LicenseStatus.UnsupportedVersion =>
                        "This license version is not supported.",

                    LicenseStatus.WrongProduct =>
                        "This license is not for ResumeBuilder.",

                    LicenseStatus.Expired =>
                        "This license has expired.",

                    LicenseStatus.NotYetValid =>
                        "This license is not valid yet.",

                    LicenseStatus.WrongKey =>
                        "This license is not a current Resume Builder license.",

                    LicenseStatus.WrongMachine =>
                        "This license was created for a different computer.",

                    LicenseStatus.MachineUnavailable =>
                        "This computer's Machine ID could not be read.",

                    LicenseStatus.Missing =>
                        "Please enter a license key.",

                    _ =>
                        "The license could not be verified."
                };

                return;
            }

            LicenseStore.Save(licenseText);

            StatusText.Text = "ResumeBuilder activated successfully.";

            DialogResult = true;
            Close();
        }
        catch (IOException) {
            StatusText.Text =
                "The license is valid, but ResumeBuilder could not save it.";
        }
        catch (UnauthorizedAccessException) {
            StatusText.Text =
                "The license is valid, but ResumeBuilder does not have permission to save it.";
        }
        catch (Exception) {
            StatusText.Text =
                "An unexpected error occurred while activating ResumeBuilder.";
        }
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e) {
        DialogResult = false;
        Close();
    }
}
