using System;

namespace ResumeBuilder;

public static class LicenseKeys {
    public const string ProductionKeyId = "rb-prod-2";

    // Public verification key only.
    // The private signing key must never be included in ResumeBuilder.
    private const string ProductionPublicKeyBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE29dJhlQ5TCbzw6WjwIoF/4gwvU37chjtBTvkRmWlhC3fmdrlbX6nZJRyDy5GptxLDVClO1ULcTDvneTdxQgwog==";

    public static byte[] ProductionPublicKey =>
        Convert.FromBase64String(ProductionPublicKeyBase64);
}