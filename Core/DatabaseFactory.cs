using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Ekom.Payments;

public interface IDatabaseFactory
{
    DbContext GetDatabase();
    SqlConnection GetSqlConnection();
}

class DatabaseFactory : IDatabaseFactory
{
    readonly string _connectionString;
    readonly string _providerName;

    public DatabaseFactory(IConfiguration configuration)
    {
        _connectionString = UmbracoDatabaseConfiguration.GetConnectionString(configuration);
        _providerName = UmbracoDatabaseConfiguration.GetLinqToDbProviderName(configuration);
    }

    public DbContext GetDatabase() => new DbContext(_providerName, _connectionString);
    public SqlConnection GetSqlConnection() => new SqlConnection(_connectionString);
}
