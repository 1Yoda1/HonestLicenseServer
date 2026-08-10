using System.Security.Cryptography;

namespace HonestLicenseServer.Infrastructure;

public sealed class LicenseSignatureVerifier(IConfiguration configuration)
{
    public SignatureVerificationResult Verify(string keyId, byte[] grantBytes, byte[] signature)
    {
        var publicKeyBase64 = configuration[$"LicenseSigningKeys:{keyId}:PublicKeyBase64"];
        var publicKeyPem = configuration[$"LicenseSigningKeys:{keyId}:PublicKeyPem"];
        if (string.IsNullOrWhiteSpace(publicKeyBase64) && string.IsNullOrWhiteSpace(publicKeyPem))
            return SignatureVerificationResult.KeyNotConfigured;

        try
        {
            using var ecdsa = ECDsa.Create();
            if (!string.IsNullOrWhiteSpace(publicKeyBase64))
                ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            else
                ecdsa.ImportFromPem(publicKeyPem);
            var parameters = ecdsa.ExportParameters(false);
            if (parameters.Curve.Oid.Value != "1.2.840.10045.3.1.7")
                return SignatureVerificationResult.InvalidPublicKey;

            var valid = ecdsa.VerifyData(grantBytes, signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            if (!valid)
                valid = ecdsa.VerifyData(grantBytes, signature, HashAlgorithmName.SHA256,
                    DSASignatureFormat.Rfc3279DerSequence);
            return valid ? SignatureVerificationResult.Valid : SignatureVerificationResult.InvalidSignature;
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            return SignatureVerificationResult.InvalidPublicKey;
        }
    }
}

public enum SignatureVerificationResult
{
    Valid,
    KeyNotConfigured,
    InvalidPublicKey,
    InvalidSignature
}
