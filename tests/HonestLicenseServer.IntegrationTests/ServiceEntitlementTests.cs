using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HonestLicenseServer.Data;
using HonestLicenseServer.Infrastructure;
using HonestLicenseServer.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HonestLicenseServer.IntegrationTests;

[Collection(IntegrationTestCollection.Name)]
public sealed class ServiceEntitlementTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Active_service_grant_allows_support_and_generic_asset_download()
    {
        var fixture = await CreateFixtureAsync();
        await PublishGrantAsync(fixture, features: ["Service"]);

        using var client = AuthenticatedClient(fixture.AccessToken, allowAutoRedirect: false);
        var support = await client.PostAsJsonAsync("/api/support/requests", new
        {
            subject = "Service entitlement test",
            message = "The Service grant permits this support request.",
            contact = "integration@example.test"
        });
        var download = await client.GetAsync("/api/assets/HonestFlow/2.5.0/download");

        Assert.Equal(HttpStatusCode.Accepted, support.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, download.StatusCode);
        Assert.Equal("https://example.test/override", download.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Diagnostic_archive_keeps_history_after_download_and_delete()
    {
        var fixture = await CreateFixtureAsync();
        await PublishGrantAsync(fixture, features: ["Service"]);
        factory.DiagnosticStorage.UploadedSizeBytes = 4;

        using var client = AuthenticatedClient(fixture.AccessToken);
        HttpResponseMessage createResponse = await client.PostAsJsonAsync("/api/support/diagnostics/uploads", new
        {
            fileName = "diagnostics.zip",
            sizeBytes = 4,
            sha256 = new string('A', 64),
            honestFlowVersion = "3.0.0"
        });
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        JsonElement created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        int id = created.GetProperty("id").GetInt32();

        HttpResponseMessage completeResponse = await client.PostAsJsonAsync(
            $"/api/support/diagnostics/{id}/complete",
            new { sizeBytes = 4, sha256 = new string('A', 64) });
        Assert.Equal(HttpStatusCode.NoContent, completeResponse.StatusCode);

        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", ApiFactory.AdminKey);
        JsonElement list = await admin.GetFromJsonAsync<JsonElement>("/api/admin/diagnostics");
        JsonElement archive = list.EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == id);
        Assert.Equal("Service test address", archive.GetProperty("address").GetString());
        Assert.Equal("Available", archive.GetProperty("status").GetString());

        HttpResponseMessage download = await admin.PostAsync($"/api/admin/diagnostics/{id}/download", null);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Contains("download=1", await download.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.DeleteAsync($"/api/admin/diagnostics/{id}")).StatusCode);
        list = await admin.GetFromJsonAsync<JsonElement>("/api/admin/diagnostics");
        archive = list.EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == id);
        Assert.Equal("Deleted", archive.GetProperty("status").GetString());
        Assert.NotNull(archive.GetProperty("deletedAtUtc").GetString());
        Assert.Contains(factory.DiagnosticStorage.LastObjectKey!, factory.DiagnosticStorage.DeletedObjectKeys);
    }

    [Fact]
    public async Task Active_device_without_license_is_denied_with_service_required()
    {
        var fixture = await CreateFixtureAsync();

        using var client = AuthenticatedClient(fixture.AccessToken);
        var response = await CreateSupportRequestAsync(client);

        await AssertServiceRequiredAsync(response);
    }

    [Fact]
    public async Task Legacy_or_non_service_features_do_not_grant_service()
    {
        var legacy = await CreateFixtureAsync();
        var legacyGrantBytes = CreateGrantBytes(legacy, 1, ["Service"]);
        await AddVerifiedGrantAsync(legacy, legacyGrantBytes, signatureScope: "LegacySnapshot");

        using (var client = AuthenticatedClient(legacy.AccessToken))
            await AssertServiceRequiredAsync(await CreateSupportRequestAsync(client));

        var fixture = await CreateFixtureAsync();
        await PublishGrantAsync(fixture, features: ["Legacy", "Operator"]);
        using var noServiceClient = AuthenticatedClient(fixture.AccessToken);
        await AssertServiceRequiredAsync(await CreateSupportRequestAsync(noServiceClient));
    }

    [Fact]
    public async Task Latest_revoked_or_expired_grant_never_falls_back_to_previous_service_grant()
    {
        var revoked = await CreateFixtureAsync();
        await PublishGrantAsync(revoked, revision: 1, features: ["Service"]);
        var revokedLicenseId = await PublishGrantAsync(revoked, revision: 2, features: ["Service"]);
        await SetLicenseStatusAsync(revokedLicenseId, "Revoked");

        using (var client = AuthenticatedClient(revoked.AccessToken))
            await AssertServiceRequiredAsync(await CreateSupportRequestAsync(client));

        var expired = await CreateFixtureAsync();
        await PublishGrantAsync(expired, revision: 1, features: ["Service"]);
        await PublishGrantAsync(expired, revision: 2, features: ["Service"],
            validUntilUtc: DateTime.UtcNow.AddMinutes(-1));

        using var expiredClient = AuthenticatedClient(expired.AccessToken);
        await AssertServiceRequiredAsync(await CreateSupportRequestAsync(expiredClient));
    }

    [Fact]
    public async Task Signed_grant_bound_to_another_client_or_device_is_denied()
    {
        var fixture = await CreateFixtureAsync();
        var grantBytes = CreateGrantBytes(fixture, 1, ["Service"],
            clientId: fixture.ExternalClientId + "-other");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HonestDbContext>();
            db.Licenses.Add(new License
            {
                ClientId = fixture.ClientId,
                DeviceId = fixture.DeviceId,
                Revision = 1,
                GrantJson = Encoding.UTF8.GetString(grantBytes),
                GrantBytes = grantBytes,
                SignatureBase64 = Convert.ToBase64String(factory.Sign(grantBytes)),
                KeyId = "integration-key",
                SignatureScope = "PersonalGrant",
                SignatureVerifiedAtUtc = DateTime.UtcNow,
                Status = "Active",
                IssuedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                ValidUntilUtc = DateTime.UtcNow.AddDays(1),
                PublishedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        using var client = AuthenticatedClient(fixture.AccessToken);
        await AssertServiceRequiredAsync(await CreateSupportRequestAsync(client));

        var wrongDevice = await CreateFixtureAsync();
        var wrongDeviceGrantBytes = CreateGrantBytes(wrongDevice, 1, ["Service"],
            deviceId: wrongDevice.ExternalDeviceId + "-other");
        await AddVerifiedGrantAsync(wrongDevice, wrongDeviceGrantBytes);

        using var wrongDeviceClient = AuthenticatedClient(wrongDevice.AccessToken);
        await AssertServiceRequiredAsync(await CreateSupportRequestAsync(wrongDeviceClient));
    }

    [Fact]
    public async Task Operator_device_or_disabled_license_policy_is_denied()
    {
        var operatorDevice = await CreateFixtureAsync();
        await PublishGrantAsync(operatorDevice, features: ["Service"], operatorDevice: true);
        using (var client = AuthenticatedClient(operatorDevice.AccessToken))
            await AssertServiceRequiredAsync(await CreateSupportRequestAsync(client));

        var disabledPolicy = await CreateFixtureAsync(policyEnabled: false);
        await PublishGrantAsync(disabledPolicy, features: ["Service"]);
        using var disabledPolicyClient = AuthenticatedClient(disabledPolicy.AccessToken);
        await AssertServiceRequiredAsync(await CreateSupportRequestAsync(disabledPolicyClient));
    }

    [Fact]
    public async Task Signed_grant_requires_enabled_client_and_device_flags()
    {
        var clientDisabled = await CreateFixtureAsync();
        await PublishGrantAsync(clientDisabled, features: ["Service"], clientEnabled: false);
        using (var client = AuthenticatedClient(clientDisabled.AccessToken))
            await AssertServiceRequiredAsync(await CreateSupportRequestAsync(client));

        var deviceDisabled = await CreateFixtureAsync();
        await PublishGrantAsync(deviceDisabled, features: ["Service"], deviceEnabled: false);
        using var deviceClient = AuthenticatedClient(deviceDisabled.AccessToken);
        await AssertServiceRequiredAsync(await CreateSupportRequestAsync(deviceClient));
    }

    [Fact]
    public async Task Pending_device_is_reported_as_pending_not_service_required()
    {
        using var client = AuthenticatedClient(ApiFactory.PendingAccessToken);
        var response = await CreateSupportRequestAsync(client);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("device_pending", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Configuration_and_license_remain_active_device_endpoints_without_service_feature()
    {
        var fixture = await CreateFixtureAsync();
        await PublishGrantAsync(fixture, features: ["Legacy"]);

        using var client = AuthenticatedClient(fixture.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/configuration/current")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/license/current")).StatusCode);
    }

    [Fact]
    public async Task Remote_support_request_stays_single_until_admin_marks_it_completed()
    {
        ServiceFixture fixture = await CreateFixtureAsync();
        await PublishGrantAsync(fixture, features: ["Service"]);
        string credentialId = Guid.NewGuid().ToString("D");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HonestDbContext>();
            db.RemoteAccessCredentials.Add(new RemoteAccessCredential
            {
                DeviceId = fixture.DeviceId,
                ClientId = fixture.ClientId,
                CredentialId = credentialId,
                KeyId = Guid.NewGuid().ToString("D"),
                RuDesktopId = "123456789",
                CiphertextBase64 = Convert.ToBase64String(new byte[384]),
                IsActive = true,
                UpdatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        using var client = AuthenticatedClient(fixture.AccessToken);
        object FirstBody() => new
        {
            credentialId,
            subject = "Remote help",
            message = "The operator described the problem in detail.",
            contact = "Service client",
            honestFlowVersion = "3.0.0"
        };
        JsonElement first = await (await client.PostAsJsonAsync(
            "/api/support/remote-requests", FirstBody())).Content.ReadFromJsonAsync<JsonElement>();
        JsonElement repeated = await (await client.PostAsJsonAsync(
            "/api/support/remote-requests", FirstBody())).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(first.GetProperty("id").GetInt32(), repeated.GetProperty("id").GetInt32());

        JsonElement active = await client.GetFromJsonAsync<JsonElement>("/api/support/remote-access/current");
        Assert.Equal(first.GetProperty("id").GetInt32(), active.GetProperty("activeRequestId").GetInt32());
        Assert.Equal(JsonValueKind.String, active.GetProperty("activeRequestCreatedAtUtc").ValueKind);

        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", ApiFactory.AdminKey);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsync(
            $"/api/admin/support-requests/{first.GetProperty("id").GetInt32()}/resolve", null)).StatusCode);

        JsonElement completed = await client.GetFromJsonAsync<JsonElement>("/api/support/remote-access/current");
        Assert.Equal(JsonValueKind.Null, completed.GetProperty("activeRequestId").ValueKind);
        JsonElement next = await (await client.PostAsJsonAsync(
            "/api/support/remote-requests", FirstBody())).Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(first.GetProperty("id").GetInt32(), next.GetProperty("id").GetInt32());
    }

    private async Task<ServiceFixture> CreateFixtureAsync(bool policyEnabled = true)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;
        var fixture = new ServiceFixture(
            "service-client-" + suffix,
            "service-device-" + suffix,
            "service-access-" + suffix);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HonestDbContext>();
        var client = new Client
        {
            ExternalClientId = fixture.ExternalClientId,
            Name = "Service entitlement test client",
            Architecture = "x64",
            IsActive = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        var device = new Device
        {
            Client = client,
            ExternalDeviceId = fixture.ExternalDeviceId,
            Name = "Service entitlement test device",
            Address = "Service test address",
            Status = "Active",
            RegisteredAtUtc = now
        };
        db.AddRange(client, device);
        await db.SaveChangesAsync();

        db.LicensePolicies.Add(new LicensePolicy
        {
            ClientId = client.Id,
            IsEnabled = policyEnabled,
            OfflineGraceHours = 0,
            SourceRevision = 1,
            SourceIssuedAtUtc = now,
            SourceValidUntilUtc = now.AddDays(1)
        });
        db.RefreshTokens.Add(new RefreshToken
        {
            ClientId = client.Id,
            DeviceId = device.Id,
            RequestedExternalDeviceId = fixture.ExternalDeviceId,
            AccessTokenHash = TokenHelper.Hash(fixture.AccessToken),
            AccessTokenExpiresAtUtc = now.AddHours(1),
            TokenHash = TokenHelper.Hash("refresh-" + fixture.AccessToken),
            TokenFamilyId = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddDays(1)
        });
        await db.SaveChangesAsync();

        return fixture with { ClientId = client.Id, DeviceId = device.Id };
    }

    private async Task<int> PublishGrantAsync(
        ServiceFixture fixture,
        long revision = 1,
        string[]? features = null,
        DateTime? validUntilUtc = null,
        bool operatorDevice = false,
        bool clientEnabled = true,
        bool deviceEnabled = true)
    {
        var grantBytes = CreateGrantBytes(fixture, revision, features, validUntilUtc,
            operatorDevice, clientEnabled, deviceEnabled);
        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", ApiFactory.AdminKey);
        var response = await admin.PostAsJsonAsync("/api/admin/licenses", new
        {
            grantBase64 = Convert.ToBase64String(grantBytes),
            signatureBase64 = Convert.ToBase64String(factory.Sign(grantBytes)),
            keyId = "integration-key"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetInt32();
    }

    private static byte[] CreateGrantBytes(
        ServiceFixture fixture,
        long revision,
        string[]? features,
        DateTime? validUntilUtc = null,
        bool operatorDevice = false,
        bool clientEnabled = true,
        bool deviceEnabled = true,
        string? clientId = null,
        string? deviceId = null) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            revision,
            clientId = clientId ?? fixture.ExternalClientId,
            deviceId = deviceId ?? fixture.ExternalDeviceId,
            issuedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            validUntilUtc = validUntilUtc ?? DateTime.UtcNow.AddDays(1),
            clientEnabled,
            deviceEnabled,
            operatorDevice,
            features = features ?? []
        }));

    private async Task SetLicenseStatusAsync(int licenseId, string status)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HonestDbContext>();
        (await db.Licenses.SingleAsync(x => x.Id == licenseId)).Status = status;
        await db.SaveChangesAsync();
    }

    private async Task AddVerifiedGrantAsync(
        ServiceFixture fixture,
        byte[] grantBytes,
        string signatureScope = "PersonalGrant")
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HonestDbContext>();
        var now = DateTime.UtcNow;
        db.Licenses.Add(new License
        {
            ClientId = fixture.ClientId,
            DeviceId = fixture.DeviceId,
            Revision = 1,
            GrantJson = Encoding.UTF8.GetString(grantBytes),
            GrantBytes = grantBytes,
            SignatureBase64 = Convert.ToBase64String(factory.Sign(grantBytes)),
            KeyId = "integration-key",
            SignatureScope = signatureScope,
            SignatureVerifiedAtUtc = now,
            Status = "Active",
            IssuedAtUtc = now.AddMinutes(-1),
            ValidUntilUtc = now.AddDays(1),
            PublishedAtUtc = now
        });
        await db.SaveChangesAsync();
    }

    private HttpClient AuthenticatedClient(string accessToken, bool allowAutoRedirect = true)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = allowAutoRedirect
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    private static Task<HttpResponseMessage> CreateSupportRequestAsync(HttpClient client) =>
        client.PostAsJsonAsync("/api/support/requests", new
        {
            subject = "Service entitlement test",
            message = "The endpoint must require an active Service grant.",
            contact = "integration@example.test"
        });

    private static async Task AssertServiceRequiredAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("service_required", problem.GetProperty("code").GetString());
    }

    private sealed record ServiceFixture(
        string ExternalClientId,
        string ExternalDeviceId,
        string AccessToken,
        int ClientId = 0,
        int DeviceId = 0);
}
