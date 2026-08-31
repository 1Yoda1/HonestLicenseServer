using System.Net;
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
public sealed class PublicHonestFlowUpdateTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Anonymous_current_returns_published_honestflow_asset_only()
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/updates/honestflow/current?architecture=x64");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("HonestFlow", body.GetProperty("application").GetString());
        Assert.Equal("2.6.2.0", body.GetProperty("version").GetString());
        Assert.Equal(100, body.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(new string('a', 64), body.GetProperty("sha256").GetString());
        Assert.Contains("/api/updates/honestflow/2.6.2.0/download", body.GetProperty("downloadUrl").GetString());
    }

    [Fact]
    public async Task Anonymous_download_redirects_only_current_published_honestflow_asset()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.GetAsync("/api/updates/honestflow/2.6.2.0/download?architecture=x64");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("https://example.test/global", response.Headers.Location?.ToString());
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync("/api/updates/honestflow/2.5.0/download")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/assets/HonestFlow/2.6.2.0/download")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync("/api/updates/esm/current")).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    public async Task Incomplete_trusted_metadata_is_not_published(long? sizeBytes)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HonestDbContext>();
        var asset = await db.ComponentAssets.SingleAsync(x => x.Component == "HonestFlow" && x.Version == "2.6.2.0");
        string? sha = asset.Sha256;
        long? size = asset.SizeBytes;
        try
        {
            asset.SizeBytes = sizeBytes;
            if (sizeBytes is null) asset.Sha256 = null;
            await db.SaveChangesAsync();
            using var client = factory.CreateClient();
            Assert.Equal(HttpStatusCode.NotFound,
                (await client.GetAsync("/api/updates/honestflow/current")).StatusCode);
        }
        finally
        {
            asset.Sha256 = sha;
            asset.SizeBytes = size;
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Missing_sha256_is_not_published()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HonestDbContext>();
        var asset = await db.ComponentAssets.SingleAsync(x => x.Component == "HonestFlow" && x.Version == "2.6.2.0");
        string? sha = asset.Sha256;
        try
        {
            asset.Sha256 = null;
            await db.SaveChangesAsync();
            using var client = factory.CreateClient();
            Assert.Equal(HttpStatusCode.NotFound,
                (await client.GetAsync("/api/updates/honestflow/current")).StatusCode);
        }
        finally
        {
            asset.Sha256 = sha;
            await db.SaveChangesAsync();
        }
    }
}
