using System.Net;
using System.Net.Http.Json;
using System.Text;
using HonestLicenseServer.Data;
using HonestLicenseServer.Infrastructure;
using HonestLicenseServer.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HonestLicenseServer.IntegrationTests;

[Collection(IntegrationTestCollection.Name)]
public sealed class ConnectionRequestErasureTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly DateTime Now = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("New", 30, true)]
    [InlineData("Rejected", 30, true)]
    [InlineData("New", 29, false)]
    [InlineData("Rejected", 29, false)]
    [InlineData("InProgress", 90, false)]
    [InlineData("Completed", 30, true)]
    [InlineData("Completed", 29, false)]
    public async Task Retention_uses_current_status_and_its_age(string status, int age, bool erased)
    {
        await WithDatabaseAsync(async (db, _) =>
        {
            var item = Request(status, Now.AddDays(-age));
            db.ConnectionRequests.Add(item);
            await db.SaveChangesAsync();
            db.AuditEvents.Add(Audit(item.Id));
            await db.SaveChangesAsync();
            var service = new ConnectionRequestDeletionService(db, new FixedClock());
            Assert.Equal(erased ? 1 : 0, await service.DeleteExpiredAsync(default));
            Assert.Equal(!erased, await db.ConnectionRequests.AnyAsync(x => x.Id == item.Id));
            Assert.Equal(!erased, await db.AuditEvents.AnyAsync(x => x.EntityType == "ConnectionRequest" && x.EntityId == item.Id.ToString()));
        });
    }

    [Fact]
    public async Task Recent_status_entry_protects_old_requests_and_legacy_null_uses_creation_time()
    {
        await WithDatabaseAsync(async (db, _) =>
        {
            db.ConnectionRequests.AddRange(Request("New", Now.AddDays(-2)), Request("Rejected", Now.AddDays(-2)));
            var legacy = Request("New", null);
            db.ConnectionRequests.Add(legacy);
            await db.SaveChangesAsync();
            Assert.Equal(1, await new ConnectionRequestDeletionService(db, new FixedClock()).DeleteExpiredAsync(default));
            Assert.False(await db.ConnectionRequests.AnyAsync(x => x.Id == legacy.Id));
            Assert.Equal(2, await db.ConnectionRequests.CountAsync());
        });
    }

    [Fact]
    public async Task Cleanup_handles_multiple_batches_and_deletes_closed_but_not_active_requests()
    {
        await WithDatabaseAsync(async (db, _) =>
        {
            db.ConnectionRequests.AddRange(Enumerable.Range(0, 205).Select(_ => Request("New", Now.AddDays(-31))));
            db.ConnectionRequests.AddRange(Request("InProgress", Now.AddDays(-90)), Request("Completed", Now.AddDays(-90)));
            await db.SaveChangesAsync();
            Assert.Equal(206, await new ConnectionRequestDeletionService(db, new FixedClock()).DeleteExpiredAsync(default));
            Assert.Equal(1, await db.ConnectionRequests.CountAsync());
        });
    }

    [Fact]
    public async Task Permanent_delete_scrubs_request_audit_and_database_and_wal_bytes()
    {
        await WithDatabaseAsync(async (db, path) =>
        {
            string marker = "PERSONAL-ERASURE-" + Guid.NewGuid().ToString("N");
            var item = Request("Completed", Now);
            item.ContactName = item.Comment = item.ConsentText = item.NotificationError = marker;
            db.ConnectionRequests.Add(item);
            await db.SaveChangesAsync();
            var audit = Audit(item.Id);
            audit.IpAddress = audit.DetailsJson = marker;
            db.AuditEvents.Add(audit);
            db.AuditEvents.Add(new AuditEvent { EntityType = "SupportRequest", EntityId = item.Id.ToString(),
                Action = "Unrelated", OccurredAtUtc = Now, DetailsJson = "{}", CorrelationId = "synthetic" });
            await db.SaveChangesAsync();
            var service = new ConnectionRequestDeletionService(db, new FixedClock());
            Assert.Equal(1, await service.DeleteAsync(item.Id, default));
            Assert.Equal(0, await service.DeleteAsync(item.Id, default));
            Assert.False(await db.ConnectionRequests.AnyAsync());
            Assert.False(await db.AuditEvents.AnyAsync(x => x.EntityType == "ConnectionRequest"));
            Assert.True(await db.AuditEvents.AnyAsync(x => x.EntityType == "SupportRequest"));
            foreach (string file in new[] { path, path + "-wal", path + "-journal" }.Where(File.Exists))
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory);
                Assert.DoesNotContain(marker, Encoding.UTF8.GetString(memory.ToArray()));
            }
        });
    }

    [Fact]
    public async Task Blocked_wal_checkpoint_is_not_reported_as_full_erasure_and_can_be_retried()
    {
        await WithDatabaseAsync(async (db, path) =>
        {
            var item = Request("New", Now);
            db.ConnectionRequests.Add(item);
            await db.SaveChangesAsync();
            await using var readerConnection = new SqliteConnection($"Data Source={path};Pooling=False");
            await readerConnection.OpenAsync();
            await using (var snapshot = readerConnection.BeginTransaction(deferred: true))
            {
                await using var command = readerConnection.CreateCommand();
                command.Transaction = (SqliteTransaction)snapshot;
                command.CommandText = "SELECT COUNT(*) FROM ConnectionRequests;";
                await command.ExecuteScalarAsync();
                var service = new ConnectionRequestDeletionService(db, new FixedClock());
                var error = await Record.ExceptionAsync(() => service.DeleteAsync(item.Id, default));
                Assert.True(error is ConnectionRequestErasurePendingException || error is SqliteException { SqliteErrorCode: 5 or 6 });
            }
            Assert.Equal(0, await new ConnectionRequestDeletionService(db, new FixedClock()).DeleteAsync(item.Id, default));
            Assert.False(await db.ConnectionRequests.AnyAsync());
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("incorrect")]
    public async Task Delete_requires_admin_key(string? key)
    {
        int id = await SeedApiRequestAsync();
        using var client = factory.CreateClient();
        if (key is not null) client.DefaultRequestHeaders.Add("X-Admin-Key", key);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.DeleteAsync($"/api/admin/connection-requests/{id}")).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<HonestDbContext>().ConnectionRequests.AnyAsync(x => x.Id == id));
    }

    [Fact]
    public async Task Admin_can_erase_any_status_and_repeating_delete_is_safe()
    {
        int id = await SeedApiRequestAsync();
        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", ApiFactory.AdminKey);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/connection-requests/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/connection-requests/{id}")).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<HonestDbContext>().ConnectionRequests.AnyAsync(x => x.Id == id));
    }

    [Fact]
    public async Task Actual_status_change_restarts_retention_but_same_status_does_not()
    {
        int id = await SeedApiRequestAsync();
        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", ApiFactory.AdminKey);
        DateTime before = DateTime.UtcNow;
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/admin/connection-requests/{id}/status",
            new { status = "Rejected", expectedStatus = "Completed" })).StatusCode);
        DateTime entered;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var item = await scope.ServiceProvider.GetRequiredService<HonestDbContext>().ConnectionRequests.SingleAsync(x => x.Id == id);
            entered = item.StatusChangedAtUtc!.Value;
            Assert.InRange(entered, before, DateTime.UtcNow);
        }
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/admin/connection-requests/{id}/status",
            new { status = "Rejected", expectedStatus = "Rejected" })).StatusCode);
        await using var check = factory.Services.CreateAsyncScope();
        Assert.Equal(entered, (await check.ServiceProvider.GetRequiredService<HonestDbContext>()
            .ConnectionRequests.SingleAsync(x => x.Id == id)).StatusChangedAtUtc);
    }

    [Fact]
    public async Task Schema_backfills_status_time_from_audit_or_creation_and_is_idempotent()
    {
        await WithDatabaseAsync(async (db, _) =>
        {
            var item = Request("Rejected", null);
            var legacy = Request("New", null);
            db.AddRange(item, legacy);
            await db.SaveChangesAsync();
            var audit = Audit(item.Id);
            audit.OccurredAtUtc = Now.AddDays(-2);
            db.AuditEvents.Add(audit);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE ConnectionRequests DROP COLUMN StatusChangedAtUtc");
            await DatabaseSchema.EnsureCurrentAsync(db);
            await DatabaseSchema.EnsureCurrentAsync(db);
            db.ChangeTracker.Clear();
            Assert.Equal(audit.OccurredAtUtc, (await db.ConnectionRequests.SingleAsync(x => x.Id == item.Id)).StatusChangedAtUtc);
            Assert.Equal(legacy.CreatedAtUtc, (await db.ConnectionRequests.SingleAsync(x => x.Id == legacy.Id)).StatusChangedAtUtc);
        });
    }

    [Fact]
    public async Task Closed_requests_are_removed_in_the_sweep_before_the_30_day_deadline()
    {
        await WithDatabaseAsync(async (db, _) =>
        {
            var due = Request("Completed", Now.AddDays(-30).AddMinutes(14));
            var notDue = Request("Completed", Now.AddDays(-30).AddMinutes(16));
            db.AddRange(due, notDue);
            await db.SaveChangesAsync();
            Assert.Equal(1, await new ConnectionRequestDeletionService(db, new FixedClock()).DeleteExpiredAsync(default));
            Assert.False(await db.ConnectionRequests.AnyAsync(x => x.Id == due.Id));
            Assert.True(await db.ConnectionRequests.AnyAsync(x => x.Id == notDue.Id));
        });
    }

    private async Task<int> SeedApiRequestAsync()
    {
        // Start the host before inserting old dates, to keep the startup cleanup deterministic.
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HonestDbContext>();
        var item = Request("Completed", DateTime.UtcNow);
        db.ConnectionRequests.Add(item);
        await db.SaveChangesAsync();
        return item.Id;
    }

    private static ConnectionRequest Request(string status, DateTime? entered) => new()
    {
        ContactName = "Synthetic", Phone = "+79990000000", Status = status,
        CreatedAtUtc = Now.AddDays(-90), StatusChangedAtUtc = entered
    };

    private static AuditEvent Audit(int id) => new()
    {
        EntityType = "ConnectionRequest", EntityId = id.ToString(), Action = "ConnectionRequest.StatusChanged",
        OccurredAtUtc = Now, DetailsJson = "{}", CorrelationId = "synthetic"
    };

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(Now);
    }

    private static async Task WithDatabaseAsync(Func<HonestDbContext, string, Task> action)
    {
        string directory = Path.Combine(Path.GetTempPath(), "honest-erasure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "test.db");
        try
        {
            var options = new DbContextOptionsBuilder<HonestDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
            await using var db = new HonestDbContext(options);
            await db.Database.EnsureCreatedAsync();
            await db.Database.OpenConnectionAsync();
            await action(db, path);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
