namespace HonestLicenseServer.Infrastructure;

public sealed class ConnectionRequestCleanupService(
    IServiceScopeFactory scopeFactory,
    ILogger<ConnectionRequestCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                int deleted = await scope.ServiceProvider.GetRequiredService<ConnectionRequestDeletionService>()
                    .DeleteExpiredAsync(stoppingToken);
                if (deleted > 0) logger.LogInformation("Expired connection requests erased: {Count}", deleted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Connection request erasure will be retried."); }
            await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
        }
    }
}
