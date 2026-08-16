using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using HonestLicenseServer.Data;
using HonestLicenseServer.Infrastructure;
using HonestLicenseServer.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace HonestLicenseServer.IntegrationTests;

[Collection(IntegrationTestCollection.Name)]
public sealed class ApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Public_connection_request_is_saved_even_when_smtp_is_unavailable()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("HonestFlow-Site-Test/1.0");
        var response = await client.PostAsJsonAsync("/api/connection-requests", new
        {
            contactName = "Иван Иванов", company = "ООО Ромашка",
            phone = "+7 999 000-00-00", email = "ivan@example.ru", city = "Омск",
            workplaceCount = 12, inventorySystem = "1С",
            comment = "Хотим подключить сеть магазинов", website = "",
            source = "honestflow-site"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("success").GetBoolean());
        var id = result.GetProperty("requestId").GetInt32();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HonestDbContext>();
        var saved = await db.ConnectionRequests.SingleAsync(x => x.Id == id);
        Assert.Equal("New", saved.Status);
        Assert.Equal("Иван Иванов", saved.ContactName);
        Assert.Equal(12, saved.WorkplaceCount);
        Assert.Null(saved.NotificationSentAtUtc);
        Assert.Contains("SMTP", saved.NotificationError);
    }

    [Fact]
    public async Task Connection_request_honeypot_returns_success_without_saving()
    {
        int before;
        await using (var scope = factory.Services.CreateAsyncScope())
            before = await scope.ServiceProvider.GetRequiredService<HonestDbContext>()
                .ConnectionRequests.CountAsync();

        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/connection-requests", new
        {
            contactName = "Bot", phone = "123", workplaceCount = 1,
            website = "https://spam.example"
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using var verificationScope = factory.Services.CreateAsyncScope();
        var after = await verificationScope.ServiceProvider.GetRequiredService<HonestDbContext>()
            .ConnectionRequests.CountAsync();
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Invalid_connection_request_returns_public_safe_error()
    {
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/connection-requests", new
        {
            contactName = "", phone = "1", workplaceCount = 0, email = "not-an-email"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal("Проверьте заполненные данные.", result.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Protected_endpoint_without_token_returns_problem_details()
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/license/current");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_access_token", json.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Empty_authentication_request_returns_validation_problem()
    {
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation_failed", json.GetProperty("code").GetString());
        Assert.True(json.GetProperty("errors").TryGetProperty("Password", out _));
        Assert.True(json.GetProperty("errors").TryGetProperty("DeviceId", out _));
    }

    [Fact]
    public async Task Pending_device_can_read_registration_status_but_not_configuration()
    {
        using var client = AuthenticatedClient(ApiFactory.PendingAccessToken);
        var registration = await client.GetAsync("/api/device/registration/current");
        var configuration = await client.GetAsync("/api/configuration/current");

        Assert.True(registration.StatusCode == HttpStatusCode.OK,
            await registration.Content.ReadAsStringAsync());
        var json = await registration.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Pending", json.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, configuration.StatusCode);
    }

    [Fact]
    public async Task Configuration_uses_client_override_and_matching_asset()
    {
        using var client = AuthenticatedClient(ApiFactory.ActiveAccessToken);
        var response = await client.GetAsync("/api/configuration/current");

        Assert.True(response.StatusCode == HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var clientConfiguration = json.GetProperty("client");
        Assert.Equal("7701234567", clientConfiguration.GetProperty("inn").GetString());
        Assert.Equal("integration-password",
            clientConfiguration.GetProperty("identificationCode").GetString());
        Assert.StartsWith("integration-chz-token",
            clientConfiguration.GetProperty("chzToken").GetString());
        var component = json.GetProperty("components").EnumerateArray().Single();
        Assert.Equal("2.6.2.0", component.GetProperty("globalVersion").GetString());
        Assert.Equal("2.5.0", component.GetProperty("effectiveVersion").GetString());
        Assert.Equal("HonestFlow-2.5.0.zip", component.GetProperty("fileName").GetString());
        Assert.Equal("any", component.GetProperty("architecture").GetString());
        Assert.EndsWith("/api/assets/HonestFlow/2.5.0/download",
            component.GetProperty("downloadUrl").GetString());
        Assert.True(component.GetProperty("isOverride").GetBoolean());
    }

    [Fact]
    public async Task Configuration_returns_null_current_client_inn_without_leaking_another_client_inn()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HonestDbContext>();
        var current = await db.Clients.SingleAsync(x => x.ExternalClientId == "integration-client");
        string? originalInn = current.Inn;
        var foreign = new Client
        {
            ExternalClientId = "foreign-inn-" + Guid.NewGuid().ToString("N"),
            Name = "Foreign INN client",
            Inn = "9999999999",
            Architecture = "x64",
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        try
        {
            current.Inn = null;
            db.Clients.Add(foreign);
            await db.SaveChangesAsync();

            using var client = AuthenticatedClient(ApiFactory.ActiveAccessToken);
            var response = await client.GetAsync("/api/configuration/current");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(JsonValueKind.Null,
                json.GetProperty("client").GetProperty("inn").ValueKind);
        }
        finally
        {
            current.Inn = originalInn;
            db.Clients.Remove(foreign);
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Asset_download_redirects_active_device_to_configured_source()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ApiFactory.ActiveAccessToken);
        var response = await client.GetAsync("/api/assets/HonestFlow/2.5.0/download");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("https://example.test/override", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Signed_grant_supports_etag_and_revocation()
    {
        int registrationHistoryId;
        await using (var setupScope = factory.Services.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<HonestDbContext>();
            int clientId = await db.Clients.Where(x => x.ExternalClientId == "integration-client")
                .Select(x => x.Id).SingleAsync();
            var history = new HonestLicenseServer.Models.DeviceRegistrationRequest
            {
                ClientId = clientId,
                ExternalDeviceId = "integration-device",
                RequestedName = "Test Device",
                RequestedAddress = "Test address",
                Status = "Approved",
                RequestedAtUtc = DateTime.UtcNow.AddDays(-2),
                ResolvedAtUtc = DateTime.UtcNow.AddDays(-1)
            };
            db.DeviceRegistrationRequests.Add(history);
            await db.SaveChangesAsync();
            registrationHistoryId = history.Id;
        }

        var grantBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            revision = 1001,
            clientId = "integration-client",
            deviceId = "integration-device",
            issuedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            validUntilUtc = DateTime.UtcNow.AddDays(30)
        }));
        var request = new
        {
            grantBase64 = Convert.ToBase64String(grantBytes),
            signatureBase64 = Convert.ToBase64String(factory.Sign(grantBytes)),
            keyId = "integration-key"
        };

        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", ApiFactory.AdminKey);
        var publish = await admin.PostAsJsonAsync("/api/admin/licenses", request);
        Assert.True(publish.StatusCode == HttpStatusCode.Created,
            await publish.Content.ReadAsStringAsync());
        var published = await publish.Content.ReadFromJsonAsync<JsonElement>();
        var licenseId = published.GetProperty("id").GetInt32();

        using var honestFlow = AuthenticatedClient(ApiFactory.ActiveAccessToken);
        var current = await honestFlow.GetAsync("/api/license/current");
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        var response = await current.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(request.grantBase64, response.GetProperty("grantBase64").GetString());
        Assert.Equal("integration-key", response.GetProperty("keyId").GetString());

        var etag = current.Headers.ETag?.ToString();
        Assert.False(string.IsNullOrWhiteSpace(etag));
        using var notModifiedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/license/current");
        notModifiedRequest.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var notModified = await honestFlow.SendAsync(notModifiedRequest);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);

        var revoke = await admin.PutAsync($"/api/admin/licenses/{licenseId}/revoke", null);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        var repeatedRevoke = await admin.PutAsync($"/api/admin/licenses/{licenseId}/revoke", null);
        Assert.Equal(HttpStatusCode.NoContent, repeatedRevoke.StatusCode);
        var revoked = await honestFlow.GetAsync("/api/license/current");
        Assert.Equal(HttpStatusCode.Gone, revoked.StatusCode);
        var revokedProblem = await revoked.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("license_revoked", revokedProblem.GetProperty("code").GetString());

        await using (var verificationScope = factory.Services.CreateAsyncScope())
        {
            var db = verificationScope.ServiceProvider.GetRequiredService<HonestDbContext>();
            var stored = await db.Licenses.SingleAsync(x => x.Id == licenseId);
            Assert.Equal("Revoked", stored.Status);
            Assert.True(await db.Clients.AnyAsync(x => x.ExternalClientId == "integration-client"));
            Assert.True(await db.Devices.AnyAsync(x => x.ExternalDeviceId == "integration-device"));
            Assert.True(await db.DeviceRegistrationRequests.AnyAsync(x => x.Id == registrationHistoryId));
            Assert.True(await db.AuditEvents.AnyAsync(x =>
                x.Action == "License.Revoked" && x.EntityId == licenseId.ToString()));
        }

        var expiredBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            revision = 1002,
            clientId = "integration-client",
            deviceId = "integration-device",
            issuedAtUtc = DateTime.UtcNow.AddDays(-2),
            validUntilUtc = DateTime.UtcNow.AddDays(-1)
        }));
        var expiredPublish = await admin.PostAsJsonAsync("/api/admin/licenses", new
        {
            grantBase64 = Convert.ToBase64String(expiredBytes),
            signatureBase64 = Convert.ToBase64String(factory.Sign(expiredBytes)),
            keyId = "integration-key"
        });
        Assert.Equal(HttpStatusCode.Created, expiredPublish.StatusCode);
        var expired = await honestFlow.GetAsync("/api/license/current");
        Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
        var expiredProblem = await expired.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("license_expired", expiredProblem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Modified_grant_with_unrelated_signature_is_rejected()
    {
        var original = Encoding.UTF8.GetBytes("{\"revision\":2000}");
        var modified = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            revision = 2001,
            clientId = "integration-client",
            deviceId = "integration-device",
            issuedAtUtc = DateTime.UtcNow,
            validUntilUtc = DateTime.UtcNow.AddDays(30)
        }));
        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", ApiFactory.AdminKey);

        var response = await admin.PostAsJsonAsync("/api/admin/licenses", new
        {
            grantBase64 = Convert.ToBase64String(modified),
            signatureBase64 = Convert.ToBase64String(factory.Sign(original)),
            keyId = "integration-key"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_license_signature", json.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Pending_device_can_create_support_request_and_admin_can_read_it()
    {
        using var honestFlow = AuthenticatedClient(ApiFactory.PendingAccessToken);
        var create = await honestFlow.PostAsJsonAsync("/api/support/requests", new
        {
            subject = "Device approval",
            message = "Please check the pending device request.",
            contact = "integration@example.test",
            honestFlowVersion = "2.6.2.0"
        });
        Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);

        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", ApiFactory.AdminKey);
        var list = await admin.GetAsync("/api/admin/support-requests");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var items = await list.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(items.EnumerateArray(), item =>
            item.GetProperty("subject").GetString() == "Device approval");
    }

    [Fact]
    public async Task Admin_errors_use_problem_details()
    {
        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", "wrong-key");
        var response = await admin.GetAsync("/api/admin/clients");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_admin_key", json.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Login_refresh_rotation_and_logout_work_end_to_end()
    {
        using var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            password = "integration-password", deviceId = "integration-device"
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var loginTokens = await login.Content.ReadFromJsonAsync<JsonElement>();
        var accessToken = loginTokens.GetProperty("accessToken").GetString()!;
        var refreshToken = loginTokens.GetProperty("refreshToken").GetString()!;
        Assert.False(loginTokens.GetProperty("deviceRegistrationRequired").GetBoolean());
        Assert.Equal("integration-client", loginTokens.GetProperty("clientId").GetString());
        Assert.Equal("Integration Client", loginTokens.GetProperty("clientName").GetString());

        var refresh = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken });
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        var refreshedTokens = await refresh.Content.ReadFromJsonAsync<JsonElement>();
        var refreshedAccess = refreshedTokens.GetProperty("accessToken").GetString()!;
        Assert.Equal("integration-client", refreshedTokens.GetProperty("clientId").GetString());
        Assert.Equal("Integration Client", refreshedTokens.GetProperty("clientName").GetString());
        var reused = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", refreshedAccess);
        var logout = await client.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        var afterLogout = await client.GetAsync("/api/configuration/current");
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);

        Assert.NotEqual(accessToken, refreshedAccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Login_ReturnsCurrentLicensePolicyEnabledState(bool enabled)
    {
        await SetLicensePolicyAsync(enabled);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            password = "integration-password", deviceId = "integration-device"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(enabled, body.GetProperty("licensePolicyEnabled").GetBoolean());
    }

    [Fact]
    public async Task Login_WithoutLicensePolicy_ReturnsNullPolicyMetadata()
    {
        await SetLicensePolicyAsync(null);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            password = "integration-password", deviceId = "integration-device"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, body.GetProperty("licensePolicyEnabled").ValueKind);
    }

    [Fact]
    public async Task Refresh_ReturnsUpdatedLicensePolicyRatherThanLoginMetadata()
    {
        await SetLicensePolicyAsync(true);
        using var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            password = "integration-password", deviceId = "integration-device"
        });
        var loginBody = await login.Content.ReadFromJsonAsync<JsonElement>();
        string refreshToken = loginBody.GetProperty("refreshToken").GetString()!;

        await SetLicensePolicyAsync(false);
        var refresh = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken });

        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        var refreshBody = await refresh.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(refreshBody.GetProperty("licensePolicyEnabled").GetBoolean());
    }

    [Fact]
    public async Task Admin_policy_toggle_controls_unknown_device_login_and_preserves_history()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string externalClientId = "policy-client-" + suffix;
        string password = "policy-password-" + suffix;
        int clientDatabaseId;
        int existingSessionId;
        int operatorGrantId;

        await using (var setupScope = factory.Services.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<HonestDbContext>();
            var now = DateTime.UtcNow;
            var client = new Client
            {
                ExternalClientId = externalClientId,
                Name = "Policy Test Client",
                IsActive = true,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            var device = new Device
            {
                Client = client,
                ExternalDeviceId = "known-" + suffix,
                Name = "Known policy device",
                Status = "Active",
                RegisteredAtUtc = now
            };
            db.AddRange(client, device);
            await db.SaveChangesAsync();
            clientDatabaseId = client.Id;

            db.Credentials.Add(new Credential
            {
                ClientId = client.Id,
                Login = "policy-login-" + suffix,
                PasswordHash = PasswordHasher.Hash(password),
                IsActive = true,
                PasswordChangedAtUtc = now
            });
            db.ClientSettings.Add(new ClientSetting
            {
                ClientId = client.Id,
                IdentificationCode = password
            });
            db.LicensePolicies.Add(new LicensePolicy
            {
                ClientId = client.Id,
                IsEnabled = true,
                OfflineGraceHours = 72,
                SourceRevision = 1,
                SourceIssuedAtUtc = now,
                SourceValidUntilUtc = now.AddYears(1)
            });
            db.DeviceRegistrationRequests.Add(new DeviceRegistrationRequest
            {
                ClientId = client.Id,
                ExternalDeviceId = device.ExternalDeviceId,
                RequestedName = device.Name,
                Status = "Approved",
                RequestedAtUtc = now.AddDays(-1),
                ResolvedAtUtc = now
            });
            var existingSession = new RefreshToken
            {
                ClientId = client.Id,
                DeviceId = device.Id,
                RequestedExternalDeviceId = device.ExternalDeviceId,
                AccessTokenHash = TokenHelper.Hash("policy-access-" + suffix),
                AccessTokenExpiresAtUtc = now.AddHours(1),
                TokenHash = TokenHelper.Hash("policy-refresh-" + suffix),
                TokenFamilyId = Guid.NewGuid().ToString(),
                CreatedAtUtc = now,
                ExpiresAtUtc = now.AddDays(1)
            };
            db.RefreshTokens.Add(existingSession);
            byte[] operatorGrantBytes = Encoding.UTF8.GetBytes("{\"operatorDevice\":true}");
            var operatorGrant = new License
            {
                ClientId = client.Id,
                DeviceId = device.Id,
                Revision = now.Ticks,
                GrantJson = Encoding.UTF8.GetString(operatorGrantBytes),
                GrantBytes = operatorGrantBytes,
                SignatureBase64 = "test-history-signature",
                KeyId = "test-history-key",
                SignatureScope = "PersonalGrant",
                Status = "Superseded",
                IssuedAtUtc = now.AddDays(-2),
                ValidUntilUtc = now.AddDays(30),
                PublishedAtUtc = now.AddDays(-2)
            };
            db.Licenses.Add(operatorGrant);
            await db.SaveChangesAsync();
            existingSessionId = existingSession.Id;
            operatorGrantId = operatorGrant.Id;
        }

        try
        {
            using var admin = factory.CreateClient();
            admin.DefaultRequestHeaders.Add("X-Admin-Key", ApiFactory.AdminKey);

            var initial = await admin.GetFromJsonAsync<JsonElement>(
                $"/api/admin/clients/{externalClientId}/license-policy");
            Assert.True(initial.GetProperty("isEnabled").GetBoolean());

            var disable = await admin.PutAsJsonAsync(
                $"/api/admin/clients/{externalClientId}/license-policy",
                new { isEnabled = false });
            Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
            await AssertPolicyAndHistoryAsync(false, expectExistingSessionRevoked: false);

            using var honestFlow = factory.CreateClient();
            var disabledLogin = await honestFlow.PostAsJsonAsync("/api/auth/login", new
            {
                password,
                deviceId = "unknown-disabled-" + suffix
            });
            Assert.Equal(HttpStatusCode.OK, disabledLogin.StatusCode);
            var disabledBody = await disabledLogin.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(disabledBody.GetProperty("deviceRegistrationRequired").GetBoolean());
            Assert.False(disabledBody.GetProperty("licensePolicyEnabled").GetBoolean());

            var enable = await admin.PutAsJsonAsync(
                $"/api/admin/clients/{externalClientId}/license-policy",
                new { isEnabled = true });
            Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
            await AssertPolicyAndHistoryAsync(true, expectExistingSessionRevoked: false);

            var enabledLogin = await honestFlow.PostAsJsonAsync("/api/auth/login", new
            {
                password,
                deviceId = "unknown-enabled-" + suffix
            });
            Assert.Equal(HttpStatusCode.OK, enabledLogin.StatusCode);
            var enabledBody = await enabledLogin.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(enabledBody.GetProperty("deviceRegistrationRequired").GetBoolean());
            Assert.True(enabledBody.GetProperty("licensePolicyEnabled").GetBoolean());

            await using var auditScope = factory.Services.CreateAsyncScope();
            var auditDb = auditScope.ServiceProvider.GetRequiredService<HonestDbContext>();
            Assert.Equal(2, await auditDb.AuditEvents.CountAsync(x =>
                x.ClientId == clientDatabaseId && x.Action == "ClientLicensePolicy.Updated"));
        }
        finally
        {
            await using var cleanupScope = factory.Services.CreateAsyncScope();
            var db = cleanupScope.ServiceProvider.GetRequiredService<HonestDbContext>();
            db.AuditEvents.RemoveRange(db.AuditEvents.Where(x => x.ClientId == clientDatabaseId));
            db.RefreshTokens.RemoveRange(db.RefreshTokens.Where(x => x.ClientId == clientDatabaseId));
            db.Licenses.RemoveRange(db.Licenses.Where(x => x.ClientId == clientDatabaseId));
            db.DeviceRegistrationRequests.RemoveRange(db.DeviceRegistrationRequests.Where(x => x.ClientId == clientDatabaseId));
            db.LicensePolicies.RemoveRange(db.LicensePolicies.Where(x => x.ClientId == clientDatabaseId));
            db.ClientSettings.RemoveRange(db.ClientSettings.Where(x => x.ClientId == clientDatabaseId));
            db.Credentials.RemoveRange(db.Credentials.Where(x => x.ClientId == clientDatabaseId));
            db.Devices.RemoveRange(db.Devices.Where(x => x.ClientId == clientDatabaseId));
            db.Clients.RemoveRange(db.Clients.Where(x => x.Id == clientDatabaseId));
            await db.SaveChangesAsync();
        }

        async Task AssertPolicyAndHistoryAsync(bool expectedPolicy, bool expectExistingSessionRevoked)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HonestDbContext>();
            Assert.Equal(expectedPolicy,
                (await db.LicensePolicies.SingleAsync(x => x.ClientId == clientDatabaseId)).IsEnabled);
            Assert.True(await db.Clients.AnyAsync(x => x.Id == clientDatabaseId));
            Assert.True(await db.ClientSettings.AnyAsync(x => x.ClientId == clientDatabaseId));
            Assert.True(await db.Devices.AnyAsync(x => x.ClientId == clientDatabaseId));
            Assert.True(await db.DeviceRegistrationRequests.AnyAsync(x => x.ClientId == clientDatabaseId));
            Assert.True(await db.Licenses.AnyAsync(x => x.Id == operatorGrantId));
            var session = await db.RefreshTokens.SingleAsync(x => x.Id == existingSessionId);
            Assert.Equal(expectExistingSessionRevoked, session.RevokedAtUtc is not null);
        }
    }

    [Fact]
    public async Task Admin_policy_put_creates_missing_row_and_is_idempotent()
    {
        await SetLicensePolicyAsync(null);
        try
        {
            using var admin = factory.CreateClient();
            admin.DefaultRequestHeaders.Add("X-Admin-Key", ApiFactory.AdminKey);

            var legacy = await admin.GetFromJsonAsync<JsonElement>(
                "/api/admin/clients/integration-client/license-policy");
            Assert.Equal(JsonValueKind.Null, legacy.GetProperty("isEnabled").ValueKind);

            var first = await admin.PutAsJsonAsync(
                "/api/admin/clients/integration-client/license-policy",
                new { isEnabled = false });
            var repeated = await admin.PutAsJsonAsync(
                "/api/admin/clients/integration-client/license-policy",
                new { isEnabled = false });
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);

            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HonestDbContext>();
            int clientId = await db.Clients.Where(x => x.ExternalClientId == "integration-client")
                .Select(x => x.Id).SingleAsync();
            var policy = await db.LicensePolicies.SingleAsync(x => x.ClientId == clientId);
            Assert.False(policy.IsEnabled);
            Assert.Equal(0, policy.SourceRevision);
        }
        finally
        {
            await SetLicensePolicyAsync(true);
        }
    }

    private async Task SetLicensePolicyAsync(bool? enabled)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HonestDbContext>();
        int clientId = await db.Clients.Where(x => x.ExternalClientId == "integration-client")
            .Select(x => x.Id).SingleAsync();
        var policy = await db.LicensePolicies.FindAsync(clientId);
        if (enabled is null)
        {
            if (policy is not null)
                db.LicensePolicies.Remove(policy);
        }
        else if (policy is null)
        {
            db.LicensePolicies.Add(new HonestLicenseServer.Models.LicensePolicy
            {
                ClientId = clientId, IsEnabled = enabled.Value, MinimumHonestFlowVersion = "0.0.0",
                SourceRevision = 1, SourceIssuedAtUtc = DateTime.UtcNow, SourceValidUntilUtc = DateTime.UtcNow.AddDays(1)
            });
        }
        else
        {
            policy.IsEnabled = enabled.Value;
        }
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Admin_can_approve_and_reject_device_requests()
    {
        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", ApiFactory.AdminKey);
        var requests = await admin.GetFromJsonAsync<JsonElement>("/api/admin/device-requests");
        var approveRequest = requests.EnumerateArray().Single(x =>
            x.GetProperty("deviceId").GetString() == "approve-device");
        var approveId = approveRequest.GetProperty("id").GetInt32();
        var rejectId = requests.EnumerateArray().Single(x =>
            x.GetProperty("deviceId").GetString() == "reject-device").GetProperty("id").GetInt32();
        Assert.Equal("Integration Client", approveRequest.GetProperty("clientName").GetString());
        Assert.Equal("7701234567", approveRequest.GetProperty("clientInn").GetString());
        Assert.Equal("Test physical address", approveRequest.GetProperty("requestedAddress").GetString());
        Assert.Equal(JsonValueKind.Null, approveRequest.GetProperty("honestFlowVersion").ValueKind);

        var approve = await admin.PutAsJsonAsync($"/api/admin/device-requests/{approveId}/approve",
            new { comment = "ok" });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HonestDbContext>();
            var device = await db.Devices.SingleAsync(x => x.ExternalDeviceId == "approve-device");
            Assert.Equal("Approve Device", device.Name);
            Assert.Equal("Test physical address", device.Address);
            Assert.Equal(device.Id, await db.RefreshTokens
                .Where(x => x.RequestedExternalDeviceId == "approve-device")
                .Select(x => x.DeviceId)
                .SingleAsync());
        }
        using var approvedDevice = AuthenticatedClient(ApiFactory.ApproveAccessToken);
        Assert.Equal(HttpStatusCode.OK,
            (await approvedDevice.GetAsync("/api/configuration/current")).StatusCode);

        var reject = await admin.PutAsJsonAsync($"/api/admin/device-requests/{rejectId}/reject",
            new { comment = "not allowed" });
        Assert.Equal(HttpStatusCode.NoContent, reject.StatusCode);
        using var rejectedDevice = AuthenticatedClient(ApiFactory.RejectAccessToken);
        var rejectedStatus = await rejectedDevice.GetFromJsonAsync<JsonElement>(
            "/api/device/registration/current");
        Assert.Equal("Rejected", rejectedStatus.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Forbidden,
            (await rejectedDevice.GetAsync("/api/configuration/current")).StatusCode);
    }

    [Fact]
    public async Task Admin_soft_deletes_device_without_removing_database_history()
    {
        string externalDeviceId = "soft-delete-" + Guid.NewGuid().ToString("N");
        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", ApiFactory.AdminKey);

        var create = await admin.PostAsJsonAsync("/api/admin/devices", new
        {
            clientId = "integration-client",
            deviceId = externalDeviceId,
            name = "Device to delete",
            address = "Delete test address",
            comment = "Preserve history"
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        int id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        string refreshTokenValue = "refresh-" + Guid.NewGuid().ToString("N");
        await using (var setupScope = factory.Services.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<HonestDbContext>();
            var device = await db.Devices.SingleAsync(x => x.Id == id);
            var now = DateTime.UtcNow;
            db.RefreshTokens.Add(new HonestLicenseServer.Models.RefreshToken
            {
                ClientId = device.ClientId,
                DeviceId = device.Id,
                RequestedExternalDeviceId = externalDeviceId,
                AccessTokenHash = TokenHelper.Hash("access-" + refreshTokenValue),
                AccessTokenExpiresAtUtc = now.AddHours(1),
                TokenHash = TokenHelper.Hash(refreshTokenValue),
                TokenFamilyId = Guid.NewGuid().ToString("N"),
                CreatedAtUtc = now,
                ExpiresAtUtc = now.AddDays(1)
            });
            await db.SaveChangesAsync();
        }

        var delete = await admin.PutAsJsonAsync($"/api/admin/devices/{id}", new
        {
            name = "Device to delete",
            address = "Delete test address",
            comment = "Preserve history",
            status = "Deleted"
        });
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var devices = await admin.GetFromJsonAsync<JsonElement>(
            "/api/admin/devices?clientId=integration-client");
        var deleted = devices.EnumerateArray().Single(x =>
            x.GetProperty("deviceId").GetString() == externalDeviceId);
        Assert.Equal("Deleted", deleted.GetProperty("status").GetString());

        await using var scope = factory.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<HonestDbContext>()
            .Devices.SingleAsync(x => x.Id == id);
        Assert.Equal("Deleted", stored.Status);
        Assert.Equal("Delete test address", stored.Address);
        var revokedSession = await scope.ServiceProvider.GetRequiredService<HonestDbContext>()
            .RefreshTokens.SingleAsync(x => x.TokenHash == TokenHelper.Hash(refreshTokenValue));
        Assert.NotNull(revokedSession.RevokedAtUtc);
        Assert.Equal("device_disabled", revokedSession.RevokeReason);
    }

    [Fact]
    public async Task Admin_deletes_current_device_by_internal_id_with_multiple_released_history_rows()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string externalDeviceId = "delete-history-" + suffix;
        int otherClientId = await CreateClientAsync(
            "delete-history-client-" + suffix, "delete-history-password-" + suffix);
        int currentDeviceId;
        int firstHistoricalId;
        int secondHistoricalId;
        int crossClientHistoricalId;
        int sessionId;
        int licenseId;
        await using (var setupScope = factory.Services.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<HonestDbContext>();
            int clientId = await db.Clients.Where(x => x.ExternalClientId == "integration-client")
                .Select(x => x.Id).SingleAsync();
            var now = DateTime.UtcNow;
            var firstHistorical = new HonestLicenseServer.Models.Device
            {
                ClientId = clientId, ExternalDeviceId = "historical-first-" + suffix,
                Name = "Historical 1", Status = "Deleted", RegisteredAtUtc = now.AddDays(-30)
            };
            var secondHistorical = new HonestLicenseServer.Models.Device
            {
                ClientId = clientId, ExternalDeviceId = "historical-second-" + suffix,
                Name = "Historical 2", Status = "Deleted", RegisteredAtUtc = now.AddDays(-20)
            };
            var crossClientHistorical = new HonestLicenseServer.Models.Device
            {
                ClientId = otherClientId, ExternalDeviceId = "historical-cross-" + suffix,
                Name = "Other client history", Status = "Deleted", RegisteredAtUtc = now.AddDays(-10)
            };
            var current = new HonestLicenseServer.Models.Device
            {
                ClientId = clientId, ExternalDeviceId = externalDeviceId,
                Name = "Current device", Address = "Current address", Status = "Active",
                RegisteredAtUtc = now
            };
            db.Devices.AddRange(firstHistorical, secondHistorical, crossClientHistorical, current);
            await db.SaveChangesAsync();
            firstHistorical.ExternalDeviceId = ReleasedDeviceIdentity.Create(firstHistorical.Id, externalDeviceId);
            secondHistorical.ExternalDeviceId = ReleasedDeviceIdentity.Create(secondHistorical.Id, externalDeviceId);
            crossClientHistorical.ExternalDeviceId = ReleasedDeviceIdentity.Create(
                crossClientHistorical.Id, externalDeviceId);
            await db.SaveChangesAsync();
            firstHistoricalId = firstHistorical.Id;
            secondHistoricalId = secondHistorical.Id;
            crossClientHistoricalId = crossClientHistorical.Id;
            currentDeviceId = current.Id;
            var session = CreateSession(clientId, currentDeviceId, externalDeviceId,
                "delete-current-access-" + suffix, now);
            var license = new HonestLicenseServer.Models.License
            {
                ClientId = clientId, DeviceId = currentDeviceId, Revision = 1,
                GrantJson = "{}", GrantBytes = Encoding.UTF8.GetBytes("{}"),
                SignatureBase64 = "delete-history-signature", KeyId = "delete-history-key",
                SignatureScope = "PersonalGrant", Status = "Active",
                IssuedAtUtc = now, ValidUntilUtc = now.AddDays(30), PublishedAtUtc = now
            };
            db.AddRange(session, license);
            await db.SaveChangesAsync();
            sessionId = session.Id;
            licenseId = license.Id;
        }

        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", ApiFactory.AdminKey);
        var listed = await admin.GetFromJsonAsync<JsonElement>(
            "/api/admin/devices?clientId=integration-client");
        Assert.Equal(3, listed.EnumerateArray().Count(x =>
            x.GetProperty("deviceId").GetString() == externalDeviceId));
        object deleteBody = new
        {
            name = "Current device", address = "Current address",
            comment = (string?)null, status = "Deleted"
        };
        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.PutAsJsonAsync($"/api/admin/devices/{currentDeviceId}", deleteBody)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.PutAsJsonAsync($"/api/admin/devices/{currentDeviceId}", deleteBody)).StatusCode);

        await using var verificationScope = factory.Services.CreateAsyncScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<HonestDbContext>();
        Assert.Equal("Deleted", await verificationDb.Devices.Where(x => x.Id == currentDeviceId)
            .Select(x => x.Status).SingleAsync());
        Assert.Equal("Deleted", await verificationDb.Devices.Where(x => x.Id == firstHistoricalId)
            .Select(x => x.Status).SingleAsync());
        Assert.Equal(ReleasedDeviceIdentity.Create(secondHistoricalId, externalDeviceId),
            await verificationDb.Devices.Where(x => x.Id == secondHistoricalId)
                .Select(x => x.ExternalDeviceId).SingleAsync());
        Assert.Equal(otherClientId, await verificationDb.Devices.Where(x => x.Id == crossClientHistoricalId)
            .Select(x => x.ClientId).SingleAsync());
        var storedSession = await verificationDb.RefreshTokens.SingleAsync(x => x.Id == sessionId);
        Assert.NotNull(storedSession.RevokedAtUtc);
        Assert.Equal("device_disabled", storedSession.RevokeReason);
        Assert.True(await verificationDb.Licenses.AnyAsync(x =>
            x.Id == licenseId && x.DeviceId == currentDeviceId && x.Status == "Active"));
    }

    [Fact]
    public async Task Deleted_device_can_be_registered_again_and_reuses_device_and_pending_session()
    {
        string externalDeviceId = "reregister-" + Guid.NewGuid().ToString("N");
        int deviceId;
        int registrationRequestId;
        await using (var setupScope = factory.Services.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<HonestDbContext>();
            int clientId = await db.Clients.Where(x => x.ExternalClientId == "integration-client")
                .Select(x => x.Id).SingleAsync();
            var now = DateTime.UtcNow.AddMinutes(-5);
            var device = new HonestLicenseServer.Models.Device
            {
                ClientId = clientId,
                ExternalDeviceId = externalDeviceId,
                Name = "Old device name",
                Address = "Old address",
                Status = "Active",
                RegisteredAtUtc = now
            };
            var oldRequest = new HonestLicenseServer.Models.DeviceRegistrationRequest
            {
                ClientId = clientId,
                ExternalDeviceId = externalDeviceId,
                RequestedName = "Old device name",
                RequestedAddress = "Old address",
                Status = "Approved",
                RequestedAtUtc = now,
                ResolvedAtUtc = now.AddMinutes(1)
            };
            db.AddRange(device, oldRequest);
            await db.SaveChangesAsync();
            deviceId = device.Id;
            registrationRequestId = oldRequest.Id;
        }

        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", ApiFactory.AdminKey);
        var delete = await admin.PutAsJsonAsync($"/api/admin/devices/{deviceId}", new
        {
            name = "Old device name",
            address = "Old address",
            comment = (string?)null,
            status = "Deleted"
        });
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        using var honestFlow = factory.CreateClient();
        var login = await honestFlow.PostAsJsonAsync("/api/auth/login", new
        {
            password = "integration-password",
            deviceId = externalDeviceId
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var loginTokens = await login.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(loginTokens.GetProperty("deviceRegistrationRequired").GetBoolean());
        string accessToken = loginTokens.GetProperty("accessToken").GetString()!;
        string refreshToken = loginTokens.GetProperty("refreshToken").GetString()!;
        honestFlow.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        Assert.Equal(HttpStatusCode.NotFound,
            (await honestFlow.GetAsync("/api/device/registration/current")).StatusCode);

        var registration = await honestFlow.PostAsJsonAsync("/api/device/request", new
        {
            deviceId = externalDeviceId,
            name = "Current machine name",
            address = "Current physical address",
            honestFlowVersion = "3.0-test"
        });
        Assert.Equal(HttpStatusCode.Accepted, registration.StatusCode);
        var registrationResult = await registration.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(registrationRequestId, registrationResult.GetProperty("id").GetInt32());
        Assert.Equal("Pending", registrationResult.GetProperty("status").GetString());

        var approve = await admin.PutAsJsonAsync(
            $"/api/admin/device-requests/{registrationRequestId}/approve", new { comment = "approved again" });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        var approvedStatus = await honestFlow.GetFromJsonAsync<JsonElement>(
            "/api/device/registration/current");
        Assert.Equal("Approved", approvedStatus.GetProperty("status").GetString());

        await using (var verificationScope = factory.Services.CreateAsyncScope())
        {
            var db = verificationScope.ServiceProvider.GetRequiredService<HonestDbContext>();
            var device = await db.Devices.SingleAsync(x => x.Id == deviceId);
            Assert.Equal("Active", device.Status);
            Assert.Equal("Current machine name", device.Name);
            Assert.Equal("Current physical address", device.Address);
            Assert.Equal("approved again", device.Comment);
            Assert.Equal(deviceId, await db.RefreshTokens
                .Where(x => x.TokenHash == TokenHelper.Hash(refreshToken))
                .Select(x => x.DeviceId)
                .SingleAsync());
        }

        var refresh = await honestFlow.PostAsJsonAsync("/api/auth/refresh", new { refreshToken });
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        var refreshedTokens = await refresh.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(refreshedTokens.GetProperty("deviceRegistrationRequired").GetBoolean());
        honestFlow.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", refreshedTokens.GetProperty("accessToken").GetString());
        Assert.Equal(HttpStatusCode.OK,
            (await honestFlow.GetAsync("/api/configuration/current")).StatusCode);
    }

    [Fact]
    public async Task Disabled_device_login_remains_forbidden()
    {
        string externalDeviceId = "disabled-" + Guid.NewGuid().ToString("N");
        int deviceId;
        await using (var setupScope = factory.Services.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<HonestDbContext>();
            int clientId = await db.Clients.Where(x => x.ExternalClientId == "integration-client")
                .Select(x => x.Id).SingleAsync();
            var device = new HonestLicenseServer.Models.Device
            {
                ClientId = clientId,
                ExternalDeviceId = externalDeviceId,
                Name = "Disabled device",
                Status = "Active",
                RegisteredAtUtc = DateTime.UtcNow
            };
            db.Devices.Add(device);
            await db.SaveChangesAsync();
            deviceId = device.Id;
        }

        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", ApiFactory.AdminKey);
        var disable = await admin.PutAsJsonAsync($"/api/admin/devices/{deviceId}", new
        {
            name = "Disabled device",
            address = (string?)null,
            comment = (string?)null,
            status = "Disabled"
        });
        Assert.Equal(HttpStatusCode.NoContent, disable.StatusCode);

        using var honestFlow = factory.CreateClient();
        var login = await honestFlow.PostAsJsonAsync("/api/auth/login", new
        {
            password = "integration-password",
            deviceId = externalDeviceId
        });
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        var problem = await login.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("device_disabled", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Admin_can_read_and_update_client_integration_settings()
    {
        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", ApiFactory.AdminKey);

        var initial = await admin.GetFromJsonAsync<JsonElement>(
            "/api/admin/clients/integration-client/integration-settings");
        Assert.True(initial.GetProperty("isConfigured").GetBoolean());
        Assert.Equal("integration-password", initial.GetProperty("identificationCode").GetString());
        Assert.Equal("integration-chz-token", initial.GetProperty("chzToken").GetString());
        Assert.True(initial.GetProperty("ruDesktopEnabled").GetBoolean());
        Assert.False(initial.GetProperty("ruDesktopAutoOfferPasswordSetup").GetBoolean());

        var update = await admin.PutAsJsonAsync(
            "/api/admin/clients/integration-client/integration-settings",
            new
            {
                identificationCode = "integration-password",
                chzToken = "integration-chz-token-updated",
                ruDesktopEnabled = false,
                ruDesktopAutoOfferPasswordSetup = true
            });
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);

        var updated = await admin.GetFromJsonAsync<JsonElement>(
            "/api/admin/clients/integration-client/integration-settings");
        Assert.Equal("integration-chz-token-updated", updated.GetProperty("chzToken").GetString());
        Assert.False(updated.GetProperty("ruDesktopEnabled").GetBoolean());
        Assert.True(updated.GetProperty("ruDesktopAutoOfferPasswordSetup").GetBoolean());

        using var loginClient = factory.CreateClient();
        var login = await loginClient.PostAsJsonAsync("/api/auth/login", new
        {
            password = "integration-password", deviceId = "integration-device"
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task Admin_resolves_support_request_and_reload_returns_resolved_status()
    {
        int requestId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HonestDbContext>();
            int clientId = await db.Clients.Where(x => x.ExternalClientId == "integration-client")
                .Select(x => x.Id).SingleAsync();
            var request = new HonestLicenseServer.Models.SupportRequest
            {
                ClientId = clientId,
                ExternalDeviceId = "integration-device",
                Subject = "Resolve integration request",
                Message = "Resolve through admin API",
                Contact = "integration",
                Status = "Open",
                CreatedAtUtc = DateTime.UtcNow
            };
            db.SupportRequests.Add(request);
            await db.SaveChangesAsync();
            requestId = request.Id;
        }

        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", ApiFactory.AdminKey);
        var resolve = await admin.PutAsync($"/api/admin/support-requests/{requestId}/resolve", null);
        Assert.Equal(HttpStatusCode.NoContent, resolve.StatusCode);

        var resolved = await admin.GetFromJsonAsync<JsonElement>("/api/admin/support-requests?status=Resolved");
        Assert.Contains(resolved.EnumerateArray(), x => x.GetProperty("id").GetInt32() == requestId);
    }

    [Fact]
    public async Task Unknown_device_requires_explicit_registration_request_with_physical_address()
    {
        const string deviceId = "new-address-required-device";
        using var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            password = "integration-password", deviceId
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var tokens = await login.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(tokens.GetProperty("deviceRegistrationRequired").GetBoolean());
        Assert.Equal("integration-client", tokens.GetProperty("clientId").GetString());
        Assert.Equal("Integration Client", tokens.GetProperty("clientName").GetString());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HonestDbContext>();
            Assert.False(await db.DeviceRegistrationRequests.AnyAsync(x => x.ExternalDeviceId == deviceId));
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", tokens.GetProperty("accessToken").GetString());
        var missingAddress = await client.PostAsJsonAsync("/api/device/request", new
        {
            deviceId, name = "New shop computer"
        });
        Assert.Equal(HttpStatusCode.BadRequest, missingAddress.StatusCode);

        var registration = await client.PostAsJsonAsync("/api/device/request", new
        {
            deviceId, name = "New shop computer", address = "Physical shop address",
            honestFlowVersion = new string('v', 101)
        });
        Assert.Equal(HttpStatusCode.BadRequest, registration.StatusCode);

        registration = await client.PostAsJsonAsync("/api/device/request", new
        {
            deviceId, name = "New shop computer", address = "Physical shop address",
            honestFlowVersion = "3.0.1.0"
        });
        Assert.Equal(HttpStatusCode.Accepted, registration.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HonestDbContext>();
            var stored = await db.DeviceRegistrationRequests.SingleAsync(x => x.ExternalDeviceId == deviceId);
            Assert.Equal("Physical shop address", stored.RequestedAddress);
            Assert.Equal("3.0.1.0", stored.RequestedHonestFlowVersion);
        }

        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", ApiFactory.AdminKey);
        var requests = await admin.GetFromJsonAsync<JsonElement>("/api/admin/device-requests");
        var request = requests.EnumerateArray().Single(x =>
            x.GetProperty("deviceId").GetString() == deviceId);
        Assert.Equal("Physical shop address", request.GetProperty("requestedAddress").GetString());
        Assert.Equal("7701234567", request.GetProperty("clientInn").GetString());
        Assert.Equal("3.0.1.0", request.GetProperty("honestFlowVersion").GetString());
    }

    [Fact]
    public async Task Cross_client_device_login_is_rejected_without_changing_existing_history()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string externalDeviceId = "bound-device-" + suffix;
        string clientBPassword = "client-b-password-" + suffix;
        int clientBId = await CreateClientAsync("client-b-" + suffix, clientBPassword);
        int deviceId;
        int sessionId;
        int licenseId;
        int historyId;

        await using (var setupScope = factory.Services.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<HonestDbContext>();
            int clientAId = await db.Clients.Where(x => x.ExternalClientId == "integration-client")
                .Select(x => x.Id).SingleAsync();
            var now = DateTime.UtcNow;
            var device = new HonestLicenseServer.Models.Device
            {
                ClientId = clientAId,
                ExternalDeviceId = externalDeviceId,
                Name = "Bound device",
                Address = "Original address",
                Status = "Active",
                RegisteredAtUtc = now
            };
            db.Devices.Add(device);
            await db.SaveChangesAsync();
            deviceId = device.Id;

            var session = CreateSession(clientAId, device.Id, externalDeviceId,
                "bound-access-" + suffix, now);
            var license = new HonestLicenseServer.Models.License
            {
                ClientId = clientAId,
                DeviceId = device.Id,
                Revision = 1,
                GrantJson = "{}",
                GrantBytes = Encoding.UTF8.GetBytes("{}"),
                SignatureBase64 = "history-signature",
                KeyId = "history-key",
                SignatureScope = "PersonalGrant",
                Status = "Superseded",
                IssuedAtUtc = now.AddDays(-2),
                ValidUntilUtc = now.AddDays(10),
                PublishedAtUtc = now.AddDays(-2)
            };
            var history = new HonestLicenseServer.Models.DeviceRegistrationRequest
            {
                ClientId = clientAId,
                ExternalDeviceId = externalDeviceId,
                RequestedName = "Bound device",
                RequestedAddress = "Original address",
                Status = "Approved",
                RequestedAtUtc = now.AddDays(-3),
                ResolvedAtUtc = now.AddDays(-2)
            };
            db.AddRange(session, license, history);
            await db.SaveChangesAsync();
            sessionId = session.Id;
            licenseId = license.Id;
            historyId = history.Id;
        }

        using var clientA = factory.CreateClient();
        var sameClientLogin = await clientA.PostAsJsonAsync("/api/auth/login", new
        {
            password = "integration-password",
            deviceId = externalDeviceId
        });
        Assert.Equal(HttpStatusCode.OK, sameClientLogin.StatusCode);
        Assert.False((await sameClientLogin.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("deviceRegistrationRequired").GetBoolean());

        using var clientB = factory.CreateClient();
        var crossClientLogin = await clientB.PostAsJsonAsync("/api/auth/login", new
        {
            password = clientBPassword,
            deviceId = externalDeviceId
        });
        Assert.Equal(HttpStatusCode.Conflict, crossClientLogin.StatusCode);
        var problem = await crossClientLogin.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(DeviceBindingGuard.ErrorCode, problem.GetProperty("code").GetString());
        Assert.False(problem.TryGetProperty("deviceRegistrationRequired", out _));

        await using var verificationScope = factory.Services.CreateAsyncScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<HonestDbContext>();
        var storedDevice = await verificationDb.Devices.SingleAsync(x => x.Id == deviceId);
        Assert.Equal("Active", storedDevice.Status);
        Assert.Equal("Original address", storedDevice.Address);
        Assert.NotEqual(clientBId, storedDevice.ClientId);
        Assert.True(await verificationDb.RefreshTokens.AnyAsync(x => x.Id == sessionId));
        Assert.False(await verificationDb.RefreshTokens.AnyAsync(x =>
            x.ClientId == clientBId && x.RequestedExternalDeviceId == externalDeviceId));
        Assert.True(await verificationDb.Licenses.AnyAsync(x =>
            x.Id == licenseId && x.Status == "Superseded"));
        Assert.True(await verificationDb.DeviceRegistrationRequests.AnyAsync(x =>
            x.Id == historyId && x.Status == "Approved"));
    }

    [Theory]
    [InlineData("Disabled")]
    [InlineData("Deleted")]
    public async Task Cross_client_owned_device_status_never_starts_registration(string status)
    {
        string suffix = Guid.NewGuid().ToString("N");
        string externalDeviceId = "status-conflict-" + suffix;
        string clientBPassword = "status-password-" + suffix;
        await CreateClientAsync("status-client-b-" + suffix, clientBPassword);

        await using (var setupScope = factory.Services.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<HonestDbContext>();
            int clientAId = await db.Clients.Where(x => x.ExternalClientId == "integration-client")
                .Select(x => x.Id).SingleAsync();
            db.Devices.Add(new HonestLicenseServer.Models.Device
            {
                ClientId = clientAId,
                ExternalDeviceId = externalDeviceId,
                Name = status + " device",
                Status = status,
                RegisteredAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        using var clientB = factory.CreateClient();
        var login = await clientB.PostAsJsonAsync("/api/auth/login", new
        {
            password = clientBPassword,
            deviceId = externalDeviceId
        });

        Assert.Equal(HttpStatusCode.Conflict, login.StatusCode);
        var problem = await login.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(DeviceBindingGuard.ErrorCode, problem.GetProperty("code").GetString());
        await using var verificationScope = factory.Services.CreateAsyncScope();
        Assert.Equal(status, await verificationScope.ServiceProvider.GetRequiredService<HonestDbContext>()
            .Devices.Where(x => x.ExternalDeviceId == externalDeviceId)
            .Select(x => x.Status).SingleAsync());
    }

    [Fact]
    public async Task Direct_cross_client_registration_request_is_rejected()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string externalDeviceId = "direct-conflict-" + suffix;
        int clientBId = await CreateClientAsync(
            "direct-client-b-" + suffix,
            "direct-password-" + suffix);
        string accessToken = "direct-access-" + suffix;

        await using (var setupScope = factory.Services.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<HonestDbContext>();
            int clientAId = await db.Clients.Where(x => x.ExternalClientId == "integration-client")
                .Select(x => x.Id).SingleAsync();
            db.Devices.Add(new HonestLicenseServer.Models.Device
            {
                ClientId = clientAId,
                ExternalDeviceId = externalDeviceId,
                Name = "Client A device",
                Status = "Active",
                RegisteredAtUtc = DateTime.UtcNow
            });
            db.RefreshTokens.Add(CreateSession(
                clientBId, null, externalDeviceId, accessToken, DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        using var clientB = AuthenticatedClient(accessToken);
        var response = await clientB.PostAsJsonAsync("/api/device/request", new
        {
            deviceId = externalDeviceId,
            name = "Wrong client machine",
            address = "Wrong client address"
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(DeviceBindingGuard.ErrorCode, problem.GetProperty("code").GetString());
        await using var verificationScope = factory.Services.CreateAsyncScope();
        Assert.False(await verificationScope.ServiceProvider.GetRequiredService<HonestDbContext>()
            .DeviceRegistrationRequests.AnyAsync(x =>
                x.ClientId == clientBId && x.ExternalDeviceId == externalDeviceId));
    }

    [Fact]
    public async Task Pending_registration_for_another_client_blocks_login_and_second_request()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string externalDeviceId = "pending-conflict-" + suffix;
        string clientBPassword = "pending-password-" + suffix;
        int clientBId = await CreateClientAsync("pending-client-b-" + suffix, clientBPassword);
        string accessToken = "pending-cross-access-" + suffix;

        await using (var setupScope = factory.Services.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<HonestDbContext>();
            int clientAId = await db.Clients.Where(x => x.ExternalClientId == "integration-client")
                .Select(x => x.Id).SingleAsync();
            db.DeviceRegistrationRequests.Add(new HonestLicenseServer.Models.DeviceRegistrationRequest
            {
                ClientId = clientAId,
                ExternalDeviceId = externalDeviceId,
                RequestedName = "Client A pending device",
                RequestedAddress = "Client A address",
                Status = "Pending",
                RequestedAtUtc = DateTime.UtcNow
            });
            db.RefreshTokens.Add(CreateSession(
                clientBId, null, externalDeviceId, accessToken, DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        using var loginClient = factory.CreateClient();
        var login = await loginClient.PostAsJsonAsync("/api/auth/login", new
        {
            password = clientBPassword,
            deviceId = externalDeviceId
        });
        Assert.Equal(HttpStatusCode.Conflict, login.StatusCode);

        using var requestClient = AuthenticatedClient(accessToken);
        var request = await requestClient.PostAsJsonAsync("/api/device/request", new
        {
            deviceId = externalDeviceId,
            name = "Client B machine",
            address = "Client B address"
        });
        Assert.Equal(HttpStatusCode.Conflict, request.StatusCode);

        await using var verificationScope = factory.Services.CreateAsyncScope();
        var requests = await verificationScope.ServiceProvider.GetRequiredService<HonestDbContext>()
            .DeviceRegistrationRequests.Where(x => x.ExternalDeviceId == externalDeviceId)
            .ToListAsync();
        Assert.Single(requests);
        Assert.NotEqual(clientBId, requests[0].ClientId);
        Assert.Equal("Pending", requests[0].Status);
    }

    [Fact]
    public async Task Admin_cannot_approve_legacy_cross_client_pending_request()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string externalDeviceId = "approve-conflict-" + suffix;
        int clientBId = await CreateClientAsync(
            "approve-client-b-" + suffix,
            "approve-password-" + suffix);
        int requestId;

        await using (var setupScope = factory.Services.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<HonestDbContext>();
            int clientAId = await db.Clients.Where(x => x.ExternalClientId == "integration-client")
                .Select(x => x.Id).SingleAsync();
            db.Devices.Add(new HonestLicenseServer.Models.Device
            {
                ClientId = clientAId,
                ExternalDeviceId = externalDeviceId,
                Name = "Existing owner",
                Status = "Active",
                RegisteredAtUtc = DateTime.UtcNow
            });
            var pending = new HonestLicenseServer.Models.DeviceRegistrationRequest
            {
                ClientId = clientBId,
                ExternalDeviceId = externalDeviceId,
                RequestedName = "Conflicting request",
                RequestedAddress = "Conflicting address",
                Status = "Pending",
                RequestedAtUtc = DateTime.UtcNow
            };
            db.DeviceRegistrationRequests.Add(pending);
            await db.SaveChangesAsync();
            requestId = pending.Id;
        }

        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", ApiFactory.AdminKey);
        var approve = await admin.PutAsJsonAsync(
            $"/api/admin/device-requests/{requestId}/approve",
            new { comment = "must not transfer" });

        Assert.Equal(HttpStatusCode.Conflict, approve.StatusCode);
        var error = await approve.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(DeviceBindingGuard.ErrorCode, error.GetProperty("code").GetString());
        await using var verificationScope = factory.Services.CreateAsyncScope();
        var dbAfter = verificationScope.ServiceProvider.GetRequiredService<HonestDbContext>();
        Assert.Equal("Pending", await dbAfter.DeviceRegistrationRequests
            .Where(x => x.Id == requestId).Select(x => x.Status).SingleAsync());
        Assert.Single(await dbAfter.Devices.Where(x => x.ExternalDeviceId == externalDeviceId)
            .ToListAsync());
    }

    [Fact]
    public async Task Concurrent_cross_client_registration_requests_create_only_one_pending_owner()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string externalDeviceId = "race-device-" + suffix;
        int clientAId;
        int clientBId = await CreateClientAsync(
            "race-client-b-" + suffix,
            "race-password-" + suffix);
        string accessA = "race-access-a-" + suffix;
        string accessB = "race-access-b-" + suffix;

        await using (var setupScope = factory.Services.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<HonestDbContext>();
            clientAId = await db.Clients.Where(x => x.ExternalClientId == "integration-client")
                .Select(x => x.Id).SingleAsync();
            db.RefreshTokens.AddRange(
                CreateSession(clientAId, null, externalDeviceId, accessA, DateTime.UtcNow),
                CreateSession(clientBId, null, externalDeviceId, accessB, DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        using var first = AuthenticatedClient(accessA);
        using var second = AuthenticatedClient(accessB);
        Task<HttpResponseMessage> firstRequest = first.PostAsJsonAsync("/api/device/request", new
        {
            deviceId = externalDeviceId,
            name = "Race machine A",
            address = "Race address A"
        });
        Task<HttpResponseMessage> secondRequest = second.PostAsJsonAsync("/api/device/request", new
        {
            deviceId = externalDeviceId,
            name = "Race machine B",
            address = "Race address B"
        });
        HttpResponseMessage[] responses = await Task.WhenAll(firstRequest, secondRequest);
        using var responseA = responses[0];
        using var responseB = responses[1];

        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Accepted);
        HttpResponseMessage conflict = Assert.Single(
            responses, x => x.StatusCode == HttpStatusCode.Conflict);
        var problem = await conflict.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(DeviceBindingGuard.ErrorCode, problem.GetProperty("code").GetString());

        await using var verificationScope = factory.Services.CreateAsyncScope();
        var pending = await verificationScope.ServiceProvider.GetRequiredService<HonestDbContext>()
            .DeviceRegistrationRequests.Where(x =>
                x.ExternalDeviceId == externalDeviceId && x.Status == "Pending")
            .ToListAsync();
        Assert.Single(pending);
        Assert.Contains(pending[0].ClientId, new[] { clientAId, clientBId });
    }

    [Fact]
    public async Task Admin_restores_bound_deleted_device_and_preserves_history()
    {
        string externalDeviceId = "restore-admin-" + Guid.NewGuid().ToString("N");
        int deviceId;
        await using (var setupScope = factory.Services.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<HonestDbContext>();
            int clientId = await db.Clients.Where(x => x.ExternalClientId == "integration-client")
                .Select(x => x.Id).SingleAsync();
            var device = new HonestLicenseServer.Models.Device
            {
                ClientId = clientId,
                ExternalDeviceId = externalDeviceId,
                Name = "Deleted device",
                Status = "Deleted",
                RegisteredAtUtc = DateTime.UtcNow.AddDays(-10)
            };
            db.Devices.Add(device);
            await db.SaveChangesAsync();
            deviceId = device.Id;
            db.DeviceRegistrationRequests.Add(new HonestLicenseServer.Models.DeviceRegistrationRequest
            {
                ClientId = clientId,
                ExternalDeviceId = externalDeviceId,
                RequestedName = device.Name,
                Status = "Approved",
                RequestedAtUtc = DateTime.UtcNow.AddDays(-10),
                ResolvedAtUtc = DateTime.UtcNow.AddDays(-9)
            });
            await db.SaveChangesAsync();
        }

        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", ApiFactory.AdminKey);
        var response = await admin.PutAsync($"/api/admin/devices/{deviceId}/restore", null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var verificationDb = scope.ServiceProvider.GetRequiredService<HonestDbContext>();
        var restored = await verificationDb.Devices.SingleAsync(x => x.Id == deviceId);
        Assert.Equal("Active", restored.Status);
        Assert.Equal(externalDeviceId, restored.ExternalDeviceId);
        Assert.True(await verificationDb.DeviceRegistrationRequests.AnyAsync(x =>
            x.ClientId == restored.ClientId && x.ExternalDeviceId == externalDeviceId));
        Assert.True(await verificationDb.AuditEvents.AnyAsync(x =>
            x.Action == "Device.Restored" && x.EntityId == deviceId.ToString()));
    }

    [Fact]
    public async Task Released_device_ignores_historical_approval_and_starts_a_new_registration()
    {
        string externalDeviceId = "released-registration-" + Guid.NewGuid().ToString("N");
        int historicalDeviceId;
        int historicalRequestId;
        await using (var setupScope = factory.Services.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<HonestDbContext>();
            int clientId = await db.Clients.Where(x => x.ExternalClientId == "integration-client")
                .Select(x => x.Id).SingleAsync();
            var device = new HonestLicenseServer.Models.Device
            {
                ClientId = clientId, ExternalDeviceId = externalDeviceId,
                Name = "Released historical device", Status = "Deleted",
                RegisteredAtUtc = DateTime.UtcNow.AddDays(-5)
            };
            var approved = new HonestLicenseServer.Models.DeviceRegistrationRequest
            {
                ClientId = clientId, ExternalDeviceId = externalDeviceId,
                RequestedName = "Historical request", RequestedAddress = "Historical address",
                Status = "Approved", RequestedAtUtc = DateTime.UtcNow.AddDays(-5),
                ResolvedAtUtc = DateTime.UtcNow.AddDays(-4)
            };
            db.AddRange(device, approved);
            await db.SaveChangesAsync();
            historicalDeviceId = device.Id;
            historicalRequestId = approved.Id;
        }

        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", ApiFactory.AdminKey);
        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.PutAsync($"/api/admin/devices/{historicalDeviceId}/release", null)).StatusCode);

        using var honestFlow = factory.CreateClient();
        var login = await honestFlow.PostAsJsonAsync("/api/auth/login", new
        {
            password = "integration-password", deviceId = externalDeviceId
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var loginBody = await login.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(loginBody.GetProperty("deviceRegistrationRequired").GetBoolean());
        string refreshToken = loginBody.GetProperty("refreshToken").GetString()!;
        honestFlow.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", loginBody.GetProperty("accessToken").GetString());

        Assert.Equal(HttpStatusCode.NotFound,
            (await honestFlow.GetAsync("/api/device/registration/current")).StatusCode);

        var create = await honestFlow.PostAsJsonAsync("/api/device/request", new
        {
            deviceId = externalDeviceId,
            name = "New device", address = "New physical address"
        });
        Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
        var currentRequest = await create.Content.ReadFromJsonAsync<JsonElement>();
        int currentRequestId = currentRequest.GetProperty("id").GetInt32();
        Assert.NotEqual(historicalRequestId, currentRequestId);
        Assert.Equal("Pending", currentRequest.GetProperty("status").GetString());
        var current = await honestFlow.GetFromJsonAsync<JsonElement>("/api/device/registration/current");
        Assert.Equal("Pending", current.GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(
            $"/api/admin/device-requests/{currentRequestId}/approve", new { })).StatusCode);
        await using var verificationScope = factory.Services.CreateAsyncScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<HonestDbContext>();
        var historical = await verificationDb.DeviceRegistrationRequests.SingleAsync(x => x.Id == historicalRequestId);
        Assert.Equal("Approved", historical.Status);
        Assert.True(await verificationDb.Devices.AnyAsync(x =>
            x.Id != historicalDeviceId && x.ExternalDeviceId == externalDeviceId && x.Status == "Active"));
        Assert.Equal(historicalDeviceId, await verificationDb.Devices
            .Where(x => x.Id == historicalDeviceId).Select(x => x.Id).SingleAsync());
        int? linkedDeviceId = await verificationDb.RefreshTokens
            .Where(x => x.TokenHash == TokenHelper.Hash(refreshToken))
            .Select(x => x.DeviceId).SingleAsync();
        Assert.NotNull(linkedDeviceId);
        Assert.Equal("Active", await verificationDb.Devices.Where(x => x.Id == linkedDeviceId)
            .Select(x => x.Status).SingleAsync());
    }

    [Fact]
    public async Task Historical_approval_from_another_client_does_not_create_current_registration_state()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string externalDeviceId = "cross-history-" + suffix;
        string password = "cross-history-password-" + suffix;
        int otherClientId = await CreateClientAsync("cross-history-client-" + suffix, password);
        await using (var setupScope = factory.Services.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<HonestDbContext>();
            int historicalClientId = await db.Clients.Where(x => x.ExternalClientId == "integration-client")
                .Select(x => x.Id).SingleAsync();
            db.DeviceRegistrationRequests.Add(new HonestLicenseServer.Models.DeviceRegistrationRequest
            {
                ClientId = historicalClientId, ExternalDeviceId = externalDeviceId,
                RequestedName = "Other client history", Status = "Approved",
                RequestedAtUtc = DateTime.UtcNow.AddDays(-2), ResolvedAtUtc = DateTime.UtcNow.AddDays(-1)
            });
            await db.SaveChangesAsync();
        }

        using var honestFlow = factory.CreateClient();
        var login = await honestFlow.PostAsJsonAsync("/api/auth/login", new { password, deviceId = externalDeviceId });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var loginBody = await login.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(loginBody.GetProperty("deviceRegistrationRequired").GetBoolean());
        honestFlow.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            loginBody.GetProperty("accessToken").GetString());
        Assert.Equal(HttpStatusCode.NotFound,
            (await honestFlow.GetAsync("/api/device/registration/current")).StatusCode);

        await using var verificationScope = factory.Services.CreateAsyncScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<HonestDbContext>();
        Assert.False(await verificationDb.DeviceRegistrationRequests.AnyAsync(x =>
            x.ClientId == otherClientId && x.ExternalDeviceId == externalDeviceId));
    }

    [Fact]
    public async Task Rejected_registration_is_reopened_as_pending()
    {
        string externalDeviceId = "reopen-rejected-" + Guid.NewGuid().ToString("N");
        int requestId;
        string accessToken = "reopen-access-" + Guid.NewGuid().ToString("N");
        await using (var setupScope = factory.Services.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<HonestDbContext>();
            int clientId = await db.Clients.Where(x => x.ExternalClientId == "integration-client")
                .Select(x => x.Id).SingleAsync();
            var rejected = new HonestLicenseServer.Models.DeviceRegistrationRequest
            {
                ClientId = clientId, ExternalDeviceId = externalDeviceId,
                RequestedName = "Rejected request", Status = "Rejected",
                RequestedAtUtc = DateTime.UtcNow.AddDays(-1), ResolvedAtUtc = DateTime.UtcNow.AddDays(-1),
                Comment = "Rejected before"
            };
            db.Add(rejected);
            db.RefreshTokens.Add(CreateSession(clientId, null, externalDeviceId, accessToken, DateTime.UtcNow));
            await db.SaveChangesAsync();
            requestId = rejected.Id;
        }

        using var honestFlow = AuthenticatedClient(accessToken);
        var create = await honestFlow.PostAsJsonAsync("/api/device/request", new
        {
            deviceId = externalDeviceId, name = "Retry", address = "Retry address"
        });
        Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
        Assert.Equal(requestId, (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32());
        var current = await honestFlow.GetFromJsonAsync<JsonElement>("/api/device/registration/current");
        Assert.Equal("Pending", current.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Release_and_reregistration_cycle_can_be_completed_twice_with_full_history()
    {
        string externalDeviceId = "repeat-release-" + Guid.NewGuid().ToString("N");
        int currentDeviceId;
        int clientId;
        await using (var setupScope = factory.Services.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<HonestDbContext>();
            clientId = await db.Clients.Where(x => x.ExternalClientId == "integration-client")
                .Select(x => x.Id).SingleAsync();
            var device = new HonestLicenseServer.Models.Device
            {
                ClientId = clientId, ExternalDeviceId = externalDeviceId,
                Name = "Initial device", Status = "Active", RegisteredAtUtc = DateTime.UtcNow.AddDays(-10)
            };
            db.Devices.Add(device);
            db.DeviceRegistrationRequests.Add(new HonestLicenseServer.Models.DeviceRegistrationRequest
            {
                ClientId = clientId, ExternalDeviceId = externalDeviceId,
                RequestedName = device.Name, Status = "Approved",
                RequestedAtUtc = DateTime.UtcNow.AddDays(-10), ResolvedAtUtc = DateTime.UtcNow.AddDays(-9)
            });
            await db.SaveChangesAsync();
            currentDeviceId = device.Id;
        }

        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", ApiFactory.AdminKey);
        for (int cycle = 1; cycle <= 2; cycle++)
        {
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync(
                $"/api/admin/devices/{currentDeviceId}", new
                {
                    name = $"Cycle {cycle} device", address = $"Cycle {cycle} address",
                    comment = (string?)null, status = "Deleted"
                })).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent,
                (await admin.PutAsync($"/api/admin/devices/{currentDeviceId}/release", null)).StatusCode);

            using var honestFlow = factory.CreateClient();
            var login = await honestFlow.PostAsJsonAsync("/api/auth/login", new
            {
                password = "integration-password", deviceId = externalDeviceId
            });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            var loginBody = await login.Content.ReadFromJsonAsync<JsonElement>();
            honestFlow.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer", loginBody.GetProperty("accessToken").GetString());
            Assert.Equal(HttpStatusCode.NotFound,
                (await honestFlow.GetAsync("/api/device/registration/current")).StatusCode);
            var request = await honestFlow.PostAsJsonAsync("/api/device/request", new
            {
                deviceId = externalDeviceId,
                name = $"Cycle {cycle} device", address = $"Cycle {cycle} address"
            });
            Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);
            int requestId = (await request.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
            var approve = await admin.PutAsJsonAsync(
                $"/api/admin/device-requests/{requestId}/approve", new { });
            Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
            currentDeviceId = (await approve.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        }

        await using var verificationScope = factory.Services.CreateAsyncScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<HonestDbContext>();
        Assert.Equal(3, await verificationDb.DeviceRegistrationRequests.CountAsync(x =>
            x.ClientId == clientId && x.ExternalDeviceId == externalDeviceId && x.Status == "Approved"));
        Assert.Equal(3, await verificationDb.Devices.CountAsync(x => x.ClientId == clientId &&
            (x.ExternalDeviceId == externalDeviceId || x.ExternalDeviceId.EndsWith("-" + externalDeviceId))));
        Assert.Equal(1, await verificationDb.Devices.CountAsync(x =>
            x.ClientId == clientId && x.ExternalDeviceId == externalDeviceId && x.Status == "Active"));
    }

    [Fact]
    public async Task Concurrent_same_client_registration_requests_return_one_pending_record()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string externalDeviceId = "same-client-race-" + suffix;
        string firstAccess = "same-client-access-a-" + suffix;
        string secondAccess = "same-client-access-b-" + suffix;
        int clientId;
        await using (var setupScope = factory.Services.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<HonestDbContext>();
            clientId = await db.Clients.Where(x => x.ExternalClientId == "integration-client")
                .Select(x => x.Id).SingleAsync();
            db.RefreshTokens.AddRange(
                CreateSession(clientId, null, externalDeviceId, firstAccess, DateTime.UtcNow),
                CreateSession(clientId, null, externalDeviceId, secondAccess, DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        using var first = AuthenticatedClient(firstAccess);
        using var second = AuthenticatedClient(secondAccess);
        Task<HttpResponseMessage> firstTask = first.PostAsJsonAsync("/api/device/request", new
        {
            deviceId = externalDeviceId, name = "Race A", address = "Race address A"
        });
        Task<HttpResponseMessage> secondTask = second.PostAsJsonAsync("/api/device/request", new
        {
            deviceId = externalDeviceId, name = "Race B", address = "Race address B"
        });
        HttpResponseMessage[] responses = await Task.WhenAll(firstTask, secondTask);
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
        int[] ids = await Task.WhenAll(responses.Select(async response =>
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32()));
        Assert.Single(ids.Distinct());

        await using var verificationScope = factory.Services.CreateAsyncScope();
        Assert.Equal(1, await verificationScope.ServiceProvider.GetRequiredService<HonestDbContext>()
            .DeviceRegistrationRequests.CountAsync(x => x.ClientId == clientId &&
                x.ExternalDeviceId == externalDeviceId && x.Status == "Pending"));
        foreach (HttpResponseMessage response in responses) response.Dispose();
    }

    [Fact]
    public async Task Admin_restore_rejects_cross_client_device_binding()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string externalDeviceId = "restore-conflict-" + suffix;
        int otherClientId = await CreateClientAsync("restore-client-" + suffix, "password-" + suffix);
        int deletedDeviceId;
        await using (var setupScope = factory.Services.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<HonestDbContext>();
            int clientId = await db.Clients.Where(x => x.ExternalClientId == "integration-client")
                .Select(x => x.Id).SingleAsync();
            var deleted = new HonestLicenseServer.Models.Device
            {
                ClientId = clientId, ExternalDeviceId = externalDeviceId,
                Name = "Deleted owner", Status = "Deleted", RegisteredAtUtc = DateTime.UtcNow
            };
            db.Devices.AddRange(deleted, new HonestLicenseServer.Models.Device
            {
                ClientId = otherClientId, ExternalDeviceId = externalDeviceId,
                Name = "Current owner", Status = "Active", RegisteredAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            deletedDeviceId = deleted.Id;
        }

        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", ApiFactory.AdminKey);
        var response = await admin.PutAsync($"/api/admin/devices/{deletedDeviceId}/restore", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(DeviceBindingGuard.ErrorCode, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Admin_release_preserves_history_revokes_sessions_and_allows_other_client_registration()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string externalDeviceId = "release-" + suffix;
        string otherPassword = "release-password-" + suffix;
        int otherClientId = await CreateClientAsync("release-client-" + suffix, otherPassword);
        int deviceId;
        int originalClientId;
        int licenseId;
        int sessionId;
        await using (var setupScope = factory.Services.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<HonestDbContext>();
            originalClientId = await db.Clients.Where(x => x.ExternalClientId == "integration-client")
                .Select(x => x.Id).SingleAsync();
            var device = new HonestLicenseServer.Models.Device
            {
                ClientId = originalClientId, ExternalDeviceId = externalDeviceId,
                Name = "Historical device", Address = "Old address", Comment = "Old comment",
                Status = "Deleted", RegisteredAtUtc = DateTime.UtcNow.AddDays(-30)
            };
            db.Devices.Add(device);
            await db.SaveChangesAsync();
            deviceId = device.Id;
            var session = CreateSession(originalClientId, deviceId, externalDeviceId,
                "release-access-" + suffix, DateTime.UtcNow);
            var license = new HonestLicenseServer.Models.License
            {
                ClientId = originalClientId, DeviceId = deviceId, Revision = 1,
                GrantJson = "{}", GrantBytes = Encoding.UTF8.GetBytes("{}"),
                SignatureBase64 = "historical-signature", KeyId = "historical-key",
                SignatureScope = "PersonalGrant", Status = "Superseded",
                IssuedAtUtc = DateTime.UtcNow.AddDays(-20),
                ValidUntilUtc = DateTime.UtcNow.AddDays(10),
                PublishedAtUtc = DateTime.UtcNow.AddDays(-20)
            };
            db.AddRange(session, license);
            await db.SaveChangesAsync();
            sessionId = session.Id;
            licenseId = license.Id;
        }

        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", ApiFactory.AdminKey);
        var release = await admin.PutAsync($"/api/admin/devices/{deviceId}/release", null);
        Assert.Equal(HttpStatusCode.NoContent, release.StatusCode);

        var listed = await admin.GetFromJsonAsync<JsonElement>("/api/admin/devices");
        var released = listed.EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == deviceId);
        Assert.Equal(externalDeviceId, released.GetProperty("deviceId").GetString());
        Assert.True(released.GetProperty("deviceIdReleased").GetBoolean());
        Assert.Equal("Deleted", released.GetProperty("status").GetString());

        using var otherClient = factory.CreateClient();
        var login = await otherClient.PostAsJsonAsync("/api/auth/login", new
        {
            password = otherPassword,
            deviceId = externalDeviceId
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var loginBody = await login.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(loginBody.GetProperty("deviceRegistrationRequired").GetBoolean());
        otherClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", loginBody.GetProperty("accessToken").GetString());
        var request = await otherClient.PostAsJsonAsync("/api/device/request", new
        {
            deviceId = externalDeviceId,
            name = "New client device",
            address = "New address"
        });
        Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);
        int requestId = (await request.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var approve = await admin.PutAsJsonAsync($"/api/admin/device-requests/{requestId}/approve", new { });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var verificationDb = scope.ServiceProvider.GetRequiredService<HonestDbContext>();
        var historical = await verificationDb.Devices.SingleAsync(x => x.Id == deviceId);
        Assert.Equal("Deleted", historical.Status);
        Assert.Equal(ReleasedDeviceIdentity.Create(deviceId, externalDeviceId), historical.ExternalDeviceId);
        Assert.True(await verificationDb.Devices.AnyAsync(x =>
            x.Id != deviceId && x.ClientId == otherClientId &&
            x.ExternalDeviceId == externalDeviceId && x.Status == "Active"));
        var oldSession = await verificationDb.RefreshTokens.SingleAsync(x => x.Id == sessionId);
        Assert.Equal(originalClientId, oldSession.ClientId);
        Assert.NotNull(oldSession.RevokedAtUtc);
        Assert.Equal("device_id_released", oldSession.RevokeReason);
        Assert.True(await verificationDb.Licenses.AnyAsync(x =>
            x.Id == licenseId && x.ClientId == originalClientId && x.DeviceId == deviceId));
        var audit = await verificationDb.AuditEvents.SingleAsync(x =>
            x.Action == "Device.ExternalIdReleased" && x.EntityId == deviceId.ToString());
        Assert.Contains(externalDeviceId, audit.DetailsJson);
    }

    [Fact]
    public async Task Disabled_client_remains_listed_and_can_be_enabled_without_new_lifecycle()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string externalClientId = "disabled-client-" + suffix;
        await CreateClientAsync(externalClientId, "disabled-password-" + suffix);
        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", ApiFactory.AdminKey);

        var disable = await admin.PutAsJsonAsync($"/api/admin/clients/{externalClientId}", new
        {
            name = externalClientId,
            inn = (string?)null,
            architecture = "x64",
            isActive = false,
            hasLmDatabaseBackup = false
        });
        Assert.Equal(HttpStatusCode.NoContent, disable.StatusCode);
        var disabledList = await admin.GetFromJsonAsync<JsonElement>("/api/admin/clients");
        Assert.False(disabledList.EnumerateArray().Single(x =>
            x.GetProperty("clientId").GetString() == externalClientId).GetProperty("isActive").GetBoolean());

        var enable = await admin.PutAsJsonAsync($"/api/admin/clients/{externalClientId}", new
        {
            name = externalClientId,
            inn = (string?)null,
            architecture = "x64",
            isActive = true,
            hasLmDatabaseBackup = false
        });
        Assert.Equal(HttpStatusCode.NoContent, enable.StatusCode);
        var enabledList = await admin.GetFromJsonAsync<JsonElement>("/api/admin/clients");
        Assert.True(enabledList.EnumerateArray().Single(x =>
            x.GetProperty("clientId").GetString() == externalClientId).GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task OpenApi_has_unique_operation_ids_and_correct_security_schemes()
    {
        using var client = factory.CreateClient();
        var document = await client.GetFromJsonAsync<JsonElement>("/swagger/v1/swagger.json");
        var operations = document.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject()
                .Where(operation => operation.Name is "get" or "post" or "put" or "delete")
                .Select(operation => (Path: path.Name, Method: operation.Name, Value: operation.Value)))
            .ToList();
        var operationIds = operations.Select(x => x.Value.GetProperty("operationId").GetString()!).ToList();
        Assert.DoesNotContain(operationIds, string.IsNullOrWhiteSpace);
        Assert.Equal(operationIds.Count, operationIds.Distinct(StringComparer.Ordinal).Count());

        var login = operations.Single(x => x.Path == "/api/auth/login").Value;
        Assert.Empty(login.GetProperty("security").EnumerateArray());
        Assert.True(login.GetProperty("responses").TryGetProperty("409", out _));
        var license = operations.Single(x => x.Path == "/api/license/current").Value;
        Assert.True(license.GetProperty("security")[0].TryGetProperty("Bearer", out _),
            license.GetProperty("security").GetRawText());
        var admin = operations.Single(x => x.Path == "/api/admin/clients" && x.Method == "get").Value;
        Assert.True(admin.GetProperty("security")[0].TryGetProperty("AdminKey", out _));
        var registrationRequest = document.GetProperty("components").GetProperty("schemas")
            .GetProperty("DeviceRegistrationRequestDto");
        var version = registrationRequest.GetProperty("properties").GetProperty("honestFlowVersion");
        Assert.True(version.GetProperty("nullable").GetBoolean());
        Assert.False(registrationRequest.TryGetProperty("required", out var required) &&
            required.EnumerateArray().Any(x => x.GetString() == "honestFlowVersion"));
    }

    private HttpClient AuthenticatedClient(string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<int> CreateClientAsync(string externalClientId, string password)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HonestDbContext>();
        var now = DateTime.UtcNow;
        var client = new HonestLicenseServer.Models.Client
        {
            ExternalClientId = externalClientId,
            Name = externalClientId,
            Architecture = "x64",
            IsActive = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        client.Credentials.Add(new HonestLicenseServer.Models.Credential
        {
            Login = externalClientId,
            PasswordHash = PasswordHasher.Hash(password),
            IsActive = true,
            PasswordChangedAtUtc = now
        });
        client.Settings = new HonestLicenseServer.Models.ClientSetting
        {
            IdentificationCode = password
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return client.Id;
    }

    private static HonestLicenseServer.Models.RefreshToken CreateSession(
        int clientId,
        int? deviceId,
        string externalDeviceId,
        string accessToken,
        DateTime now) => new()
    {
        ClientId = clientId,
        DeviceId = deviceId,
        RequestedExternalDeviceId = externalDeviceId,
        AccessTokenHash = TokenHelper.Hash(accessToken),
        AccessTokenExpiresAtUtc = now.AddHours(1),
        TokenHash = TokenHelper.Hash("refresh-" + accessToken),
        TokenFamilyId = Guid.NewGuid().ToString("N"),
        CreatedAtUtc = now,
        ExpiresAtUtc = now.AddDays(1)
    };
}
