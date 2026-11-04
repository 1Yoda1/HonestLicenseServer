using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HonestLicenseServer.Data;
using HonestLicenseServer.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HonestLicenseServer.IntegrationTests;

[Collection(IntegrationTestCollection.Name)]
public sealed class ConnectionRequestPrivacyTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private WebApplicationFactory<Program> ConsentHost(bool enabled = true) => factory.WithWebHostBuilder(builder =>
        builder.ConfigureServices(services => services.PostConfigure<ConnectionRequestPrivacyOptions>(options =>
        {
            options.Enabled = enabled;
            options.LegalBasis = "Consent";
            options.ConsentVersion = "1.0";
            options.ConsentText = ConnectionRequestDocuments.ConsentText;
        })));

    [Theory]
    [InlineData(false, "1.0", HttpStatusCode.BadRequest)]
    [InlineData(true, "old-version", HttpStatusCode.Conflict)]
    [InlineData(true, null, HttpStatusCode.Conflict)]
    public async Task Direct_requests_cannot_bypass_consent(bool consent, string? version, HttpStatusCode expected)
    {
        using var host = ConsentHost();
        using var client = host.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HonestDbContext>();
        var before = await db.ConnectionRequests.CountAsync();
        var response = await client.PostAsJsonAsync("/api/connection-requests", new
        {
            contactName = "Synthetic", phone = "+7 999 000-00-00", city = "Test", workplaceCount = 1,
            privacyPolicyVersion = "1.0", consent, consentVersion = version
        });
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(before, await db.ConnectionRequests.CountAsync());
    }

    [Fact]
    public async Task Approved_consent_is_recorded_with_server_time_and_exact_text()
    {
        using var host = ConsentHost();
        using var client = host.CreateClient();
        var before = DateTime.UtcNow;
        var response = await client.PostAsJsonAsync("/api/connection-requests", new
        {
            contactName = "Synthetic", phone = "+7 999 000-00-00", city = "Test", workplaceCount = 1,
            privacyPolicyVersion = "1.0", consent = true, consentVersion = "1.0"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        await using var scope = factory.Services.CreateAsyncScope();
        var item = await scope.ServiceProvider.GetRequiredService<HonestDbContext>().ConnectionRequests
            .SingleAsync(x => x.Id == body.GetProperty("requestId").GetInt32());
        Assert.Equal("Consent", item.ProcessingLegalBasis);
        Assert.Equal("1.0", item.PrivacyPolicyVersion);
        Assert.Equal("1.0", item.ConsentVersion);
        Assert.Equal(ConnectionRequestDocuments.ConsentText, item.ConsentText);
        Assert.InRange(item.ConsentReceivedAtUtc!.Value, before, DateTime.UtcNow);
    }

    [Fact]
    public async Task Unapproved_documents_are_neither_published_nor_accepted()
    {
        using var host = ConsentHost(false);
        using var client = host.CreateClient();
        var policy = await client.GetAsync("/api/connection-requests/policy");
        Assert.Contains("no-store", policy.Headers.CacheControl!.ToString());
        var body = await policy.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("available").GetBoolean());
        Assert.False(body.TryGetProperty("consentText", out _));
        var response = await client.PostAsJsonAsync("/api/connection-requests", new
        {
            contactName = "Synthetic", phone = "+7 999 000-00-00", city = "Test", workplaceCount = 1,
            privacyPolicyVersion = "1.0", consent = true, consentVersion = "1.0"
        });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Theory]
    [InlineData("127.0.0.1", "203.0.113.8")]
    [InlineData("198.51.100.10", "198.51.100.10")]
    public async Task Forwarded_ip_is_accepted_only_from_trusted_proxy(string remote, string expected)
    {
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton<IStartupFilter>(new RemoteAddressFilter(remote))));
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.8");
        var response = await client.PostAsJsonAsync("/api/connection-requests", new
        {
            contactName = "Proxy test", phone = "+7 999 000-00-00", city = "Test", workplaceCount = 1,
            privacyPolicyVersion = "1.0", consent = true, consentVersion = "1.0"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        await using var scope = factory.Services.CreateAsyncScope();
        var item = await scope.ServiceProvider.GetRequiredService<HonestDbContext>().ConnectionRequests
            .SingleAsync(x => x.Id == body.GetProperty("requestId").GetInt32());
        Assert.Equal(expected, item.IpAddress);
    }

    [Fact]
    public async Task Rate_limit_is_applied_to_resolved_address()
    {
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton<IStartupFilter>(new RemoteAddressFilter("198.51.100.99"))));
        using var client = host.CreateClient();
        for (var i = 0; i < 6; i++)
        {
            var response = await client.PostAsJsonAsync("/api/connection-requests", new
            {
                contactName = "Bot", phone = "+7 999 000-00-00", city = "Test", workplaceCount = 1,
                website = "honeypot"
            });
            Assert.Equal(i < 5 ? HttpStatusCode.NoContent : HttpStatusCode.TooManyRequests, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("1234567890", false)]
    [InlineData("+01234567890", false)]
    [InlineData("+7<script>9990000000", false)]
    [InlineData("+7 999 000-00-00", true)]
    [InlineData(" 8 (999) 000-00-00 ", true)]
    public void Phone_validation_rejects_invalid_numbers(string value, bool expected) =>
        Assert.Equal(expected, new HonestLicenseServer.Contracts.ConnectionPhoneAttribute().IsValid(value));

    [Fact]
    public async Task Native_form_post_never_falls_back_to_get_or_creates_a_request()
    {
        using var client = factory.CreateClient();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["contactName"] = "Synthetic", ["phone"] = "+7 999 000-00-00", ["city"] = "Test"
        });
        var response = await client.PostAsync("/api/connection-requests", content);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal("/api/connection-requests", response.RequestMessage!.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task Privacy_columns_are_added_idempotently_without_changing_existing_requests()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<HonestDbContext>().UseSqlite(connection).Options;
        await using var db = new HonestDbContext(options);
        await db.Database.EnsureCreatedAsync();
        foreach (var column in new[] { "ConsentVersion", "ConsentText", "ConsentReceivedAtUtc", "PrivacyPolicyVersion", "ProcessingLegalBasis" })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"ALTER TABLE ConnectionRequests DROP COLUMN {column}";
            await command.ExecuteNonQueryAsync();
        }
        await db.Database.ExecuteSqlRawAsync("INSERT INTO ConnectionRequests (CreatedAtUtc, ContactName, Phone, WorkplaceCount, Status) VALUES ('2026-01-01', 'Imported', '+79990000000', 1, 'New')");
        await DatabaseSchema.EnsureCurrentAsync(db);
        await DatabaseSchema.EnsureCurrentAsync(db);
        var item = await db.ConnectionRequests.SingleAsync();
        Assert.Equal("Imported", item.ContactName);
        Assert.Null(item.ConsentVersion);
        Assert.Null(item.ConsentReceivedAtUtc);
        Assert.Null(item.ProcessingLegalBasis);
    }

    [Fact]
    public void Revision_1_requires_explicit_approval_and_exact_bundled_documents()
    {
        var options = new ConnectionRequestPrivacyOptions { Enabled = true };
        Assert.False(options.IsReady);
        options.ApprovedDocumentVersion = "1.0";
        Assert.True(options.IsReady);
        options.LegalBasis = "PreContract";
        Assert.False(options.IsReady);
        options.LegalBasis = "Consent";
        options.ConsentText += "tampered";
        Assert.False(options.IsReady);
        options.ConsentText = ConnectionRequestDocuments.ConsentText;
        options.PrivacyPolicyVersion = "old";
        Assert.False(options.IsReady);
    }

    [Fact]
    public async Task Missing_approval_blocks_intake_even_when_old_enabled_flag_is_true()
    {
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.PostConfigure<ConnectionRequestPrivacyOptions>(options => options.ApprovedDocumentVersion = "")));
        using var client = host.CreateClient();
        var policy = await client.GetFromJsonAsync<JsonElement>("/api/connection-requests/policy");
        Assert.False(policy.GetProperty("available").GetBoolean());
        var response = await client.PostAsJsonAsync("/api/connection-requests", new {
            contactName = "Synthetic", phone = "+79990000000", city = "Test", workplaceCount = 1,
            privacyPolicyVersion = "1.0", consent = true, consentVersion = "1.0"
        });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Client_cannot_replace_the_exact_consent_text_stored_as_evidence()
    {
        using var host = ConsentHost();
        using var client = host.CreateClient();
        var response = await client.PostAsJsonAsync("/api/connection-requests", new {
            contactName = "Synthetic", phone = "+79990000000", city = "Test", workplaceCount = 1,
            privacyPolicyVersion = "1.0", consent = true, consentVersion = "1.0", consentText = "forged"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        await using var scope = factory.Services.CreateAsyncScope();
        int id = result.GetProperty("requestId").GetInt32();
        var item = await scope.ServiceProvider.GetRequiredService<HonestDbContext>().ConnectionRequests.SingleAsync(x => x.Id == id);
        Assert.Equal(ConnectionRequestDocuments.ConsentText, item.ConsentText);
        Assert.Equal("1.0", item.ConsentVersion);
        Assert.NotNull(item.ConsentReceivedAtUtc);
    }

    private sealed class RemoteAddressFilter(string address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(address);
                await continuation();
            });
            next(app);
        };
    }
}
