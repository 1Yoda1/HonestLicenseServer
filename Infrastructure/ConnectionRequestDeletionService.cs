using System.Data;
using HonestLicenseServer.Data;
using Microsoft.EntityFrameworkCore;

namespace HonestLicenseServer.Infrastructure;

public sealed class ConnectionRequestDeletionService(HonestDbContext db, TimeProvider clock)
{
    public Task<int> DeleteAsync(int id, CancellationToken cancellationToken) =>
        EraseAsync(id, cancellationToken);

    public async Task<int> DeleteExpiredAsync(CancellationToken cancellationToken)
    {
        int total = 0;
        int deleted;
        do
        {
            deleted = await EraseAsync(null, cancellationToken);
            total += deleted;
        } while (deleted == 100);
        return total;
    }

    private async Task<int> EraseAsync(int? requestId, CancellationToken cancellationToken)
    {
        bool closeConnection = db.Database.GetDbConnection().State != ConnectionState.Open;
        if (closeConnection) await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            // Apply to this connection before deleting either the request or its audit data.
            await db.Database.ExecuteSqlRawAsync("PRAGMA secure_delete=ON;", cancellationToken);
            int deleted;
            await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
            {
                // Sweep up to one interval early rather than retaining beyond the 30-day deadline.
                DateTime cutoff = clock.GetUtcNow().UtcDateTime.AddDays(-30).AddMinutes(15);
                int[] ids = requestId is int id ? [id] : await db.ConnectionRequests
                    .Where(x => (x.Status == "New" || x.Status == "Rejected" || x.Status == "Completed") &&
                        (x.StatusChangedAtUtc ?? x.CreatedAtUtc) <= cutoff)
                    .OrderBy(x => x.Id).Select(x => x.Id).Take(100).ToArrayAsync(cancellationToken);
                if (ids.Length > 0)
                {
                    string[] auditIds = ids.Select(x => x.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    await db.AuditEvents.Where(x => x.EntityType == "ConnectionRequest" && auditIds.Contains(x.EntityId))
                        .ExecuteDeleteAsync(cancellationToken);
                    deleted = await db.ConnectionRequests.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync(cancellationToken);
                }
                else deleted = 0;
                await transaction.CommitAsync(cancellationToken);
            }

            // Also retry checkpointing on empty sweeps: a previous attempt may have been blocked by readers.
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            command.CommandTimeout = 5;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken) && reader.GetInt32(0) != 0)
                throw new ConnectionRequestErasurePendingException();
            return deleted;
        }
        finally
        {
            if (closeConnection) await db.Database.CloseConnectionAsync();
        }
    }
}

public sealed class ConnectionRequestErasurePendingException : Exception
{
    public ConnectionRequestErasurePendingException() : base("Database journal cleanup is pending; retry the operation.") { }
}
