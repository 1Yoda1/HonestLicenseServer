namespace HonestLicenseServer.Infrastructure;

public sealed class ConnectionRequestPrivacyOptions
{
    public bool Enabled { get; set; }
    // A new revision needs explicit approval; old Enabled=true settings cannot approve it.
    public string ApprovedDocumentVersion { get; set; } = "";
    public string LegalBasis { get; set; } = "Consent";
    public string PrivacyPolicyVersion { get; set; } = ConnectionRequestDocuments.Version;
    public string PrivacyPolicyText { get; set; } = ConnectionRequestDocuments.PrivacyPolicyText;
    public string ConsentVersion { get; set; } = ConnectionRequestDocuments.Version;
    public string ConsentText { get; set; } = ConnectionRequestDocuments.ConsentText;

    public bool ConsentRequired => true;
    public bool IsReady => Enabled && LegalBasis == "Consent" &&
        ApprovedDocumentVersion == ConnectionRequestDocuments.Version &&
        PrivacyPolicyVersion == ConnectionRequestDocuments.Version &&
        ConsentVersion == ConnectionRequestDocuments.Version &&
        PrivacyPolicyText == ConnectionRequestDocuments.PrivacyPolicyText &&
        ConsentText == ConnectionRequestDocuments.ConsentText;
}
