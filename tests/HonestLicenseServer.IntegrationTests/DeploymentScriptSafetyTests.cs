using Xunit;

namespace HonestLicenseServer.IntegrationTests;

public sealed class DeploymentScriptSafetyTests
{
    [Fact]
    public void ProductionDeployRequiresCleanStableSourceAndServiceRoute()
    {
        string script = File.ReadAllText(RepositoryFile("deploy-production.ps1"));

        Assert.Contains("Get-RemoteStableCommit", script, StringComparison.Ordinal);
        Assert.Contains("Production deploy requires a clean checkout", script, StringComparison.Ordinal);
        Assert.Contains("Production deploy requires the current origin/main commit", script, StringComparison.Ordinal);
        Assert.Contains("swagger_has_required_routes()", script, StringComparison.Ordinal);
        Assert.Contains("/api/configuration/service", script, StringComparison.Ordinal);
        Assert.Contains("Published binary: $binary", script, StringComparison.Ordinal);
    }

    private static string RepositoryFile(string fileName)
    {
        string? directory = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            string candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new FileNotFoundException($"Repository file was not found: {fileName}");
    }
}
