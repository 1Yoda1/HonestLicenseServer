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
        }
        finally
        {
            if (closeWhenDone) await connection.CloseAsync();
        }
    }

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
