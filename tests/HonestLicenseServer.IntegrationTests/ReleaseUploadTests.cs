using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using HonestLicenseServer.Data;
using HonestLicenseServer.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HonestLicenseServer.IntegrationTests;

[Collection(IntegrationTestCollection.Name)]
public sealed class ReleaseUploadTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Signed_release_is_verified_uploaded_and_served_from_object_storage()
    {
        byte[] binary = "signed honestflow release"u8.ToArray();
        byte[] manifest = Manifest("HonestFlow", "3.1.0", "HonestFlow.exe", binary);
        using HttpClient admin = AdminClient();

        HttpResponseMessage create = await admin.PostAsJsonAsync("/api/admin/release-uploads", new
        {
            keyId = "integration-release",
            manifestBase64 = Convert.ToBase64String(manifest),
            signatureBase64 = Convert.ToBase64String(factory.SignRelease(manifest))
        });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        JsonElement created = await create.Content.ReadFromJsonAsync<JsonElement>();
        int id = created.GetProperty("id").GetInt32();
        Assert.StartsWith("https://storage.test/releases/", created.GetProperty("uploadUrl").GetString());

        factory.ReleaseStorage.UploadedBytes = binary;
        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.PostAsync($"/api/admin/release-uploads/{id}/complete", null)).StatusCode);
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        HonestDbContext db = scope.ServiceProvider.GetRequiredService<HonestDbContext>();
        var asset = await db.ComponentAssets.SingleAsync(x => x.Component == "HonestFlow" && x.Version == "3.1.0");
        Assert.Equal("integration-release", asset.ReleaseKeyId);
        Assert.Equal(Convert.ToBase64String(manifest), asset.ManifestBase64);
        var currentVersion = await db.AppVersions.SingleAsync(x => x.Application == "HonestFlow");
        string previousVersion = currentVersion.CurrentVersion;
        try
        {
            currentVersion.CurrentVersion = "3.1.0";
            await db.SaveChangesAsync();
            using HttpClient client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });
            JsonElement current = await client.GetFromJsonAsync<JsonElement>(
                "/api/updates/honestflow/current?architecture=x64");
            Assert.Equal("integration-release", current.GetProperty("releaseKeyId").GetString());
            Assert.Equal(Convert.ToBase64String(manifest), current.GetProperty("manifestBase64").GetString());
            HttpResponseMessage download = await client.GetAsync(
                "/api/updates/honestflow/3.1.0/download?architecture=x64");
            Assert.Equal(HttpStatusCode.Redirect, download.StatusCode);
            Assert.StartsWith("https://storage.test/releases/", download.Headers.Location?.ToString());
        }
        finally
        {
            currentVersion.CurrentVersion = previousVersion;
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Invalid_signature_is_rejected_before_upload_url_is_created()
    {
        byte[] binary = "invalid signature release"u8.ToArray();
        byte[] manifest = Manifest("HonestFlow", "3.2.0", "HonestFlow.exe", binary);
        using HttpClient admin = AdminClient();

        HttpResponseMessage response = await admin.PostAsJsonAsync("/api/admin/release-uploads", new
        {
            keyId = "integration-release",
            manifestBase64 = Convert.ToBase64String(manifest),
            signatureBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64))
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Object_with_wrong_size_is_deleted_and_not_published()
    {
        byte[] expected = "expected bytes"u8.ToArray();
        byte[] manifest = Manifest("ESM", "9.9.1", "esm.msi", expected);
        using HttpClient admin = AdminClient();
        JsonElement created = await (await admin.PostAsJsonAsync("/api/admin/release-uploads", new
        {
            keyId = "integration-release",
            manifestBase64 = Convert.ToBase64String(manifest),
            signatureBase64 = Convert.ToBase64String(factory.SignRelease(manifest))
        })).Content.ReadFromJsonAsync<JsonElement>();
        int id = created.GetProperty("id").GetInt32();
        factory.ReleaseStorage.UploadedBytes = "wrong-size"u8.ToArray();

        HttpResponseMessage complete = await admin.PostAsync($"/api/admin/release-uploads/{id}/complete", null);

        Assert.Equal(HttpStatusCode.Conflict, complete.StatusCode);
        Assert.Contains(factory.ReleaseStorage.LastObjectKey!, factory.ReleaseStorage.DeletedObjectKeys);
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        HonestDbContext db = scope.ServiceProvider.GetRequiredService<HonestDbContext>();
        Assert.False(await db.ComponentAssets.AnyAsync(x => x.Component == "ESM" && x.Version == "9.9.1"));
    }

    [Fact]
    public async Task Multipart_release_is_uploaded_by_parts_and_server_checks_metadata_only()
    {
        byte[] binary = new byte[9 * 1024 * 1024];
        RandomNumberGenerator.Fill(binary);
        byte[] manifest = Manifest("Controller", "9.9.2", "controller.exe", binary, "any");
        using HttpClient admin = AdminClient();

        HttpResponseMessage create = await admin.PostAsJsonAsync("/api/admin/release-uploads", new
        {
            keyId = "integration-release",
            manifestBase64 = Convert.ToBase64String(manifest),
            signatureBase64 = Convert.ToBase64String(factory.SignRelease(manifest)),
            useMultipart = true
        });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        JsonElement created = await create.Content.ReadFromJsonAsync<JsonElement>();
        int id = created.GetProperty("id").GetInt32();
        Assert.Equal(JsonValueKind.Null, created.GetProperty("uploadUrl").ValueKind);
        Assert.Equal(8 * 1024 * 1024, created.GetProperty("partSizeBytes").GetInt64());
        Assert.Equal(2, created.GetProperty("partCount").GetInt32());

        JsonElement part1 = await (await admin.PostAsync(
            $"/api/admin/release-uploads/{id}/parts/1", null)).Content.ReadFromJsonAsync<JsonElement>();
        JsonElement part2 = await (await admin.PostAsync(
            $"/api/admin/release-uploads/{id}/parts/2", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("partNumber=1", part1.GetProperty("uploadUrl").GetString());
        Assert.Contains("partNumber=2", part2.GetProperty("uploadUrl").GetString());

        factory.ReleaseStorage.UploadedBytes = binary;
        HttpResponseMessage complete = await admin.PostAsJsonAsync(
            $"/api/admin/release-uploads/{id}/complete", new
            {
                parts = new[]
                {
                    new { partNumber = 1, eTag = "etag-1" },
                    new { partNumber = 2, eTag = "etag-2" }
                }
            });

        Assert.Equal(HttpStatusCode.NoContent, complete.StatusCode);
        Assert.Equal([1, 2], factory.ReleaseStorage.CompletedParts.Select(x => x.PartNumber));
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        HonestDbContext db = scope.ServiceProvider.GetRequiredService<HonestDbContext>();
        ComponentAsset asset = await db.ComponentAssets.SingleAsync(
            x => x.Component == "Controller" && x.Version == "9.9.2" && x.Architecture == "any");
        Assert.Equal(binary.LongLength, asset.SizeBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(binary)).ToLowerInvariant(), asset.Sha256);
    }

    [Fact]
    public async Task Published_installer_is_available_through_stable_public_redirect()
    {
        byte[] installer = "signed honestflow web setup"u8.ToArray();
        byte[] manifest = Manifest(
            "HonestFlowInstaller", "3.0.0", "HonestFlow-Web-Setup-3.0.0.exe", installer);
        using HttpClient admin = AdminClient();
        JsonElement created = await (await admin.PostAsJsonAsync("/api/admin/release-uploads", new
        {
            keyId = "integration-release",
            manifestBase64 = Convert.ToBase64String(manifest),
            signatureBase64 = Convert.ToBase64String(factory.SignRelease(manifest))
        })).Content.ReadFromJsonAsync<JsonElement>();
        factory.ReleaseStorage.UploadedBytes = installer;
        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.PostAsync($"/api/admin/release-uploads/{created.GetProperty("id").GetInt32()}/complete", null))
            .StatusCode);

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            HonestDbContext db = scope.ServiceProvider.GetRequiredService<HonestDbContext>();
            db.AppVersions.Add(new HonestLicenseServer.Models.AppVersion
            {
                Application = "HonestFlowInstaller",
                CurrentVersion = "3.0.0",
                ImportedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        using HttpClient publicClient = factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        HttpResponseMessage response = await publicClient.GetAsync("/api/downloads/honestflow/setup");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("https://storage.test/releases/", response.Headers.Location?.ToString());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Signed_release_replaces_matching_legacy_asset()
    {
        byte[] binary = "signed lm release"u8.ToArray();
        byte[] manifest = Manifest("LmModule", "2.6.0-10", "regime-2.6.0-10.msi", binary, "any");
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            HonestDbContext db = scope.ServiceProvider.GetRequiredService<HonestDbContext>();
            db.ComponentAssets.Add(new ComponentAsset
            {
                Component = "LmModule",
                Version = "2.6.0-10",
                Architecture = "any",
                FileName = "legacy.msi",
                YandexPublicKey = "legacy-public-key",
                YandexPath = "/legacy.msi",
                UpdatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
        using HttpClient admin = AdminClient();

        JsonElement created = await (await admin.PostAsJsonAsync("/api/admin/release-uploads", new
        {
            keyId = "integration-release",
            manifestBase64 = Convert.ToBase64String(manifest),
            signatureBase64 = Convert.ToBase64String(factory.SignRelease(manifest))
        })).Content.ReadFromJsonAsync<JsonElement>();
        factory.ReleaseStorage.UploadedBytes = binary;

        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.PostAsync($"/api/admin/release-uploads/{created.GetProperty("id").GetInt32()}/complete", null))
            .StatusCode);
        await using AsyncServiceScope verificationScope = factory.Services.CreateAsyncScope();
        HonestDbContext verificationDb = verificationScope.ServiceProvider.GetRequiredService<HonestDbContext>();
        ComponentAsset asset = await verificationDb.ComponentAssets.SingleAsync(
            x => x.Component == "LmModule" && x.Version == "2.6.0-10" && x.Architecture == "any");
        Assert.Equal("regime-2.6.0-10.msi", asset.FileName);
        Assert.NotNull(asset.ObjectKey);
        Assert.Equal("integration-release", asset.ReleaseKeyId);
        Assert.Null(asset.DownloadUrl);
        Assert.Null(asset.YandexPublicKey);
        Assert.Null(asset.YandexPath);
    }

    [Fact]
    public async Task Existing_signed_release_cannot_be_overwritten()
    {
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            HonestDbContext db = scope.ServiceProvider.GetRequiredService<HonestDbContext>();
            db.ComponentAssets.Add(new ComponentAsset
            {
                Component = "Controller",
                Version = "7.7.7",
                Architecture = "x64",
                FileName = "controller.exe",
                ObjectKey = "releases/existing/controller.exe",
                ReleaseKeyId = "integration-release",
                ManifestBase64 = "existing",
                ReleaseSignatureBase64 = "existing",
                UpdatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
        byte[] binary = "replacement"u8.ToArray();
        byte[] manifest = Manifest("Controller", "7.7.7", "controller.exe", binary);
        using HttpClient admin = AdminClient();

        HttpResponseMessage response = await admin.PostAsJsonAsync("/api/admin/release-uploads", new
        {
            keyId = "integration-release",
            manifestBase64 = Convert.ToBase64String(manifest),
            signatureBase64 = Convert.ToBase64String(factory.SignRelease(manifest))
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    private HttpClient AdminClient()
    {
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Key", ApiFactory.AdminKey);
        return client;
    }

    private static byte[] Manifest(string component, string version, string fileName, byte[] bytes,
        string architecture = "x64") =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            component,
            version,
            fileName,
            architecture,
            sizeBytes = bytes.LongLength,
            sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
        });
}
