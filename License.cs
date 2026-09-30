using System.Security.Cryptography;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace ResumeBuilder;

/// <summary>
/// Version 2 offline license contract, matching the License Generator. The signed bytes are exactly
/// the UTF-8 JSON payload bytes encoded before the dot. Do not reserialize before verification.
/// </summary>
public sealed class LicensePayload {
    public int Version { get; set; } = 2;
    public string Product { get; set; } = "ResumeBuilder";
    public string LicenseId { get; set; } = "";
    public string MachineId { get; set; } = "";
    public string Customer { get; set; } = "";
    public string Edition { get; set; } = "";
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string[] Features { get; set; } = Array.Empty<string>();
    public string KeyId { get; set; } = "";
}

public enum LicenseStatus {
    Valid,
    Missing,
    Malformed,
    InvalidSignature,
    UnsupportedVersion,
    WrongProduct,
    Expired,
    NotYetValid,
    WrongMachine,
    MachineUnavailable,
    WrongKey
}

public sealed record LicenseVerificationResult(LicenseStatus Status, LicensePayload? License = null) {
    public bool IsValid => Status == LicenseStatus.Valid;
}

/// <summary>
/// One stable id for this Windows installation. It is a hash, so the raw MachineGuid is never
/// shown, stored in a license by this app, or written to a log. The License Generator stores the
/// id the user pastes; comparison uses <see cref="Normalize"/> on both sides.
/// </summary>
public static class MachineId {
    public static string Normalize(string? value) {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var sb = new StringBuilder(value.Length);
        foreach (var c in value) {
            if (char.IsWhiteSpace(c)) continue;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>Lowercase hex SHA-256 of the normalized Windows MachineGuid, or empty when it cannot be read.</summary>
    public static string Current() {
        try {
            var guid = ReadMachineGuid();
            if (guid.Length == 0) return "";
            var material = Encoding.UTF8.GetBytes("ResumeBuilder.MachineId.v1:" + guid);
            return Convert.ToHexString(SHA256.HashData(material)).ToLowerInvariant();
        } catch {
            return "";
        }
    }

    static string ReadMachineGuid() {
        // Registry64 keeps the same MachineGuid whether this process is 32-bit or 64-bit.
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        var raw = key?.GetValue("MachineGuid") as string;
        var guid = Normalize(raw).Replace("{", "").Replace("}", "");
        return guid;
    }
}

/// <summary>
/// Verifies a v2 offline license using ECDSA P-256/SHA-256. No private key is ever needed here.
/// </summary>
public sealed class LicenseVerifier {
    public const string ProductName = "ResumeBuilder";
    public const int SupportedVersion = 2;
    public const string ProductionKeyId = "rb-prod-2";

    readonly byte[] _publicKeySpki;
    readonly Func<DateTimeOffset> _clock;
    readonly string _expectedProduct;
    readonly string _currentMachineId;

    public LicenseVerifier(byte[] publicKeySpki, Func<DateTimeOffset>? clock = null, string expectedProduct = ProductName, string? currentMachineId = null) {
        if (publicKeySpki is null || publicKeySpki.Length == 0) throw new ArgumentException("A public key is required.", nameof(publicKeySpki));
        _publicKeySpki = publicKeySpki.ToArray();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _expectedProduct = expectedProduct;
        _currentMachineId = currentMachineId ?? "";
    }

    public LicenseVerificationResult Verify(string? licenseText) {
        if (string.IsNullOrWhiteSpace(licenseText)) return new(LicenseStatus.Missing);

        var parts = licenseText.Trim().Split('.', 2);
        if (parts.Length != 2) return new(LicenseStatus.Malformed);

        byte[] payloadBytes;
        byte[] signature;
        try {
            payloadBytes = Base64Url.Decode(parts[0]);
            signature = Base64Url.Decode(parts[1]);
        } catch (FormatException) {
            return new(LicenseStatus.Malformed);
        }

        if (payloadBytes.Length == 0 || signature.Length == 0) return new(LicenseStatus.Malformed);

        bool signatureValid;
        try {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(_publicKeySpki, out _);
            signatureValid = ecdsa.VerifyData(payloadBytes, signature, HashAlgorithmName.SHA256);
        } catch (CryptographicException) {
            return new(LicenseStatus.Malformed);
        }

        // Parse only after the signature has been verified. This keeps the signed payload as the
        // source of truth and avoids accepting a modified payload with a reused signature.
        if (!signatureValid) return new(LicenseStatus.InvalidSignature);

        LicensePayload? payload;
        try {
            payload = JsonSerializer.Deserialize<LicensePayload>(payloadBytes);
        } catch (JsonException) {
            return new(LicenseStatus.Malformed);
        }

        if (payload is null) return new(LicenseStatus.Malformed);
        if (payload.Version != SupportedVersion) return new(LicenseStatus.UnsupportedVersion);
        if (!string.Equals(payload.Product, _expectedProduct, StringComparison.Ordinal)) return new(LicenseStatus.WrongProduct);

        var now = _clock();
        if (payload.IssuedAt > now) return new(LicenseStatus.NotYetValid);
        if (payload.ExpiresAt is not null && payload.ExpiresAt.Value < now) return new(LicenseStatus.Expired);

        if (string.IsNullOrWhiteSpace(payload.LicenseId) ||
            string.IsNullOrWhiteSpace(payload.MachineId) ||
            string.IsNullOrWhiteSpace(payload.Customer) ||
            string.IsNullOrWhiteSpace(payload.Edition) ||
            string.IsNullOrWhiteSpace(payload.KeyId))
            return new(LicenseStatus.Malformed);
        if (!string.Equals(payload.KeyId, ProductionKeyId, StringComparison.Ordinal))
            return new(LicenseStatus.WrongKey);

        var current = MachineId.Normalize(_currentMachineId);
        if (current.Length == 0) return new(LicenseStatus.MachineUnavailable);
        if (!string.Equals(MachineId.Normalize(payload.MachineId), current, StringComparison.Ordinal))
            return new(LicenseStatus.WrongMachine);

        return new(LicenseStatus.Valid, payload);
    }
}

/// <summary>The license log lines. Callers write them; this stays free of the diagnostics file.</summary>
public static class LicenseReport {
    public static void Write(LicenseVerificationResult result, Action<string> log) {
        if (result.Status == LicenseStatus.WrongMachine)
            log("LICENSE machine-match=false");
        else if (result.IsValid)
            log("LICENSE machine-match=true");

        log(result.IsValid
            ? "LICENSE verification=valid"
            : "LICENSE verification=invalid reason=" + Reason(result.Status));
    }

    public static string Reason(LicenseStatus status) => status switch {
        LicenseStatus.Missing => "missing",
        LicenseStatus.Malformed => "malformed",
        LicenseStatus.InvalidSignature => "invalid-signature",
        LicenseStatus.UnsupportedVersion => "unsupported-version",
        LicenseStatus.WrongProduct => "wrong-product",
        LicenseStatus.Expired => "expired",
        LicenseStatus.NotYetValid => "not-yet-valid",
        LicenseStatus.WrongMachine => "wrong-machine",
        LicenseStatus.MachineUnavailable => "machine-id-unavailable",
        LicenseStatus.WrongKey => "wrong-key",
        _ => "invalid"
    };
}

/// <summary>Local license file. It is deliberately separate from settings.json.</summary>
public static class LicenseStore {
    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ResumeBuilder", "license.lic");

    public static string? Load(string? path = null) {
        var file = path ?? Path;
        try {
            return File.Exists(file) ? File.ReadAllText(file, Encoding.UTF8) : null;
        } catch (IOException) {
            return null;
        } catch (UnauthorizedAccessException) {
            return null;
        }
    }

    /// <summary>
    /// Writes the already-signed license text atomically. The generator is responsible for creating
    /// the license; the application only stores the resulting signed text.
    /// </summary>
    public static void Save(string licenseText, string? path = null) {
        if (string.IsNullOrWhiteSpace(licenseText)) throw new ArgumentException("License text is required.", nameof(licenseText));
        var file = System.IO.Path.GetFullPath(path ?? Path);
        var dir = System.IO.Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = file + ".tmp";
        try {
            File.WriteAllText(temp, licenseText.Trim() + Environment.NewLine, new UTF8Encoding(false));
            if (!File.Exists(file)) File.Move(temp, file);
            else {
                try { File.Replace(temp, file, destinationBackupFileName: null, ignoreMetadataErrors: true); }
                catch (IOException) { File.Move(temp, file, overwrite: true); }
            }
        } finally {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }
}

internal static class Base64Url {
    public static string Encode(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    public static byte[] Decode(string value) {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += (normalized.Length % 4) switch { 0 => "", 2 => "==", 3 => "=", _ => throw new FormatException("Invalid base64url.") };
        return Convert.FromBase64String(normalized);
    }
}
