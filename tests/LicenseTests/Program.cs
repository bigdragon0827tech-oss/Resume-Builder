using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ResumeBuilder;

static class Program {
    static int _passed;
    static int _failed;

    static void Main() {
        Test("a signed license for this machine verifies", ValidLicense);
        Test("the same license on a different machine is rejected", DifferentMachineIsRejected);
        Test("an edited machine id fails the signature", EditedMachineIdIsRejected);
        Test("an rb-prod-1 key id is rejected", OldKeyIdIsRejected);
        Test("payload tampering is rejected", PayloadTamperingIsRejected);
        Test("signature tampering is rejected", SignatureTamperingIsRejected);
        Test("wrong product is rejected", WrongProductIsRejected);
        Test("expired license is rejected", ExpiredLicenseIsRejected);
        Test("future-issued license is rejected", FutureLicenseIsRejected);
        Test("perpetual license has no expiry", PerpetualLicenseIsValid);
        Test("malformed license is rejected", MalformedIsRejected);
        Test("a corrupted license is rejected", CorruptedLicenseIsRejected);
        Test("machine ids compare after the same normalization", MachineIdNormalizationMatches);
        Test("license store uses its own file and round-trips", StoreRoundTrips);

        Console.WriteLine($"{_passed} passed, {_failed} failed");
        if (_failed != 0) Environment.ExitCode = 1;
    }

    static void Test(string name, Action test) {
        try { test(); _passed++; Console.WriteLine("PASS: " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL: " + name + " — " + ex.Message); }
    }

    static (string Text, LicensePayload Payload) MakeLicense(ECDsa key, Action<LicensePayload>? edit = null) {
        var payload = new LicensePayload {
            LicenseId = "LIC-001",
            MachineId = "machine-a",
            Customer = "Test Customer",
            Edition = "Professional",
            IssuedAt = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero),
            ExpiresAt = null,
            Features = ["resume-tailoring"],
            KeyId = "rb-prod-2"
        };
        edit?.Invoke(payload);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var signature = key.SignData(bytes, HashAlgorithmName.SHA256);
        return ($"{Base64Url.Encode(bytes)}.{Base64Url.Encode(signature)}", payload);
    }

    static LicenseVerifier Verifier(ECDsa key, DateTimeOffset now, string machineId = "machine-a") {
        return new LicenseVerifier(key.ExportSubjectPublicKeyInfo(), () => now, currentMachineId: machineId);
    }

    static void ValidLicense() {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var license = MakeLicense(key).Text;
        var result = Verifier(key, new DateTimeOffset(2026, 9, 21, 12, 1, 0, TimeSpan.Zero)).Verify(license);
        Assert(result.IsValid && result.License?.LicenseId == "LIC-001" && result.License.Version == 2);
        var lines = new List<string>();
        LicenseReport.Write(result, lines.Add);
        Assert(lines[0] == "LICENSE machine-match=true");
        Assert(lines[1] == "LICENSE verification=valid");
    }

    static void DifferentMachineIsRejected() {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var text = MakeLicense(key).Text;
        var result = Verifier(key, new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero), "machine-b").Verify(text);
        Assert(result.Status == LicenseStatus.WrongMachine);
        var lines = new List<string>();
        LicenseReport.Write(result, lines.Add);
        Assert(lines[0] == "LICENSE machine-match=false");
        Assert(lines[1] == "LICENSE verification=invalid reason=wrong-machine");
    }

    static void OldKeyIdIsRejected() {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var text = MakeLicense(key, p => p.KeyId = "rb-prod-1").Text;
        var result = Verifier(key, new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero)).Verify(text);
        Assert(result.Status == LicenseStatus.WrongKey);
        var lines = new List<string>();
        LicenseReport.Write(result, lines.Add);
        Assert(lines[0] == "LICENSE verification=invalid reason=wrong-key");
    }

    static void EditedMachineIdIsRejected() {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var text = MakeLicense(key).Text;
        var parts = text.Split('.');
        var payload = JsonSerializer.Deserialize<LicensePayload>(Base64Url.Decode(parts[0]))!;
        payload.MachineId = "machine-b";
        var modified = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(payload)) + "." + parts[1];
        Assert(Verifier(key, new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero)).Verify(modified).Status == LicenseStatus.InvalidSignature);
    }

    static void PayloadTamperingIsRejected() {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var text = MakeLicense(key).Text;
        var parts = text.Split('.');
        var payload = JsonSerializer.Deserialize<LicensePayload>(Base64Url.Decode(parts[0]))!;
        payload.Customer = "Attacker";
        var modified = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(payload)) + "." + parts[1];
        Assert(Verifier(key, new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero)).Verify(modified).Status == LicenseStatus.InvalidSignature);
    }

    static void SignatureTamperingIsRejected() {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var text = MakeLicense(key).Text;
        var parts = text.Split('.');
        var sig = Base64Url.Decode(parts[1]);
        sig[0] ^= 0x01;
        var modified = parts[0] + "." + Base64Url.Encode(sig);
        Assert(Verifier(key, new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero)).Verify(modified).Status == LicenseStatus.InvalidSignature);
    }

    static void WrongProductIsRejected() {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var text = MakeLicense(key, p => p.Product = "OtherProduct").Text;
        Assert(Verifier(key, new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero)).Verify(text).Status == LicenseStatus.WrongProduct);
    }

    static void ExpiredLicenseIsRejected() {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var text = MakeLicense(key, p => p.ExpiresAt = new DateTimeOffset(2026, 9, 20, 23, 59, 59, TimeSpan.Zero)).Text;
        Assert(Verifier(key, new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero)).Verify(text).Status == LicenseStatus.Expired);
    }

    static void FutureLicenseIsRejected() {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var text = MakeLicense(key, p => p.IssuedAt = new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero)).Text;
        Assert(Verifier(key, new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero)).Verify(text).Status == LicenseStatus.NotYetValid);
    }

    static void PerpetualLicenseIsValid() {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var text = MakeLicense(key).Text;
        var result = Verifier(key, new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero)).Verify(text);
        Assert(result.IsValid && result.License?.ExpiresAt is null);
    }

    static void MalformedIsRejected() {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var result = Verifier(key, DateTimeOffset.UtcNow).Verify("not-a-license");
        Assert(result.Status == LicenseStatus.Malformed);
    }

    static void CorruptedLicenseIsRejected() {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var text = MakeLicense(key).Text;
        var corrupted = text.Substring(0, text.Length / 2) + "###";
        var result = Verifier(key, new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero)).Verify(corrupted);
        Assert(result.Status is LicenseStatus.Malformed or LicenseStatus.InvalidSignature);
        var lines = new List<string>();
        LicenseReport.Write(result, lines.Add);
        Assert(lines[0].StartsWith("LICENSE verification=invalid reason=", StringComparison.Ordinal));
    }

    static void MachineIdNormalizationMatches() {
        Assert(MachineId.Normalize("  ABC\n123  ") == MachineId.Normalize("abc123"));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var text = MakeLicense(key, p => p.MachineId = "ABC 123").Text;
        var result = Verifier(key, new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero), "abc123").Verify(text);
        Assert(result.IsValid);
    }

    static void StoreRoundTrips() {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ResumeBuilder-LicenseTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, "license.lic");
        try {
            LicenseStore.Save("abc.def", path);
            Assert(File.Exists(path));
            Assert(LicenseStore.Load(path)?.Trim() == "abc.def");
            Assert(System.IO.Path.GetFileName(LicenseStore.Path) == "license.lic");
        } finally {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    static void Assert(bool condition) {
        if (!condition) throw new InvalidOperationException("assertion failed");
    }
}
