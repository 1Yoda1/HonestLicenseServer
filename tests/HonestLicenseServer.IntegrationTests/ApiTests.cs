using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using Xunit;

namespace HonestLicenseServer.IntegrationTests;

public sealed class ApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
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
    public async Task Empty_login_returns_validation_problem()
    {
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation_failed", json.GetProperty("code").GetString());
        Assert.True(json.GetProperty("errors").TryGetProperty("Login", out _));
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
        var component = json.GetProperty("components").EnumerateArray().Single();
        Assert.Equal("2.6.2.0", component.GetProperty("globalVersion").GetString());
        Assert.Equal("2.5.0", component.GetProperty("effectiveVersion").GetString());
        Assert.Equal("HonestFlow-2.5.0.zip", component.GetProperty("fileName").GetString());
        Assert.True(component.GetProperty("isOverride").GetBoolean());
    }

    [Fact]
    public async Task Signed_grant_supports_etag_and_revocation()
    {
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
        var revoked = await honestFlow.GetAsync("/api/license/current");
        Assert.Equal(HttpStatusCode.Gone, revoked.StatusCode);
        var revokedProblem = await revoked.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("license_revoked", revokedProblem.GetProperty("code").GetString());

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
            login = "integration-login", password = "integration-password",
            deviceId = "integration-device", deviceName = "Test Device"
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var loginTokens = await login.Content.ReadFromJsonAsync<JsonElement>();
        var accessToken = loginTokens.GetProperty("accessToken").GetString()!;
        var refreshToken = loginTokens.GetProperty("refreshToken").GetString()!;
        Assert.False(loginTokens.GetProperty("deviceRegistrationRequired").GetBoolean());

        var refresh = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken });
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        var refreshedTokens = await refresh.Content.ReadFromJsonAsync<JsonElement>();
        var refreshedAccess = refreshedTokens.GetProperty("accessToken").GetString()!;
        var reused = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", refreshedAccess);
        var logout = await client.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        var afterLogout = await client.GetAsync("/api/configuration/current");
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);

        Assert.NotEqual(accessToken, refreshedAccess);
    }

    [Fact]
    public async Task Admin_can_approve_and_reject_device_requests()
    {
        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", ApiFactory.AdminKey);
        var requests = await admin.GetFromJsonAsync<JsonElement>("/api/admin/device-requests");
        var approveId = requests.EnumerateArray().Single(x =>
            x.GetProperty("deviceId").GetString() == "approve-device").GetProperty("id").GetInt32();
        var rejectId = requests.EnumerateArray().Single(x =>
            x.GetProperty("deviceId").GetString() == "reject-device").GetProperty("id").GetInt32();

        var approve = await admin.PutAsJsonAsync($"/api/admin/device-requests/{approveId}/approve",
            new { name = "Approved Device", address = "Approved address", comment = "ok" });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
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
        var license = operations.Single(x => x.Path == "/api/license/current").Value;
        Assert.True(license.GetProperty("security")[0].TryGetProperty("Bearer", out _),
            license.GetProperty("security").GetRawText());
        var admin = operations.Single(x => x.Path == "/api/admin/clients" && x.Method == "get").Value;
        Assert.True(admin.GetProperty("security")[0].TryGetProperty("AdminKey", out _));
    }

    private HttpClient AuthenticatedClient(string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
