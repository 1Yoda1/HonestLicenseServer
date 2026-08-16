using Xunit;

namespace HonestLicenseServer.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class IntegrationTestCollection
{
    public const string Name = "HonestLicenseServer API integration";
}
