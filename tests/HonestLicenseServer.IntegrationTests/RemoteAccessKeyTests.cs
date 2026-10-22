using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace HonestLicenseServer.IntegrationTests;

[Collection(IntegrationTestCollection.Name)]
public sealed class RemoteAccessKeyTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Admin_key_must_be_signed_and_is_published_without_private_material()
    {
        using var rsa = RSA.Create(3072);
        string keyId = Guid.NewGuid().ToString("D");
        string publicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
        byte[] signedBytes = Encoding.UTF8.GetBytes(
            $"HonestFlow.RemoteAccessKey.v1\n{keyId}\n{publicKey}");
        var body = new
        {
            keyId, publicKeyBase64 = publicKey,
            signatureBase64 = Convert.ToBase64String(factory.Sign(signedBytes)),
            signingKeyId = "integration-key"
        };

        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", ApiFactory.AdminKey);
        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.PutAsJsonAsync("/api/admin/remote-access/key", body)).StatusCode);

        JsonElement current = await admin.GetFromJsonAsync<JsonElement>("/api/admin/remote-access/key");
        Assert.Equal(keyId, current.GetProperty("keyId").GetString());
        Assert.Equal(publicKey, current.GetProperty("publicKeyBase64").GetString());

        var tampered = new
        {
            keyId = Guid.NewGuid().ToString("D"), publicKeyBase64 = publicKey,
            signatureBase64 = body.signatureBase64, signingKeyId = body.signingKeyId
        };
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PutAsJsonAsync("/api/admin/remote-access/key", tampered)).StatusCode);
    }
}
