using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HonestLicenseServer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HonestLicenseServer.IntegrationTests;

[Collection(IntegrationTestCollection.Name)]
public sealed class ConnectionRequestAdminTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private HttpClient Admin()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Key", ApiFactory.AdminKey);
        return client;
    }

    private async Task<int> SubmitAsync()
    {
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/connection-requests", new
        {
            contactName = "Synthetic contact", company = "Synthetic company", phone = "+7 999 000-00-00",
            city = "Test city", workplaceCount = 3, comment = "Модели ККТ: АТОЛ 30Ф",
            privacyPolicyVersion = "1.0", consent = true, consentVersion = "1.0", source = "honestflow.ru"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("requestId").GetInt32();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-key")]
    public async Task Connection_requests_require_admin_key(string? key)
    {
        int id = await SubmitAsync();
        using var client = factory.CreateClient();
        if (key is not null) client.DefaultRequestHeaders.Add("X-Admin-Key", key);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/connection-requests")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync($"/api/admin/connection-requests/{id}/status",
            new { status = "Completed", expectedStatus = "New" })).StatusCode);
        using var admin = Admin();
        var body = await admin.GetFromJsonAsync<JsonElement>("/api/admin/connection-requests?status=New");
        Assert.Contains(body.EnumerateArray(), x => x.GetProperty("id").GetInt32() == id);
    }

    [Fact]
    public async Task Website_submission_is_visible_with_details_without_technical_private_fields()
    {
        int id = await SubmitAsync();
        using var admin = Admin();
        var response = await admin.GetAsync("/api/admin/connection-requests?status=New");
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var item = body.EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == id);
        Assert.Equal("Synthetic contact", item.GetProperty("contactName").GetString());
        Assert.Equal("Synthetic company", item.GetProperty("company").GetString());
        Assert.Equal("Модели ККТ: АТОЛ 30Ф", item.GetProperty("comment").GetString());
        Assert.Equal(3, item.GetProperty("workplaceCount").GetInt32());
        foreach (string field in new[] { "ipAddress", "userAgent", "consentText", "notificationError" })
            Assert.False(item.TryGetProperty(field, out _));
    }

    [Fact]
    public async Task Status_changes_survive_reload_and_are_audited_without_changing_original_data()
    {
        int id = await SubmitAsync();
        using var admin = Admin();
        string previous = "New";
        foreach (string next in new[] { "InProgress", "Completed", "New", "Rejected" })
        {
            var response = await admin.PutAsJsonAsync($"/api/admin/connection-requests/{id}/status",
                new { status = next, expectedStatus = previous });
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            var body = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/connection-requests?status={next}");
            var item = body.EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == id);
            Assert.Equal(next, item.GetProperty("status").GetString());
            previous = next;
        }
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HonestDbContext>();
        var saved = await db.ConnectionRequests.SingleAsync(x => x.Id == id);
        Assert.Equal("Synthetic contact", saved.ContactName);
        Assert.Equal("1.0", saved.PrivacyPolicyVersion);
        Assert.Null(saved.NotificationSentAtUtc);
        Assert.Null(saved.NotificationError);
        var audit = await db.AuditEvents.Where(x => x.EntityType == "ConnectionRequest" && x.EntityId == id.ToString()).ToListAsync();
        Assert.Equal(4, audit.Count);
        Assert.All(audit, x => Assert.Equal("ConnectionRequest.StatusChanged", x.Action));
        Assert.Contains(audit, x => x.DetailsJson != null && x.DetailsJson.Contains("InProgress") && x.DetailsJson.Contains("Completed"));
    }

    [Fact]
    public async Task Stale_status_update_does_not_overwrite_another_operator_or_add_an_audit_event()
    {
        int id = await SubmitAsync();
        using var admin = Admin();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/admin/connection-requests/{id}/status",
            new { status = "InProgress", expectedStatus = "New" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"/api/admin/connection-requests/{id}/status",
            new { status = "Rejected", expectedStatus = "New" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/admin/connection-requests/{id}/status",
            new { status = "InProgress", expectedStatus = "InProgress" })).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HonestDbContext>();
        Assert.Equal("InProgress", (await db.ConnectionRequests.SingleAsync(x => x.Id == id)).Status);
        Assert.Equal(1, await db.AuditEvents.CountAsync(x => x.EntityType == "ConnectionRequest" && x.EntityId == id.ToString()));
    }

    [Theory]
    [InlineData("Deleted", "New")]
    [InlineData("Completed", "")]
    [InlineData("Completed", "Unknown")]
    public async Task Invalid_status_is_rejected_without_changing_the_request(string status, string expectedStatus)
    {
        int id = await SubmitAsync();
        using var admin = Admin();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/admin/connection-requests/{id}/status",
            new { status, expectedStatus })).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.Equal("New", (await scope.ServiceProvider.GetRequiredService<HonestDbContext>()
            .ConnectionRequests.SingleAsync(x => x.Id == id)).Status);
    }

    [Fact]
    public async Task Missing_request_and_unknown_filter_return_explicit_errors()
    {
        using var admin = Admin();
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync("/api/admin/connection-requests/2147483647/status",
            new { status = "Completed", expectedStatus = "New" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/admin/connection-requests?status=Unknown")).StatusCode);
    }
}
