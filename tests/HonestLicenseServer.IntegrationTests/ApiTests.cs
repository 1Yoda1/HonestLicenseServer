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
    public async Task Signed_grant_is_verified_stored_as_original_bytes_and_returned_unchanged()
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

        using var honestFlow = AuthenticatedClient(ApiFactory.ActiveAccessToken);
        var current = await honestFlow.GetAsync("/api/license/current");
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        var response = await current.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(request.grantBase64, response.GetProperty("grantBase64").GetString());
        Assert.Equal("integration-key", response.GetProperty("keyId").GetString());
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

    private HttpClient AuthenticatedClient(string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
