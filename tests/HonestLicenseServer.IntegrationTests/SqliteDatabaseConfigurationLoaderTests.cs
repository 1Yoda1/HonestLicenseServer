using HonestLicenseServer.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace HonestLicenseServer.IntegrationTests;

public sealed class SqliteDatabaseConfigurationLoaderTests
{
    [Fact]
    public void Production_check_does_not_leave_a_read_only_shared_cache_for_the_working_connection()
    {
        string databasePath = Path.Combine(Path.GetTempPath(), $"honest-sqlite-{Guid.NewGuid():N}.db");
        try
        {
            using (var setup = new SqliteConnection($"Data Source={databasePath}"))
            {
                setup.Open();
                using var command = setup.CreateCommand();
                command.CommandText = "CREATE TABLE Clients (Id INTEGER PRIMARY KEY);";
                command.ExecuteNonQuery();
            }

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["HONESTSERVER_SQLITE_PATH"] = databasePath
                })
                .Build();

            SqliteDatabaseConfiguration result = SqliteDatabaseConfigurationLoader.Load(
                configuration, isProduction: true, AppContext.BaseDirectory);

            Assert.Contains("Mode=ReadWrite", result.ConnectionString);
            Assert.DoesNotContain("Cache=Shared", result.ConnectionString);

            using var workingSession = new SqliteConnection(result.ConnectionString);
            workingSession.Open();
            using var write = workingSession.CreateCommand();
            write.CommandText = "CREATE TABLE WriteProbe (Id INTEGER PRIMARY KEY);";
            Assert.Equal(0, write.ExecuteNonQuery());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
            File.Delete(databasePath + "-wal");
            File.Delete(databasePath + "-shm");
        }
    }
}
