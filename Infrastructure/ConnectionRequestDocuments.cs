using System.Reflection;

namespace HonestLicenseServer.Infrastructure;

public static class ConnectionRequestDocuments
{
    public const string Version = "1.0";
    public static string PrivacyPolicyText { get; } = Read("policy");
    public static string ConsentText { get; } = Read("consent");

    private static string Read(string name)
    {
        using var stream = typeof(ConnectionRequestDocuments).Assembly.GetManifestResourceStream(
            $"HonestLicenseServer.Privacy.v1.0.{name}.md")
            ?? throw new InvalidOperationException("Bundled privacy document is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
