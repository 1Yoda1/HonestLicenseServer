using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using System.Net;

namespace HonestLicenseServer.Infrastructure;

public sealed record ReleaseObjectInfo(long SizeBytes);

public interface IReleaseObjectStorage
{
    bool IsConfigured { get; }
    string CreateUploadUrl(string objectKey, DateTime expiresAtUtc);
    Task<string> InitiateMultipartUploadAsync(string objectKey, CancellationToken cancellationToken);
    string CreateMultipartPartUploadUrl(string objectKey, string uploadId, int partNumber,
        DateTime expiresAtUtc);
    Task CompleteMultipartUploadAsync(string objectKey, string uploadId,
        IReadOnlyList<ReleaseMultipartPart> parts, CancellationToken cancellationToken);
    Task AbortMultipartUploadAsync(string objectKey, string uploadId, CancellationToken cancellationToken);
    string CreateDownloadUrl(string objectKey, string fileName, DateTime expiresAtUtc);
    Task<ReleaseObjectInfo?> GetInfoAsync(string objectKey, CancellationToken cancellationToken);
    Task DeleteAsync(string objectKey, CancellationToken cancellationToken);
}

public sealed record ReleaseMultipartPart(int PartNumber, string ETag);

public sealed class S3ReleaseObjectStorage : IReleaseObjectStorage, IDisposable
{
    private readonly DiagnosticStorageOptions _options;
    private readonly AmazonS3Client? _client;

    public S3ReleaseObjectStorage(IOptions<DiagnosticStorageOptions> options)
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
            BucketName = _options.Bucket, Key = objectKey, Verb = HttpVerb.PUT,
            Expires = expiresAtUtc, ContentType = "application/octet-stream"
        });
    }

    public string CreateDownloadUrl(string objectKey, string fileName, DateTime expiresAtUtc)
    {
        EnsureConfigured();
        return _client!.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket, Key = objectKey, Verb = HttpVerb.GET,
            Expires = expiresAtUtc,
            ResponseHeaderOverrides = new ResponseHeaderOverrides
            {
                ContentDisposition = $"attachment; filename*=UTF-8''{Uri.EscapeDataString(fileName)}"
            }
        });
    }

    public async Task<string> InitiateMultipartUploadAsync(
        string objectKey, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        InitiateMultipartUploadResponse response = await _client!.InitiateMultipartUploadAsync(
            new InitiateMultipartUploadRequest
            {
                BucketName = _options.Bucket,
                Key = objectKey,
                ContentType = "application/octet-stream"
            }, cancellationToken);
        return response.UploadId;
    }

    public string CreateMultipartPartUploadUrl(string objectKey, string uploadId, int partNumber,
        DateTime expiresAtUtc)
    {
        EnsureConfigured();
        return _client!.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = objectKey,
            Verb = HttpVerb.PUT,
            Expires = expiresAtUtc,
            UploadId = uploadId,
            PartNumber = partNumber
        });
    }

    public async Task CompleteMultipartUploadAsync(string objectKey, string uploadId,
        IReadOnlyList<ReleaseMultipartPart> parts, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        await _client!.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = _options.Bucket,
            Key = objectKey,
            UploadId = uploadId,
            PartETags = parts.Select(x => new PartETag(x.PartNumber, x.ETag)).ToList()
        }, cancellationToken);
    }

    public async Task AbortMultipartUploadAsync(
        string objectKey, string uploadId, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        await _client!.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
        {
            BucketName = _options.Bucket,
            Key = objectKey,
            UploadId = uploadId
        }, cancellationToken);
    }

    public async Task<ReleaseObjectInfo?> GetInfoAsync(string objectKey, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        try
        {
            GetObjectMetadataResponse response = await _client!.GetObjectMetadataAsync(
                new GetObjectMetadataRequest { BucketName = _options.Bucket, Key = objectKey },
                cancellationToken);
            return new ReleaseObjectInfo(response.ContentLength);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    public async Task DeleteAsync(string objectKey, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        await _client!.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = _options.Bucket, Key = objectKey
        }, cancellationToken);
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured) throw new InvalidOperationException("Release object storage is not configured.");
    }

    public void Dispose() => _client?.Dispose();
}
