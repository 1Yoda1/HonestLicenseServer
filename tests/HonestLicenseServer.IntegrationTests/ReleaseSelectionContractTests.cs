using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HonestLicenseServer.Data;
using HonestLicenseServer.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HonestLicenseServer.IntegrationTests;

[Collection(IntegrationTestCollection.Name)]
public sealed class ReleaseSelectionContractTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Global_version_requires_a_published_asset()
    {
        string component = "MissingAsset" + Guid.NewGuid().ToString("N");
        using HttpClient admin = AdminClient();

        HttpResponseMessage response = await admin.PutAsJsonAsync(
            $"/api/admin/versions/{component}", new { currentVersion = "1.0.0" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("component_asset_not_found", await response.Content.ReadAsStringAsync());
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        HonestDbContext db = scope.ServiceProvider.GetRequiredService<HonestDbContext>();
        Assert.False(await db.AppVersions.AnyAsync(x => x.Application == component));
    }

    [Fact]
    public async Task Client_override_requires_an_asset_matching_the_client_architecture()
    {
        string version = "x86-only-" + Guid.NewGuid().ToString("N");
        int assetId;
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            HonestDbContext db = scope.ServiceProvider.GetRequiredService<HonestDbContext>();
            var asset = new ComponentAsset
            {
                Component = "ESM",
                Version = version,
                Architecture = "x86",
                FileName = "esm-x86.exe",
                DownloadUrl = "https://example.test/esm-x86.exe",
                UpdatedAtUtc = DateTime.UtcNow
            };
            db.ComponentAssets.Add(asset);
            await db.SaveChangesAsync();
            assetId = asset.Id;
        }

        try
        {
            using HttpClient admin = AdminClient();
            HttpResponseMessage response = await admin.PutAsJsonAsync(
                "/api/admin/clients/integration-client/component-versions/ESM",
                new { requiredVersion = version });

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Contains("component_asset_not_found", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            await using AsyncServiceScope cleanupScope = factory.Services.CreateAsyncScope();
            HonestDbContext cleanupDb = cleanupScope.ServiceProvider.GetRequiredService<HonestDbContext>();
            await cleanupDb.ComponentAssets.Where(x => x.Id == assetId).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Configuration_prefers_exact_architecture_over_any_for_effective_version()
    {
        int assetId;
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            HonestDbContext db = scope.ServiceProvider.GetRequiredService<HonestDbContext>();
            var asset = new ComponentAsset
            {
                Component = "HonestFlow",
                Version = "2.5.0",
                Architecture = "x64",
                FileName = "HonestFlow-2.5.0-x64.zip",
                DownloadUrl = "https://example.test/HonestFlow-2.5.0-x64.zip",
                Sha256 = new string('a', 64),
                SizeBytes = 123,
                UpdatedAtUtc = DateTime.UtcNow
            };
            db.ComponentAssets.Add(asset);
            await db.SaveChangesAsync();
            assetId = asset.Id;
        }

        try
        {
            using HttpClient client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiFactory.ActiveAccessToken);
            JsonElement configuration = await client.GetFromJsonAsync<JsonElement>(
                "/api/configuration/current");
            JsonElement component = configuration.GetProperty("components").EnumerateArray()
                .Single(x => x.GetProperty("component").GetString() == "HonestFlow");

            Assert.Equal("2.5.0", component.GetProperty("effectiveVersion").GetString());
            Assert.Equal("x64", component.GetProperty("architecture").GetString());
            Assert.Equal("HonestFlow-2.5.0-x64.zip", component.GetProperty("fileName").GetString());
        }
        finally
        {
            await using AsyncServiceScope cleanupScope = factory.Services.CreateAsyncScope();
            HonestDbContext cleanupDb = cleanupScope.ServiceProvider.GetRequiredService<HonestDbContext>();
            await cleanupDb.ComponentAssets.Where(x => x.Id == assetId).ExecuteDeleteAsync();
        }
    }

    private HttpClient AdminClient()
    {
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Key", ApiFactory.AdminKey);
        return client;
    }
}
