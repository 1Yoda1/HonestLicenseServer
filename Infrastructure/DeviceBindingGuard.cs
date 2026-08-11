using System.Data;
using HonestLicenseServer.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HonestLicenseServer.Infrastructure;

public static class DeviceBindingGuard
{
    public const string ErrorCode = "device_bound_to_another_client";

    public static async Task<bool> ConflictsWithAnotherClientAsync(
        HonestDbContext db,
        int clientId,
        string externalDeviceId,
        CancellationToken cancellationToken = default)
    {
        var deviceOwners = await db.Devices.AsNoTracking()
            .Where(x => x.ExternalDeviceId == externalDeviceId)
            .Select(x => x.ClientId)
            .ToListAsync(cancellationToken);
        if (deviceOwners.Count > 0)
            return deviceOwners.Any(ownerClientId => ownerClientId != clientId);

        return await db.DeviceRegistrationRequests.AsNoTracking().AnyAsync(x =>
            x.ExternalDeviceId == externalDeviceId &&
            x.ClientId != clientId &&
            x.Status == "Pending",
            cancellationToken);
    }

    public static async Task<SqliteTransaction> BeginImmediateWriteAsync(
        HonestDbContext db,
        CancellationToken cancellationToken = default)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);
        await db.Database.UseTransactionAsync(transaction, cancellationToken);
        return transaction;
    }
}
