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
                FileName TEXT NOT NULL,
                DownloadUrl TEXT NOT NULL,
                Sha256 TEXT NULL,
                SizeBytes INTEGER NULL,
                UpdatedAtUtc TEXT NOT NULL,
                UNIQUE (Component, Version)
            );
            """, cancellationToken);

        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != System.Data.ConnectionState.Open;
        if (closeWhenDone) await connection.OpenAsync(cancellationToken);
        try
        {
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
}
