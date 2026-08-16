using HonestLicenseServer.Data;
using HonestLicenseServer.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HonestLicenseServer.IntegrationTests;

public sealed class DatabaseSchemaTests
{
    [Fact]
    public async Task Migration_adds_service_installation_access_table_to_existing_database()
    {
        await using var database = await TestDatabase.CreateAsync(legacyUniqueConstraint: false);
        var db = database.Context;
        await db.Database.ExecuteSqlRawAsync("DROP TABLE ServiceInstallationAccess;");

        await DatabaseSchema.EnsureCurrentAsync(db);

        Assert.Equal(1L, await CountRowsAsync(db,
            "SELECT name FROM sqlite_master WHERE type='table' AND name='ServiceInstallationAccess';"));
    }

    [Fact]
    public async Task Legacy_registration_constraint_is_replaced_by_pending_only_unique_index()
    {
        await using var database = await TestDatabase.CreateAsync(legacyUniqueConstraint: true);
        var db = database.Context;
        int clientId = await SeedClientAsync(db);
        db.DeviceRegistrationRequests.AddRange(
            Request(clientId, "history-device", "Approved", -3),
            Request(clientId, "history-device", "Rejected", -2),
            Request(clientId, "history-device", "Pending", -1));
        await db.SaveChangesAsync();

        Assert.Contains(await IndexesAsync(db), x =>
            x.Name.StartsWith("sqlite_autoindex_DeviceRegistrationRequests_", StringComparison.Ordinal) &&
            x.Unique && x.Origin == "u");

        await DatabaseSchema.EnsureCurrentAsync(db);

        var indexes = await IndexesAsync(db);
        Assert.DoesNotContain(indexes, x => x.Origin == "u" && x.Unique);
        var pendingIndex = Assert.Single(indexes, x =>
            x.Name == "UX_DeviceRegistrationRequests_OnePending");
        Assert.True(pendingIndex.Unique);
        Assert.Equal("c", pendingIndex.Origin);
        Assert.True(pendingIndex.Partial);
        Assert.Contains("WHERE Status = 'Pending'", pendingIndex.Sql, StringComparison.OrdinalIgnoreCase);

        db.DeviceRegistrationRequests.AddRange(
            Request(clientId, "history-device", "Approved", 1),
            Request(clientId, "history-device", "Rejected", 2));
        await db.SaveChangesAsync();
        db.DeviceRegistrationRequests.Add(Request(clientId, "history-device", "Pending", 3));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        Assert.Equal(2, await db.DeviceRegistrationRequests.CountAsync(x =>
            x.ClientId == clientId && x.ExternalDeviceId == "history-device" && x.Status == "Approved"));
        Assert.Equal(2, await db.DeviceRegistrationRequests.CountAsync(x =>
            x.ClientId == clientId && x.ExternalDeviceId == "history-device" && x.Status == "Rejected"));
        Assert.Equal(1, await db.DeviceRegistrationRequests.CountAsync(x =>
            x.ClientId == clientId && x.ExternalDeviceId == "history-device" && x.Status == "Pending"));
        Assert.Equal("ok", await ScalarAsync(db, "PRAGMA integrity_check;"));
        Assert.Equal(0L, await CountRowsAsync(db, "PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task Migration_stops_without_changing_data_when_duplicate_pending_exists()
    {
        await using var database = await TestDatabase.CreateAsync(legacyUniqueConstraint: false);
        var db = database.Context;
        int clientId = await SeedClientAsync(db);
        db.DeviceRegistrationRequests.AddRange(
            Request(clientId, "duplicate-pending", "Pending", -2),
            Request(clientId, "duplicate-pending", "Pending", -1));
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DatabaseSchema.EnsureCurrentAsync(db));
        Assert.Contains("has 2 Pending rows", exception.Message);
        Assert.Equal(2, await db.DeviceRegistrationRequests.CountAsync(x =>
            x.ClientId == clientId && x.ExternalDeviceId == "duplicate-pending" && x.Status == "Pending"));
        Assert.DoesNotContain(await IndexesAsync(db), x =>
            x.Name == "UX_DeviceRegistrationRequests_OnePending");
        Assert.Equal("ok", await ScalarAsync(db, "PRAGMA integrity_check;"));
        Assert.Equal(0L, await CountRowsAsync(db, "PRAGMA foreign_key_check;"));
    }

    private static async Task<int> SeedClientAsync(HonestDbContext db)
    {
        var client = new Client
        {
            ExternalClientId = "schema-client-" + Guid.NewGuid().ToString("N"),
            Name = "Schema Client", IsActive = true,
            CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return client.Id;
    }

    private static DeviceRegistrationRequest Request(int clientId, string deviceId,
        string status, int minuteOffset) => new()
    {
        ClientId = clientId,
        ExternalDeviceId = deviceId,
        RequestedName = status + " request",
        Status = status,
        RequestedAtUtc = DateTime.UtcNow.AddMinutes(minuteOffset),
        ResolvedAtUtc = status == "Pending" ? null : DateTime.UtcNow.AddMinutes(minuteOffset + 1)
    };

    private static async Task<List<IndexInfo>> IndexesAsync(HonestDbContext db)
    {
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        var rows = new List<(string Name, bool Unique, string Origin, bool Partial)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA index_list('DeviceRegistrationRequests');";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rows.Add((reader.GetString(1), reader.GetInt64(2) != 0,
                    reader.GetString(3), reader.GetInt64(4) != 0));
        }
        var result = new List<IndexInfo>();
        foreach (var row in rows)
            result.Add(new IndexInfo(row.Name, row.Unique, row.Origin, row.Partial,
                await IndexSqlAsync(connection, row.Name)));
        return result;
    }

