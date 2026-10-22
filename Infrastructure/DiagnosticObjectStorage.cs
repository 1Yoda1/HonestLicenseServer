using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using System.Net;

namespace HonestLicenseServer.Infrastructure;

public sealed class DiagnosticStorageOptions
{
    public string ServiceUrl { get; set; } = "https://s3.cloud.ru";
    public string Region { get; set; } = "ru-central-1";
    public string Bucket { get; set; } = "bucket-honestflow";
    public string? AccessKey { get; set; }
    public string? SecretKey { get; set; }
    public int RetentionHours { get; set; } = 48;
    public int UploadUrlMinutes { get; set; } = 15;
    public int DownloadUrlMinutes { get; set; } = 10;
}

public sealed record DiagnosticObjectInfo(long SizeBytes);

public interface IDiagnosticObjectStorage
{
    bool IsConfigured { get; }
    string CreateUploadUrl(string objectKey, DateTime expiresAtUtc);
    string CreateDownloadUrl(string objectKey, string fileName, DateTime expiresAtUtc);
    Task<DiagnosticObjectInfo?> GetInfoAsync(string objectKey, CancellationToken cancellationToken);
    Task DeleteAsync(string objectKey, CancellationToken cancellationToken);
}

public sealed class S3DiagnosticObjectStorage : IDiagnosticObjectStorage, IDisposable
{
    private readonly DiagnosticStorageOptions _options;
    private readonly AmazonS3Client? _client;

    public S3DiagnosticObjectStorage(IOptions<DiagnosticStorageOptions> options)
    {
        _options = options.Value;
        IsConfigured = !string.IsNullOrWhiteSpace(_options.ServiceUrl) &&
            !string.IsNullOrWhiteSpace(_options.Region) &&
            !string.IsNullOrWhiteSpace(_options.Bucket) &&
            !string.IsNullOrWhiteSpace(_options.AccessKey) &&
            !string.IsNullOrWhiteSpace(_options.SecretKey);
        if (!IsConfigured) return;

        _client = new AmazonS3Client(_options.AccessKey, _options.SecretKey, new AmazonS3Config
        {
            ServiceURL = _options.ServiceUrl,
            AuthenticationRegion = _options.Region,
            ForcePathStyle = true,
            // Cloud.ru implements the regular S3 SigV4 API, not AWS S3 Express
            // sessions or multi-region access points.
            DisableS3ExpressSessionAuth = true,
            DisableMultiregionAccessPoints = true,
            UseArnRegion = false
        });
    }

    public bool IsConfigured { get; }

    public string CreateUploadUrl(string objectKey, DateTime expiresAtUtc)
    {
        EnsureConfigured();
        return _client!.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = objectKey,
            Verb = HttpVerb.PUT,
            Expires = expiresAtUtc,
            ContentType = "application/zip"
        });
    }

    public string CreateDownloadUrl(string objectKey, string fileName, DateTime expiresAtUtc)
    {
        EnsureConfigured();
        return _client!.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = objectKey,
            Verb = HttpVerb.GET,
            Expires = expiresAtUtc,
            ResponseHeaderOverrides = new ResponseHeaderOverrides
            {
                ContentDisposition = $"attachment; filename*=UTF-8''{Uri.EscapeDataString(fileName)}"
            }
        });
    }

    public async Task<DiagnosticObjectInfo?> GetInfoAsync(string objectKey, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        try
        {
            GetObjectMetadataResponse response = await _client!.GetObjectMetadataAsync(
                new GetObjectMetadataRequest { BucketName = _options.Bucket, Key = objectKey }, cancellationToken);
            return new DiagnosticObjectInfo(response.ContentLength);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string objectKey, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        await _client!.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = _options.Bucket,
            Key = objectKey
        }, cancellationToken);
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured) throw new InvalidOperationException("Diagnostic object storage is not configured.");
    }

    public void Dispose() => _client?.Dispose();
}
