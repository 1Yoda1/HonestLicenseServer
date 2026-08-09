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
    public DbSet<DeviceRegistrationRequest> DeviceRegistrationRequests => Set<DeviceRegistrationRequest>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Client>().HasIndex(x => x.ExternalClientId).IsUnique();
        b.Entity<Credential>().HasIndex(x => x.Login).IsUnique();
        b.Entity<Device>().HasIndex(x => new { x.ClientId, x.ExternalDeviceId }).IsUnique();
        b.Entity<License>().HasIndex(x => new { x.ClientId, x.DeviceId, x.Revision }).IsUnique();
        b.Entity<AppVersion>().HasIndex(x => x.Application).IsUnique();
        b.Entity<RefreshToken>().HasIndex(x => x.AccessTokenHash).IsUnique();
        b.Entity<RefreshToken>().HasIndex(x => x.TokenHash).IsUnique();

        b.Entity<License>().HasOne(x => x.Client).WithMany(x => x.Licenses)
            .HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<License>().HasOne(x => x.Device).WithMany(x => x.Licenses)
            .HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Device>().HasOne(x => x.Client).WithMany(x => x.Devices)
            .HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<RefreshToken>().HasOne(x => x.Device).WithMany()
            .HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
    }
}
