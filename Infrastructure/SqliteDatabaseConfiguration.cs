using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace HonestLicenseServer.Infrastructure;

public sealed record SqliteDatabaseConfiguration(string ConnectionString, string DataSource);

public static class SqliteDatabaseConfigurationLoader
{
    public static SqliteDatabaseConfiguration Load(
        IConfiguration configuration,
        bool isProduction,
        string applicationBaseDirectory)
    {
        var configuredPath = configuration["HONESTSERVER_SQLITE_PATH"];
        SqliteConnectionStringBuilder connectionStringBuilder;

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (!Path.IsPathFullyQualified(configuredPath))
                throw new InvalidOperationException(
                    "HONESTSERVER_SQLITE_PATH must be an absolute SQLite database path.");

            connectionStringBuilder = new SqliteConnectionStringBuilder
            {
                DataSource = configuredPath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Shared
            };
        }
        else
        {
            var connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("Не задана строка подключения DefaultConnection");
            connectionStringBuilder = new SqliteConnectionStringBuilder(connectionString);

            if (!Path.IsPathFullyQualified(connectionStringBuilder.DataSource))
            {
                if (isProduction)
                    throw new InvalidOperationException(
                        "Production SQLite database path must be absolute. Set HONESTSERVER_SQLITE_PATH.");

                connectionStringBuilder.DataSource = Path.GetFullPath(
                    connectionStringBuilder.DataSource, applicationBaseDirectory);
            }
        }

        if (isProduction)
        {
            if (!File.Exists(connectionStringBuilder.DataSource))
                throw new InvalidOperationException(
                    $"Production SQLite database was not found: {connectionStringBuilder.DataSource}");

            var readOnlyConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = connectionStringBuilder.DataSource,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared
            };
            using var connection = new SqliteConnection(readOnlyConnectionString.ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT 1
                FROM sqlite_master
                WHERE type = 'table' AND name = 'Clients'
                LIMIT 1;
                """;
            if (command.ExecuteScalar() is null)
                throw new InvalidOperationException(
                    $"Production SQLite database is missing the required Clients table: {connectionStringBuilder.DataSource}");

            // Do not permit a typo or an unavailable mount to create a new empty database.
            connectionStringBuilder.Mode = SqliteOpenMode.ReadWrite;
        }

        return new SqliteDatabaseConfiguration(
            connectionStringBuilder.ConnectionString,
            connectionStringBuilder.DataSource);
    }
}
