using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HonestLicenseServer.Data;
using HonestLicenseServer.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HonestLicenseServer.IntegrationTests;

[Collection(IntegrationTestCollection.Name)]
public sealed class ServiceInstallationAccessTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Password = "service-password-2026";

    [Fact]
    public async Task Admin_can_enable_and_change_password_without_reading_it_back()
    {
        using HttpClient admin = AdminClient();
        HttpResponseMessage response = await admin.PutAsJsonAsync(
            "/api/admin/service-install-access", new { isEnabled = true, newPassword = Password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("isEnabled").GetBoolean());
        Assert.True(body.GetProperty("hasPassword").GetBoolean());
        Assert.False(body.TryGetProperty("password", out _));
        Assert.False(body.TryGetProperty("passwordHash", out _));

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HonestDbContext>();
        var stored = await db.ServiceInstallationAccess.SingleAsync();
        Assert.NotEqual(Password, stored.PasswordHash);
        Assert.True(PasswordHasher.Verify(Password, stored.PasswordHash));
    }

    [Fact]
    public async Task Correct_password_returns_install_only_token_without_refresh_and_audits_grant()
    {
        await ConfigureAsync(true, Password);
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/service/install-access", new
        {
            password = Password,
            appVersion = "3.0.0",
            architecture = "x64"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("installation_only", body.GetProperty("scope").GetString());
        Assert.StartsWith("hfi_", body.GetProperty("accessToken").GetString());
        Assert.InRange(body.GetProperty("expiresInSeconds").GetInt32(), 1, 1800);
        Assert.False(body.TryGetProperty("refreshToken", out _));
        Assert.False(body.TryGetProperty("deviceId", out _));
        Assert.False(body.TryGetProperty("clientId", out _));

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HonestDbContext>();
        Assert.True(await db.AuditEvents.AnyAsync(x => x.Action == "ServiceInstallAccess.Granted"));
    }

    [Fact]
    public async Task Wrong_password_is_denied_and_audited()
    {
        await ConfigureAsync(true, Password);
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/service/install-access", new { password = "wrong-password", architecture = "x64" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_service_install_password", body.GetProperty("code").GetString());
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HonestDbContext>();
        Assert.True(await db.AuditEvents.AnyAsync(x => x.Action == "ServiceInstallAccess.Denied"));
    }

    [Fact]
    public async Task Disabled_access_is_denied()
    {
        await ConfigureAsync(false, Password);
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/service/install-access", new { password = Password, architecture = "x64" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("service_install_access_disabled", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Install_only_token_is_rejected_by_normal_protected_endpoints()
    {
        await ConfigureAsync(true, Password);
        using HttpClient client = factory.CreateClient();
        HttpResponseMessage authorization = await client.PostAsJsonAsync(
            "/api/service/install-access", new { password = Password, architecture = "x64" });
        JsonElement body = await authorization.Content.ReadFromJsonAsync<JsonElement>();
        string token = body.GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/configuration/current")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/license/current")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/device/registration/current")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = token })).StatusCode);
    }

    [Fact]
    public async Task Install_asset_accepts_only_install_token()
    {
        await ConfigureAsync(true, Password);
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HonestDbContext>();
            if (!await db.AppVersions.AnyAsync(x => x.Application == "ESM"))
            {
                DateTime now = DateTime.UtcNow;
                db.AppVersions.Add(new AppVersion
                {
                    Application = "ESM",
                    CurrentVersion = "3.2.1",
                    ImportedAtUtc = now
                });
                db.ComponentAssets.Add(new ComponentAsset
                {
                    Component = "ESM",
                    Version = "3.2.1",
                    Architecture = "x64",
                    FileName = "esm-3.2.1.exe",
                    DownloadUrl = "https://example.test/esm-3.2.1.exe",
                    UpdatedAtUtc = now
                });
                await db.SaveChangesAsync();
            }
        }

        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        HttpResponseMessage authorization = await client.PostAsJsonAsync(
            "/api/service/install-access",
            new { password = Password, architecture = "x64" });
        JsonElement body = await authorization.Content.ReadFromJsonAsync<JsonElement>();
        string token = body.GetProperty("accessToken").GetString()!;
        JsonElement esm = body.GetProperty("components").EnumerateArray()
            .Single(x => x.GetProperty("component").GetString() == "ESM");
        string downloadUrl = esm.GetProperty("downloadUrl").GetString()!;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage installDownload = await client.GetAsync(downloadUrl);
        Assert.Equal(HttpStatusCode.Redirect, installDownload.StatusCode);
        Assert.Equal("https://example.test/esm-3.2.1.exe", installDownload.Headers.Location?.ToString());

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ApiFactory.ActiveAccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(downloadUrl)).StatusCode);
    }

    [Fact]
    public async Task Disabling_access_revokes_previously_issued_install_tokens()
    {
        await ConfigureAsync(true, Password);
        using HttpClient client = factory.CreateClient();
        HttpResponseMessage authorization = await client.PostAsJsonAsync(
            "/api/service/install-access",
            new { password = Password, architecture = "x64" });
        JsonElement body = await authorization.Content.ReadFromJsonAsync<JsonElement>();
        string token = body.GetProperty("accessToken").GetString()!;

        await ConfigureAsync(false, null);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/assets/install/ESM/3.2.1/download")).StatusCode);
    }

    private HttpClient AdminClient()
    {
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Key", ApiFactory.AdminKey);
        return client;
    }

    private async Task ConfigureAsync(bool enabled, string? password)
    {
        using HttpClient admin = AdminClient();
        HttpResponseMessage response = await admin.PutAsJsonAsync(
            "/api/admin/service-install-access", new { isEnabled = enabled, newPassword = password });
        response.EnsureSuccessStatusCode();
    }
}
