using HonestLicenseServer.Models;
using Microsoft.EntityFrameworkCore;

namespace HonestLicenseServer.Data;

public class HonestDbContext(DbContextOptions<HonestDbContext> options) : DbContext(options)
{
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<Credential> Credentials => Set<Credential>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<License> Licenses => Set<License>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<AppVersion> AppVersions => Set<AppVersion>();
    public DbSet<ClientComponentVersion> ClientComponentVersions => Set<ClientComponentVersion>();
    public DbSet<ComponentAsset> ComponentAssets => Set<ComponentAsset>();
    public DbSet<ClientSetting> ClientSettings => Set<ClientSetting>();
    public DbSet<LicensePolicy> LicensePolicies => Set<LicensePolicy>();
    public DbSet<DeviceRegistrationRequest> DeviceRegistrationRequests => Set<DeviceRegistrationRequest>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<SupportRequest> SupportRequests => Set<SupportRequest>();
    public DbSet<ConnectionRequest> ConnectionRequests => Set<ConnectionRequest>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Client>().HasIndex(x => x.ExternalClientId).IsUnique();
        b.Entity<Credential>().HasIndex(x => x.Login).IsUnique();
        b.Entity<Device>().HasIndex(x => new { x.ClientId, x.ExternalDeviceId }).IsUnique();
        b.Entity<License>().HasIndex(x => new { x.ClientId, x.DeviceId, x.Revision }).IsUnique();
        b.Entity<License>().Property(x => x.SignatureScope).HasDefaultValue("LegacySnapshot");
        b.Entity<AppVersion>().HasIndex(x => x.Application).IsUnique();
        b.Entity<ClientComponentVersion>().HasIndex(x => new { x.ClientId, x.Component }).IsUnique();
        b.Entity<ComponentAsset>().HasIndex(x => new { x.Component, x.Version }).IsUnique();
        b.Entity<ClientSetting>().HasKey(x => x.ClientId);
        b.Entity<LicensePolicy>().HasKey(x => x.ClientId);
        b.Entity<RefreshToken>().HasIndex(x => x.AccessTokenHash).IsUnique();
        b.Entity<RefreshToken>().HasIndex(x => x.TokenHash).IsUnique();
        b.Entity<SupportRequest>().HasIndex(x => new { x.Status, x.CreatedAtUtc });
        b.Entity<ConnectionRequest>().HasIndex(x => new { x.Status, x.CreatedAtUtc });

        b.Entity<License>().HasOne(x => x.Client).WithMany(x => x.Licenses)
            .HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<License>().HasOne(x => x.Device).WithMany(x => x.Licenses)
            .HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Device>().HasOne(x => x.Client).WithMany(x => x.Devices)
            .HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<RefreshToken>().HasOne(x => x.Device).WithMany()
            .HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ClientComponentVersion>().HasOne(x => x.Client).WithMany()
            .HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ClientSetting>().HasOne(x => x.Client).WithOne()
            .HasForeignKey<ClientSetting>(x => x.ClientId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<LicensePolicy>().HasOne(x => x.Client).WithOne()
            .HasForeignKey<LicensePolicy>(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<SupportRequest>().HasOne(x => x.Client).WithMany()
            .HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<SupportRequest>().HasOne(x => x.Device).WithMany()
            .HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.SetNull);
    }
}
