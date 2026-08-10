using HonestLicenseServer.Data;
using HonestLicenseServer.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;
using Xunit;

namespace HonestLicenseServer.IntegrationTests;

public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string ActiveAccessToken = "integration-active-access";
    public const string PendingAccessToken = "integration-pending-access";
    public const string AdminKey = "integration-admin-key";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"honest-api-tests-{Guid.NewGuid():N}");
    private string? _originalConnectionString;
    private string? _originalAdminKey;
    private string? _originalSigningKey;
    private readonly ECDsa _signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private string DatabasePath => Path.Combine(_directory, "integration.db");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _originalConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");
        _originalAdminKey = Environment.GetEnvironmentVariable("AdminApi__Key");
        _originalSigningKey = Environment.GetEnvironmentVariable("LicenseSigningKeys__integration-key__PublicKeyBase64");
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection",
            $"Data Source={DatabasePath};Pooling=False");
        Environment.SetEnvironmentVariable("AdminApi__Key", AdminKey);
        Environment.SetEnvironmentVariable("LicenseSigningKeys__integration-key__PublicKeyBase64",
            Convert.ToBase64String(_signingKey.ExportSubjectPublicKeyInfo()));
        var options = new DbContextOptionsBuilder<HonestDbContext>()
            .UseSqlite($"Data Source={DatabasePath};Pooling=False").Options;
        await using var db = new HonestDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var now = DateTime.UtcNow;
        var client = new Client
        {
            ExternalClientId = "integration-client", Name = "Integration Client",
            Architecture = "x64", IsActive = true, HasLmDatabaseBackup = true,
            CreatedAtUtc = now, UpdatedAtUtc = now
        };
        var device = new Device
        {
            Client = client, ExternalDeviceId = "integration-device", Name = "Test Device",
            Address = "Test address", Status = "Active", RegisteredAtUtc = now
        };
        db.AddRange(client, device);
        await db.SaveChangesAsync();

        db.AppVersions.Add(new AppVersion
        {
            Application = "HonestFlow", CurrentVersion = "2.6.2.0", ImportedAtUtc = now
        });
        db.ComponentAssets.AddRange(
            new ComponentAsset
            {
                Component = "HonestFlow", Version = "2.6.2.0",
                FileName = "HonestFlow-2.6.2.0.zip", DownloadUrl = "https://example.test/global",
                Sha256 = new string('a', 64), SizeBytes = 100, UpdatedAtUtc = now
            },
            new ComponentAsset
            {
                Component = "HonestFlow", Version = "2.5.0",
                FileName = "HonestFlow-2.5.0.zip", DownloadUrl = "https://example.test/override",
                Sha256 = new string('b', 64), SizeBytes = 90, UpdatedAtUtc = now
            });
        db.ClientComponentVersions.Add(new ClientComponentVersion
        {
            ClientId = client.Id, Component = "HonestFlow", RequiredVersion = "2.5.0", UpdatedAtUtc = now
        });
        db.RefreshTokens.AddRange(
            Session(client.Id, device.Id, device.ExternalDeviceId, ActiveAccessToken, now),
            Session(client.Id, null, "pending-device", PendingAccessToken, now));
        db.DeviceRegistrationRequests.Add(new DeviceRegistrationRequest
        {
            ClientId = client.Id, ExternalDeviceId = "pending-device", RequestedName = "Pending Device",
            Status = "Pending", RequestedAtUtc = now
        });
        await db.SaveChangesAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = $"Data Source={DatabasePath};Pooling=False",
                ["AdminApi:Key"] = AdminKey
            }));
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", _originalConnectionString);
        Environment.SetEnvironmentVariable("AdminApi__Key", _originalAdminKey);
        Environment.SetEnvironmentVariable("LicenseSigningKeys__integration-key__PublicKeyBase64", _originalSigningKey);
        _signingKey.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    public byte[] Sign(byte[] grantBytes) => _signingKey.SignData(grantBytes,
        HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    private static RefreshToken Session(int clientId, int? deviceId, string externalDeviceId,
        string accessToken, DateTime now) => new()
    {
        ClientId = clientId, DeviceId = deviceId, RequestedExternalDeviceId = externalDeviceId,
        AccessTokenHash = TokenHelper.Hash(accessToken), AccessTokenExpiresAtUtc = now.AddHours(1),
        TokenHash = TokenHelper.Hash($"refresh-{accessToken}"), TokenFamilyId = Guid.NewGuid().ToString(),
        CreatedAtUtc = now, ExpiresAtUtc = now.AddDays(1)
    };
}
