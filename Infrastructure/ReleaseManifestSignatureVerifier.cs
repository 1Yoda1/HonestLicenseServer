using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HonestLicenseServer.Infrastructure;

public sealed record VerifiedReleaseManifest(
    string Component, string Version, string FileName, string Architecture,
    long SizeBytes, string Sha256);

public sealed class ReleaseManifestSignatureVerifier(IConfiguration configuration)
{
    private const int MaxManifestBytes = 16 * 1024;

    public bool TryVerify(string keyId, string manifestBase64, string signatureBase64,
        out VerifiedReleaseManifest? manifest, out string error)
    {
        manifest = null;
        error = "invalid_release_manifest";
        string? publicKeyBase64 = configuration[$"ReleaseSigningKeys:{keyId}:PublicKeyBase64"];
        if (string.IsNullOrWhiteSpace(publicKeyBase64)) { error = "release_signing_key_not_configured"; return false; }
        try
        {
            byte[] manifestBytes = Convert.FromBase64String(manifestBase64);
            byte[] signature = Convert.FromBase64String(signatureBase64);
            if (manifestBytes.Length is 0 or > MaxManifestBytes || signature.Length is 0 or > 256)
                return false;
            using ECDsa verifier = ECDsa.Create();
            byte[] publicKey = Convert.FromBase64String(publicKeyBase64);
            verifier.ImportSubjectPublicKeyInfo(publicKey, out int bytesRead);
            if (bytesRead != publicKey.Length || verifier.KeySize != 256 ||
                verifier.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
            { error = "invalid_release_signing_key"; return false; }
            if (!verifier.VerifyData(manifestBytes, signature, HashAlgorithmName.SHA256,
                    DSASignatureFormat.Rfc3279DerSequence))
            { error = "invalid_release_signature"; return false; }

            string json = new UTF8Encoding(false, true).GetString(manifestBytes);
            ReleaseManifestData? data = JsonSerializer.Deserialize<ReleaseManifestData>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = false });
            if (data is null || string.IsNullOrWhiteSpace(data.Component) || data.Component.Length > 100 ||
                !IsValidReleaseVersion(data.Component, data.Version) || string.IsNullOrWhiteSpace(data.FileName) ||
                data.FileName.Length > 260 || data.FileName != Path.GetFileName(data.FileName) ||
                data.Architecture is not ("x86" or "x64" or "arm64" or "any") ||
                data.SizeBytes is <= 0 or > 4_294_967_296 ||
                data.Sha256?.Length != 64 || !data.Sha256.All(Uri.IsHexDigit))
                return false;
            manifest = new VerifiedReleaseManifest(data.Component.Trim(), data.Version!, data.FileName,
                data.Architecture, data.SizeBytes, data.Sha256.ToLowerInvariant());
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or JsonException or DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool IsValidReleaseVersion(string component, string? version)
    {
        if (string.IsNullOrWhiteSpace(version) || version.Length > 100 ||
            !char.IsAsciiLetterOrDigit(version[0]) ||
            !char.IsAsciiLetterOrDigit(version[^1]) ||
            version.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')))
            return false;

        return !string.Equals(component, "HonestFlow", StringComparison.OrdinalIgnoreCase) ||
            Version.TryParse(version, out _);
    }

    private sealed class ReleaseManifestData
    {
        [JsonPropertyName("component")]
        public string? Component { get; set; }
        [JsonPropertyName("version")]
        public string? Version { get; set; }
        [JsonPropertyName("fileName")]
        public string? FileName { get; set; }
        [JsonPropertyName("architecture")]
        public string? Architecture { get; set; }
        [JsonPropertyName("sizeBytes")]
        public long SizeBytes { get; set; }
        [JsonPropertyName("sha256")]
        public string? Sha256 { get; set; }
    }
}
