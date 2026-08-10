using Microsoft.EntityFrameworkCore;

namespace HonestLicenseServer.Data;

public static class DatabaseSchema
{
    public static Task EnsureCurrentAsync(HonestDbContext db, CancellationToken cancellationToken = default) =>
        db.Database.ExecuteSqlRawAsync("""
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
}
