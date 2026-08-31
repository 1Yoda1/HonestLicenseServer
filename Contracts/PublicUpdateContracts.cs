namespace HonestLicenseServer.Contracts;

public sealed record HonestFlowUpdateResponse(
    string Application,
    string Version,
    string FileName,
    string Architecture,
    long SizeBytes,
    string Sha256,
    string DownloadUrl);
