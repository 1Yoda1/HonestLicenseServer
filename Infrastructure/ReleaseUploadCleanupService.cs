using HonestLicenseServer.Data;
using Microsoft.EntityFrameworkCore;

namespace HonestLicenseServer.Infrastructure;

public sealed class ReleaseUploadCleanupService(
    IServiceScopeFactory scopeFactory,
    IReleaseObjectStorage storage,
    ILogger<ReleaseUploadCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await CleanupAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Pending release upload cleanup failed."); }
            await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
        }
    }

    internal async Task CleanupAsync(CancellationToken cancellationToken)
    {
        if (!storage.IsConfigured) return;
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        HonestDbContext db = scope.ServiceProvider.GetRequiredService<HonestDbContext>();
        DateTime cutoff = DateTime.UtcNow.AddHours(-24);
        List<Models.ReleaseUpload> expired = await db.ReleaseUploads
            .Where(x => x.Status == "Pending" && x.CreatedAtUtc <= cutoff)
            .OrderBy(x => x.CreatedAtUtc).Take(100).ToListAsync(cancellationToken);
        foreach (Models.ReleaseUpload upload in expired)
        {
            if (string.IsNullOrWhiteSpace(upload.MultipartUploadId))
                await storage.DeleteAsync(upload.ObjectKey, cancellationToken);
            else
                await storage.AbortMultipartUploadAsync(
                    upload.ObjectKey, upload.MultipartUploadId, cancellationToken);
            upload.Status = "Expired";
        }
        if (expired.Count > 0) await db.SaveChangesAsync(cancellationToken);
    }
}
