using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HonestLicenseServer.Data;

public static class DatabaseSchema
{
    public static async Task EnsureCurrentAsync(HonestDbContext db,
        CancellationToken cancellationToken = default)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS ComponentAssets (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Component TEXT NOT NULL,
                Version TEXT NOT NULL,
                Architecture TEXT NOT NULL DEFAULT 'any',
                FileName TEXT NOT NULL,
                DownloadUrl TEXT NULL,
                YandexPublicKey TEXT NULL,
                YandexPath TEXT NULL,
                Sha256 TEXT NULL,
                SizeBytes INTEGER NULL,
                UpdatedAtUtc TEXT NOT NULL,
                UNIQUE (Component, Version, Architecture)
            );
            """, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS SupportRequests (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId INTEGER NOT NULL,
                DeviceId INTEGER NULL,
                ExternalDeviceId TEXT NOT NULL,
                Subject TEXT NOT NULL,
                Message TEXT NOT NULL,
                Contact TEXT NOT NULL,
                HonestFlowVersion TEXT NULL,
                Status TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE RESTRICT,
                FOREIGN KEY (DeviceId) REFERENCES Devices(Id) ON DELETE SET NULL
            );
            CREATE INDEX IF NOT EXISTS IX_SupportRequests_Status_CreatedAtUtc
                ON SupportRequests(Status, CreatedAtUtc);
            """, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS ConnectionRequests (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CreatedAtUtc TEXT NOT NULL,
                ContactName TEXT NOT NULL,
                Company TEXT NULL,
                Phone TEXT NOT NULL,
                Email TEXT NULL,
                City TEXT NULL,
                WorkplaceCount INTEGER NOT NULL,
                InventorySystem TEXT NULL,
                Comment TEXT NULL,
                Source TEXT NULL,
                Status TEXT NOT NULL,
                IpAddress TEXT NULL,
                UserAgent TEXT NULL,
                NotificationSentAtUtc TEXT NULL,
                NotificationError TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_ConnectionRequests_Status_CreatedAtUtc
                ON ConnectionRequests(Status, CreatedAtUtc);
            """, cancellationToken);

        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != System.Data.ConnectionState.Open;
        if (closeWhenDone) await connection.OpenAsync(cancellationToken);
        try
        {
            if (!await HasColumnAsync(connection, "ComponentAssets", "Architecture", cancellationToken))
                await RebuildComponentAssetsAsync(connection, cancellationToken);

            await using var columns = connection.CreateCommand();
            columns.CommandText = "PRAGMA table_info(Licenses);";
            var hasGrantBytes = false;
            await using (var reader = await columns.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (string.Equals(reader.GetString(1), "GrantBytes", StringComparison.OrdinalIgnoreCase))
                    {
                        hasGrantBytes = true;
                        break;
                    }
                }
            }

            if (!hasGrantBytes)
            {
                await using var addColumn = connection.CreateCommand();
                addColumn.CommandText = "ALTER TABLE Licenses ADD COLUMN GrantBytes BLOB NULL;";
                await addColumn.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var backfill = connection.CreateCommand();
            backfill.CommandText = "UPDATE Licenses SET GrantBytes = CAST(GrantJson AS BLOB) WHERE GrantBytes IS NULL;";
            await backfill.ExecuteNonQueryAsync(cancellationToken);

            await EnsureColumnAsync(connection, "Licenses", "SignatureScope",
                "TEXT NOT NULL DEFAULT 'LegacySnapshot'", cancellationToken);
            await EnsureColumnAsync(connection, "Licenses", "SignatureVerifiedAtUtc",
                "TEXT NULL", cancellationToken);
            await EnsureColumnAsync(connection, "ClientSettings", "IdentificationCode",
                "TEXT NULL", cancellationToken);
            await EnsureColumnAsync(connection, "ClientSettings", "ChzToken",
                "TEXT NULL", cancellationToken);
            await EnsureColumnAsync(connection, "DeviceRegistrationRequests", "RequestedAddress",
                "TEXT NULL", cancellationToken);
            await EnsureColumnAsync(connection, "DeviceRegistrationRequests", "RequestedHonestFlowVersion",
                "TEXT NULL", cancellationToken);
            await EnsureDeviceRegistrationRequestPendingIndexAsync(connection, cancellationToken);
        }
        finally
        {
            if (closeWhenDone) await connection.CloseAsync();
        }
    }

    private static async Task EnsureDeviceRegistrationRequestPendingIndexAsync(
        DbConnection connection, CancellationToken cancellationToken)
    {
        var sqlite = (SqliteConnection)connection;
        await using var transaction = sqlite.BeginTransaction(
            IsolationLevel.Serializable, deferred: false);

        var duplicatePending = await FindDuplicatePendingAsync(connection, transaction, cancellationToken);
        if (duplicatePending is not null)
        {
            throw new InvalidOperationException(
                "DeviceRegistrationRequests migration cannot create the Pending uniqueness index: " +
                $"ClientId={duplicatePending.Value.ClientId}, " +
                $"ExternalDeviceId={duplicatePending.Value.ExternalDeviceId} has " +
                $"{duplicatePending.Value.Count} Pending rows. No data was changed.");
        }

        List<DatabaseIndex> indexes = await ReadIndexesAsync(
            connection, transaction, "DeviceRegistrationRequests", cancellationToken);
        DatabaseIndex[] legacyIndexes = indexes.Where(x => x.Unique &&
            x.Columns.SequenceEqual(
                new[] { "ClientId", "ExternalDeviceId", "Status" },
                StringComparer.OrdinalIgnoreCase)).ToArray();

        if (legacyIndexes.Any(x => string.Equals(x.Origin, "u", StringComparison.OrdinalIgnoreCase)))
        {
            await RebuildDeviceRegistrationRequestsAsync(
                connection, transaction, legacyIndexes, cancellationToken);
        }
        else
        {
            foreach (DatabaseIndex legacyIndex in legacyIndexes)
                await ExecuteAsync(connection, transaction,
                    $"DROP INDEX {QuoteIdentifier(legacyIndex.Name)};", cancellationToken);
        }

        await ExecuteAsync(connection, transaction, """
            CREATE UNIQUE INDEX IF NOT EXISTS UX_DeviceRegistrationRequests_OnePending
            ON DeviceRegistrationRequests(ClientId, ExternalDeviceId)
            WHERE Status = 'Pending';
            """, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task RebuildDeviceRegistrationRequestsAsync(
        DbConnection connection, DbTransaction transaction,
        IReadOnlyCollection<DatabaseIndex> legacyIndexes,
        CancellationToken cancellationToken)
    {
        var excludedIndexes = legacyIndexes.Select(x => x.Name)
            .Append("UX_DeviceRegistrationRequests_OnePending")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var schemaObjects = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT name, sql
                FROM sqlite_master
                WHERE tbl_name = 'DeviceRegistrationRequests'
                  AND type IN ('index', 'trigger')
                  AND sql IS NOT NULL
                ORDER BY type, name;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                string name = reader.GetString(0);
                if (!excludedIndexes.Contains(name)) schemaObjects.Add(reader.GetString(1));
            }
        }

        await ExecuteAsync(connection, transaction, """
            CREATE TABLE DeviceRegistrationRequests_New (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId INTEGER NOT NULL,
                ExternalDeviceId TEXT NOT NULL,
                RequestedName TEXT NOT NULL,
                Status TEXT NOT NULL CHECK (Status IN ('Pending','Approved','Rejected','Expired')),
                RequestedAtUtc TEXT NOT NULL,
                ResolvedAtUtc TEXT NULL,
                Comment TEXT NULL,
                RequestedAddress TEXT NULL,
                RequestedHonestFlowVersion TEXT NULL,
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );
            INSERT INTO DeviceRegistrationRequests_New (
                Id, ClientId, ExternalDeviceId, RequestedName, Status,
                RequestedAtUtc, ResolvedAtUtc, Comment,
                RequestedAddress, RequestedHonestFlowVersion)
            SELECT Id, ClientId, ExternalDeviceId, RequestedName, Status,
                RequestedAtUtc, ResolvedAtUtc, Comment,
                RequestedAddress, RequestedHonestFlowVersion
            FROM DeviceRegistrationRequests;
            DROP TABLE DeviceRegistrationRequests;
            ALTER TABLE DeviceRegistrationRequests_New RENAME TO DeviceRegistrationRequests;
            """, cancellationToken);

        foreach (string sql in schemaObjects)
            await ExecuteAsync(connection, transaction, sql + ";", cancellationToken);
    }

    private static async Task<(long ClientId, string ExternalDeviceId, long Count)?> FindDuplicatePendingAsync(
        DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT ClientId, ExternalDeviceId, COUNT(*)
            FROM DeviceRegistrationRequests
            WHERE Status = 'Pending'
            GROUP BY ClientId, ExternalDeviceId
            HAVING COUNT(*) > 1
            LIMIT 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return (reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2));
    }

    private static async Task<List<DatabaseIndex>> ReadIndexesAsync(
        DbConnection connection, DbTransaction transaction, string table,
        CancellationToken cancellationToken)
    {
        var indexes = new List<(string Name, bool Unique, string Origin)>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"PRAGMA index_list({QuoteIdentifier(table)});";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                indexes.Add((reader.GetString(1), reader.GetInt64(2) != 0, reader.GetString(3)));
        }

        var result = new List<DatabaseIndex>();
        foreach (var index in indexes)
        {
            var columns = new List<string>();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"PRAGMA index_info({QuoteIdentifier(index.Name)});";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) columns.Add(reader.GetString(2));
            result.Add(new DatabaseIndex(index.Name, index.Unique, index.Origin, columns));
        }
        return result;
    }

    private static async Task ExecuteAsync(DbConnection connection, DbTransaction transaction,
        string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string QuoteIdentifier(string value) =>
        "\"" + value.Replace("\"", "\"\"") + "\"";

    private sealed record DatabaseIndex(
        string Name, bool Unique, string Origin, IReadOnlyList<string> Columns);

    private static async Task EnsureColumnAsync(System.Data.Common.DbConnection connection,
        string table, string column, string definition, CancellationToken cancellationToken)
    {
        await using var columns = connection.CreateCommand();
        columns.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await columns.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
        await reader.DisposeAsync();

        await using var addColumn = connection.CreateCommand();
        addColumn.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        await addColumn.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> HasColumnAsync(System.Data.Common.DbConnection connection,
        string table, string column, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static async Task RebuildComponentAssetsAsync(System.Data.Common.DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE ComponentAssets_New (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Component TEXT NOT NULL,
                Version TEXT NOT NULL,
                Architecture TEXT NOT NULL DEFAULT 'any',
                FileName TEXT NOT NULL,
                DownloadUrl TEXT NULL,
                YandexPublicKey TEXT NULL,
                YandexPath TEXT NULL,
                Sha256 TEXT NULL,
                SizeBytes INTEGER NULL,
                UpdatedAtUtc TEXT NOT NULL,
                UNIQUE (Component, Version, Architecture)
            );
            INSERT INTO ComponentAssets_New (
                Id, Component, Version, Architecture, FileName, DownloadUrl,
                Sha256, SizeBytes, UpdatedAtUtc)
            SELECT Id, Component, Version, 'any', FileName, DownloadUrl,
                Sha256, SizeBytes, UpdatedAtUtc
            FROM ComponentAssets;
            DROP TABLE ComponentAssets;
            ALTER TABLE ComponentAssets_New RENAME TO ComponentAssets;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