    private static async Task<string> IndexSqlAsync(SqliteConnection connection, string name)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(sql, '') FROM sqlite_master WHERE type='index' AND name=$name;";
        command.Parameters.AddWithValue("$name", name);
        return (string)(await command.ExecuteScalarAsync() ?? "");
    }

    private static async Task<string> ScalarAsync(HonestDbContext db, string sql)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? "";
    }

    private static async Task<long> CountRowsAsync(HonestDbContext db, string sql)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        long count = 0;
        while (await reader.ReadAsync()) count++;
        return count;
    }

    private sealed record IndexInfo(
        string Name, bool Unique, string Origin, bool Partial, string Sql);

    private sealed class TestDatabase(string directory, HonestDbContext context) : IAsyncDisposable
    {
        public HonestDbContext Context { get; } = context;

        public static async Task<TestDatabase> CreateAsync(bool legacyUniqueConstraint)
        {
            string directory = Path.Combine(Path.GetTempPath(), "honest-schema-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string databasePath = Path.Combine(directory, "schema.db");
            var options = new DbContextOptionsBuilder<HonestDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False").Options;
            var db = new HonestDbContext(options);
            await db.Database.EnsureCreatedAsync();
            string schemaSql = legacyUniqueConstraint ? """
                DROP INDEX IF EXISTS UX_DeviceRegistrationRequests_OnePending;
                DROP TABLE DeviceRegistrationRequests;
                CREATE TABLE DeviceRegistrationRequests (
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
                    FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE,
                    UNIQUE (ClientId, ExternalDeviceId, Status)
                );
                CREATE INDEX IX_DeviceRegistrationRequests_Status
                    ON DeviceRegistrationRequests(Status, RequestedAtUtc);
                """ : """
                DROP INDEX IF EXISTS UX_DeviceRegistrationRequests_OnePending;
                DROP TABLE DeviceRegistrationRequests;
                CREATE TABLE DeviceRegistrationRequests (
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
                CREATE INDEX IX_DeviceRegistrationRequests_Status
                    ON DeviceRegistrationRequests(Status, RequestedAtUtc);
                """;
            await db.Database.ExecuteSqlRawAsync(schemaSql);
            return new TestDatabase(directory, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
