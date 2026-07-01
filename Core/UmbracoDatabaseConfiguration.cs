using Microsoft.Extensions.Configuration;

namespace Ekom.Payments;

internal static class UmbracoDatabaseConfiguration
{
    public const string ConnectionStringName = "umbracoDbDSN";

    public static string? GetConnectionString(IConfiguration configuration)
        => configuration.GetConnectionString(ConnectionStringName);

    public static UmbracoDatabaseProvider GetDatabaseProvider(IConfiguration configuration)
    {
        var providerName = configuration.GetConnectionString($"{ConnectionStringName}_ProviderName");

        if (!string.IsNullOrWhiteSpace(providerName))
        {
            if (providerName.Contains("SqlClient", StringComparison.OrdinalIgnoreCase)
                || providerName.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
            {
                return UmbracoDatabaseProvider.SqlServer;
            }

            if (providerName.Contains("SQLite", StringComparison.OrdinalIgnoreCase)
                || providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                return UmbracoDatabaseProvider.Sqlite;
            }
        }

        var connectionString = GetConnectionString(configuration);

        if (HasAnyKeyword(connectionString, "Initial Catalog", "TrustServerCertificate", "Integrated Security", "User ID"))
        {
            return UmbracoDatabaseProvider.SqlServer;
        }

        if (HasAnyKeyword(connectionString, "Cache", "Mode", "Foreign Keys", "Recursive Triggers")
            || IsFileDataSource(connectionString))
        {
            return UmbracoDatabaseProvider.Sqlite;
        }

        return UmbracoDatabaseProvider.SqlServer;
    }

    public static string GetLinqToDbProviderName(IConfiguration configuration)
        => GetDatabaseProvider(configuration) switch
        {
            UmbracoDatabaseProvider.Sqlite => LinqToDB.ProviderName.SQLiteMS,
            _ => LinqToDB.ProviderName.SqlServer
        };

    public static string GetProviderDisplayName(IConfiguration configuration)
    {
        var providerName = configuration.GetConnectionString($"{ConnectionStringName}_ProviderName");

        if (!string.IsNullOrWhiteSpace(providerName))
        {
            return providerName;
        }

        return GetDatabaseProvider(configuration).ToString();
    }

    static bool HasKeyword(string? connectionString, string keyword)
        => connectionString?
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Split('=', 2)[0].Trim())
            .Any(x => x.Equals(keyword, StringComparison.OrdinalIgnoreCase)) == true;

    static bool HasAnyKeyword(string? connectionString, params string[] keywords)
        => keywords.Any(x => HasKeyword(connectionString, x));

    static bool IsFileDataSource(string? connectionString)
    {
        var dataSource = connectionString?
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Split('=', 2))
            .Where(x => x.Length == 2)
            .FirstOrDefault(x => x[0].Trim().Equals("Data Source", StringComparison.OrdinalIgnoreCase))?[1]
            .Trim();

        return dataSource?.StartsWith("file:", StringComparison.OrdinalIgnoreCase) == true
            || dataSource?.EndsWith(".db", StringComparison.OrdinalIgnoreCase) == true
            || dataSource?.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase) == true
            || dataSource?.EndsWith(".sqlite3", StringComparison.OrdinalIgnoreCase) == true;
    }
}

internal enum UmbracoDatabaseProvider
{
    SqlServer,
    Sqlite
}
