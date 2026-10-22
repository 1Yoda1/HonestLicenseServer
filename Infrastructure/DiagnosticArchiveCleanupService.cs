using HonestLicenseServer.Data;
using Microsoft.EntityFrameworkCore;

namespace HonestLicenseServer.Infrastructure;

public sealed class DiagnosticArchiveCleanupService(
    IServiceScopeFactory scopeFactory,
    IDiagnosticObjectStorage storage,
    ILogger<DiagnosticArchiveCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Diagnostic archive cleanup failed.");
            }

            await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
        }
    }

    internal async Task CleanupAsync(CancellationToken cancellationToken)
    {
        if (!storage.IsConfigured) return;
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HonestDbContext>();
        DateTime now = DateTime.UtcNow;
        var expired = await db.DiagnosticArchives
            .Where(x => (x.Status == "Pending" || x.Status == "Available" || x.Status == "Downloaded") &&
                        x.ExpiresAtUtc <= now)
            .OrderBy(x => x.ExpiresAtUtc)
            .Take(100)
            .ToListAsync(cancellationToken);

        foreach (var archive in expired)
        {
            await storage.DeleteAsync(archive.ObjectKey, cancellationToken);
            archive.Status = "Expired";
            archive.DeletedAtUtc = now;
        }

        if (expired.Count > 0) await db.SaveChangesAsync(cancellationToken);
    }
}
