using LinqToDB;
using LinqToDB.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Ekom.Payments.AspNetCore;

class EnsureTablesExist
{
    readonly IDatabaseFactory _databaseFactory;
    readonly IConfiguration _configuration;
    readonly ILogger<EnsureTablesExist> _logger;

    public EnsureTablesExist(
        IDatabaseFactory databaseFactory,
        IConfiguration configuration,
        ILogger<EnsureTablesExist> logger)
    {
        _databaseFactory = databaseFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public void Create()
    {
        using var db = _databaseFactory.GetDatabase();

        switch (UmbracoDatabaseConfiguration.GetDatabaseProvider(_configuration))
        {
            case UmbracoDatabaseProvider.SqlServer:
                CreateSqlServerTables(db);
                break;
            case UmbracoDatabaseProvider.Sqlite:
                CreateSqliteTables(db);
                break;
            default:
                _logger.LogWarning(
                    "EkomPayments table creation does not support database provider {ProviderName}.",
                    UmbracoDatabaseConfiguration.GetProviderDisplayName(_configuration));
                break;
        }
    }

    static void CreateSqlServerTables(DbContext db)
    {
        var sp = db.DataProvider.GetSchemaProvider();
        var dbSchema = sp.GetSchema(db);

        if (!dbSchema.Tables.Any(t => t.TableName == "EkomPaymentOrders"))
        {
            db.CreateTable<OrderStatus>();
            db.Execute($"ALTER TABLE EkomPaymentOrders ALTER COLUMN EkomPaymentSettingsData NVARCHAR(MAX)");
            db.Execute(@"ALTER TABLE EkomPaymentOrders DROP COLUMN PaymentProviderName");
            db.Execute(@"ALTER TABLE EkomPaymentOrders ADD
                PaymentProviderName  AS json_value([EkomPaymentSettingsData],'$.PaymentProviderName')");
            db.Execute(@"ALTER TABLE EkomPaymentOrders DROP COLUMN PaymentProviderKey");
            db.Execute(@"ALTER TABLE EkomPaymentOrders ADD
                PaymentProviderKey  AS json_value([EkomPaymentSettingsData],'$.PaymentProviderKey')");

            db.Execute($"ALTER TABLE dbo.EkomPaymentOrders DROP CONSTRAINT [PK_EkomPaymentOrders]");
            db.Execute($"ALTER TABLE EkomPaymentOrders ADD CONSTRAINT [PK_EkomPaymentOrders] PRIMARY KEY NONCLUSTERED ([ReferenceId] ASC) WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, IGNORE_DUP_KEY = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]");
            db.Execute($"CREATE UNIQUE NONCLUSTERED INDEX [IX_EkomPaymentOrders_UniqueId] ON EkomPaymentOrders ( [UniqueId] ASC )WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, IGNORE_DUP_KEY = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]");
        }

        db.Execute("ALTER TABLE EkomPaymentOrders ALTER COLUMN Amount DECIMAL(18,2) NOT NULL");

        if (!dbSchema.Tables.Any(t => t.TableName == "EkomPayments"))
        {
            db.CreateTable<PaymentData>();
            db.Execute($"ALTER TABLE EkomPayments ALTER COLUMN CustomData NVARCHAR(MAX)");
        }
    }

    static void CreateSqliteTables(DbContext db)
    {
        db.Execute(@"CREATE TABLE IF NOT EXISTS EkomPaymentOrders (
            OrderName TEXT NULL,
            UniqueId TEXT NOT NULL,
            ReferenceId INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            Member TEXT NULL,
            Amount NUMERIC NOT NULL,
            Date TEXT NOT NULL,
            IPAddress TEXT NOT NULL,
            UserAgent TEXT NULL,
            Paid INTEGER NOT NULL,
            EkomPaymentSettingsData TEXT NOT NULL,
            EkomPaymentProviderData TEXT NULL,
            CustomData TEXT NULL,
            PaymentProviderName TEXT GENERATED ALWAYS AS (json_extract(EkomPaymentSettingsData, '$.PaymentProviderName')) VIRTUAL,
            PaymentProviderKey TEXT GENERATED ALWAYS AS (json_extract(EkomPaymentSettingsData, '$.PaymentProviderKey')) VIRTUAL
        )");

        db.Execute("CREATE UNIQUE INDEX IF NOT EXISTS IX_EkomPaymentOrders_UniqueId ON EkomPaymentOrders (UniqueId)");

        db.Execute(@"CREATE TABLE IF NOT EXISTS EkomPayments (
            Id TEXT NOT NULL PRIMARY KEY,
            CardNumber TEXT NULL,
            PaymentMethod TEXT NULL,
            CustomData TEXT NULL,
            Amount TEXT NOT NULL,
            Date TEXT NOT NULL
        )");
    }
}
