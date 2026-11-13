using Ekom.Payments.AltaPay;
using Ekom.Payments.AltaPay.Model;
using Ekom.Payments.Helpers;
using Ekom.Payments.Netgiro;
using Ekom.Payments.Valitor;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using System.Net;
using System.Net.Mail;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Ekom.Payments.Tests;

/// <summary>
/// Payment callbacks are only accepted when they carry the signature the payment provider documents
/// for a confirmed payment of the order they reference. All secrets are made up.
/// </summary>
public sealed class CallbackVerificationTests
{
    const string NetgiroSecret = "netgiro-test-secret";
    const string AltaSecret = "alta-test-secret";
    static readonly Guid NetgiroApplicationId = Guid.Parse("2f1f6c1e-7c4b-4d0c-9a57-3b8d6e241905");

    #region AltaPay

    [Fact]
    public async Task AltaPayAcceptsChecksummedConfirmationWhenAnotherProviderWasTriedFirst()
    {
        await using var f = await Fixture.CreateAsync();
        var netgiro = await f.InsertAsync(1990, new NetgiroSettings { ApplicationId = NetgiroApplicationId, Secret = NetgiroSecret }, "netgiro");
        var alta = await f.InsertAsync(1990, AltaPaySettings(AltaSecret), "altapay");

        var result = await f.AltaPay().Post(AltaPayConfirmation(f.OrderId, AltaChecksum(f.OrderId, "1990.00", AltaSecret)));

        Assert.IsType<RedirectResult>(result);
        Assert.True((await f.Orders.GetAsync(alta.UniqueId))!.Paid);
        Assert.False((await f.Orders.GetAsync(netgiro.UniqueId))!.Paid);
        Assert.Equal(1, f.Completions);
    }

    [Fact]
    public async Task AltaPayRejectsWrongChecksum()
    {
        await using var f = await Fixture.CreateAsync();
        var alta = await f.InsertAsync(1990, AltaPaySettings(AltaSecret), "altapay");

        var result = await f.AltaPay().Post(AltaPayConfirmation(f.OrderId, AltaChecksum(f.OrderId, "1.00", AltaSecret)));

        Assert.IsNotType<RedirectResult>(result);
        Assert.False((await f.Orders.GetAsync(alta.UniqueId))!.Paid);
    }

    [Fact]
    public async Task AltaPayDoesNotConfirmPaymentsOfOtherProviders()
    {
        await using var f = await Fixture.CreateAsync();
        var netgiro = await f.InsertAsync(1990, new NetgiroSettings { ApplicationId = NetgiroApplicationId, Secret = NetgiroSecret }, "netgiro");
        var legacy = await f.InsertAsync(
            1990,
            new NetgiroSettings { ApplicationId = NetgiroApplicationId, Secret = NetgiroSecret },
            provider: null);

        // Neither payment has an AltaPay secret to check a checksum with
        var result = await f.AltaPay().Post(AltaPayConfirmation(f.OrderId, checksum: null));

        Assert.IsType<NotFoundResult>(result);
        Assert.False((await f.Orders.GetAsync(netgiro.UniqueId))!.Paid);
        Assert.False((await f.Orders.GetAsync(legacy.UniqueId))!.Paid);
        Assert.Equal(0, f.Completions);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task AltaPayDoesNotConfirmPaymentsWithoutASharedSecret(string? secret)
    {
        await using var f = await Fixture.CreateAsync();
        var alta = await f.InsertAsync(1990, AltaPaySettings(secret), "altapay");

        // Without a shared secret AltaPay sends no checksum, and one calculated without a secret proves nothing
        foreach (var checksum in new[] { null, AltaChecksum(f.OrderId, "1990.00", secret: "") })
        {
            Assert.IsNotType<RedirectResult>(await f.AltaPay().Post(AltaPayConfirmation(f.OrderId, checksum)));
        }

        Assert.False((await f.Orders.GetAsync(alta.UniqueId))!.Paid);
        Assert.Equal(0, f.Completions);
    }

    [Fact]
    public async Task AltaPayRejectsMissingChecksum()
    {
        await using var f = await Fixture.CreateAsync();
        var alta = await f.InsertAsync(1990, AltaPaySettings(AltaSecret), "altapay");

        Assert.IsNotType<RedirectResult>(await f.AltaPay().Post(AltaPayConfirmation(f.OrderId, checksum: null)));
        Assert.False((await f.Orders.GetAsync(alta.UniqueId))!.Paid);
    }

    static AltaSettings AltaPaySettings(string? secret) => new()
    {
        Terminal = "Test Terminal",
        ApiUserName = "api-user",
        ApiPassword = "not-a-secret",
        BaseAddress = new Uri("https://alta.example/merchant/API/"),
        CustomerInformationSharedSecret = secret,
    };

    static PaymentResponse AltaPayConfirmation(Guid shopOrderId, string? checksum) => new()
    {
        ShopOrderId = shopOrderId,
        Checksum = checksum,
        Xml = WebUtility.UrlEncode(
            "<?xml version=\"1.0\"?><APIResponse version=\"20170228\"><Header><ErrorCode>0</ErrorCode></Header><Body>"
            + "<Result>Success</Result><Transactions><Transaction><TransactionId>1</TransactionId>"
            + $"<ShopOrderId>{shopOrderId}</ShopOrderId><MerchantCurrency>352</MerchantCurrency>"
            + "<MerchantCurrencyAlpha>ISK</MerchantCurrencyAlpha><CreditCardMaskedPan>411111******1111</CreditCardMaskedPan>"
            + "</Transaction></Transactions></Body></APIResponse>"),
    };

    /// <summary>
    /// MD5 of the comma separated key=value pairs, sorted by key
    /// </summary>
    static string AltaChecksum(Guid shopOrderId, string amount, string secret)
        => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(
            $"amount={amount},currency=352,secret={secret},shop_orderid={shopOrderId}"))).ToLowerInvariant();

    #endregion

    #region Stored payment settings

    [Fact]
    public async Task CardDetailsAreNotStoredWithThePayment()
    {
        await using var f = await Fixture.CreateAsync();
        var settings = f.PaymentSettings(1990, orderNumber: "IS0001");
        settings.CardNumber = "4111111111111111";
        settings.CardCVV = "737";
        settings.CardExpirationMonth = 3;
        settings.CardExpirationYear = 2030;
        settings.VirtualCardNumber = "virtual-card-token";

        var order = await f.Orders.InsertAsync(1990, settings, new { }, f.OrderId.ToString(), new DefaultHttpContext());
        var stored = (await f.Orders.GetAsync(order.UniqueId))!;

        Assert.DoesNotContain("4111111111111111", stored.EkomPaymentSettingsData);
        Assert.DoesNotContain("737", stored.EkomPaymentSettingsData);
        Assert.DoesNotContain("Card", string.Join(' ', JObject.Parse(stored.EkomPaymentSettingsData)
            .Properties().Select(x => x.Name).Where(x => x != nameof(PaymentSettings.VirtualCardNumber))));

        // The request still has the card details, everything else is stored as before
        Assert.Equal("4111111111111111", settings.CardNumber);
        var expected = JObject.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(settings));
        foreach (var property in new[] { "CardNumber", "CardCVV", "CardExpirationMonth", "CardExpirationYear" })
        {
            Assert.True(expected.Remove(property));
        }
        Assert.Equal(expected.ToString(Newtonsoft.Json.Formatting.None), stored.EkomPaymentSettingsData);

        var restored = stored.EkomPaymentSettings;
        Assert.Null(restored.CardNumber);
        Assert.Null(restored.CardCVV);
        Assert.Equal("virtual-card-token", restored.VirtualCardNumber);
        Assert.Equal("IS0001", restored.OrderNumber);
        Assert.Equal(f.OrderId.ToString(), restored.OrderCustomData["ekomOrderUniqueId"]);
    }

    #endregion

    static string Sha256Hex(string input)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();

    static string HmacHex(byte[] key, string message)
        => Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(message)));


    sealed class Fixture : IAsyncDisposable, IDatabaseFactory, IUmbracoService, IHttpContextAccessor, IMailService
    {
        readonly string _path = Path.Combine(Path.GetTempPath(), $"callback-test-{Guid.NewGuid():N}.sqlite");
        readonly IConfiguration _configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:umbracoDbDSN_ProviderName"] = "Microsoft.Data.Sqlite",
        }).Build();
        readonly PaymentsConfiguration _settings = new() { SendEmailAlerts = false };

        /// <summary>
        /// The order in the shop, shared by all payment attempts made in a test
        /// </summary>
        public Guid OrderId { get; } = Guid.NewGuid();
        public IOrderService Orders { get; private set; } = null!;
        public int Completions { get; private set; }
        public HttpContext? HttpContext { get; set; } = CreateHttpContext();
        public string From { get; set; } = "";
        public string To { get; set; } = "";

        public static Task<Fixture> CreateAsync()
        {
            var f = new Fixture();
            f.Orders = new OrderService(f._settings, f);
            new EnsureTablesExist(f, f._configuration, NullLogger<EnsureTablesExist>.Instance).Create();
            Events.SuccessAsync += f.CompleteAsync;
            return Task.FromResult(f);
        }

        public PaymentSettings PaymentSettings(decimal total, string orderNumber = "")
        {
            var settings = new PaymentSettings
            {
                OrderUniqueId = OrderId, OrderNumber = orderNumber, OrderName = "Order",
                Orders = [new OrderItem { Title = "Item", Price = total, GrandTotal = total, Quantity = 1 }],
                Currency = "ISK", Store = "test", Language = "is-IS",
                SuccessUrl = new Uri($"https://shop.example/receipt?orderId={OrderId}"),
                CancelUrl = new Uri("https://shop.example/cancel"),
                ErrorUrl = new Uri("https://shop.example/error"),
            };
            settings.OrderCustomData["ekomOrderUniqueId"] = OrderId.ToString();
            return settings;
        }

        /// <summary>
        /// Stores a payment the way a provider does, without contacting the provider
        /// </summary>
        public Task<OrderStatus> InsertAsync(decimal total, object providerSettings, string? provider)
        {
            var stored = JObject.FromObject(providerSettings);
            if (provider != null)
            {
                stored["ekomPaymentProvider"] = provider;
            }
            return Orders.InsertAsync(total, PaymentSettings(total), stored, OrderId.ToString(), new DefaultHttpContext());
        }

        public AltaResponseController AltaPay() => Controller(new AltaResponseController(
            NullLogger<AltaResponseController>.Instance, _settings, Orders, this, this, this));

        static T Controller<T>(T controller) where T : ControllerBase
        {
            controller.ControllerContext = new ControllerContext { HttpContext = CreateHttpContext() };
            return controller;
        }

        static HttpContext CreateHttpContext()
        {
            var http = new DefaultHttpContext();
            http.Request.Scheme = "https";
            http.Request.Host = new HostString("shop.example");
            return http;
        }

        Task CompleteAsync(object sender, SuccessEventArgs args) { Completions++; return Task.CompletedTask; }

        public void PopulatePaymentProviderProperties(
            PaymentSettings settings, string ppNodeName, object? customProperties, IEnumerable<PropertyInfo>? customPropertyList)
        {
            settings.PaymentProviderName = ppNodeName;
        }

        public Task SendAsync(MailMessage mailMessage) => Task.CompletedTask;
        public DbContext GetDatabase() => new(ProviderName.SQLiteMS, $"Data Source={_path};Pooling=False");
        public SqlConnection GetSqlConnection() => throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            Events.SuccessAsync -= CompleteAsync;
            File.Delete(_path);
            return ValueTask.CompletedTask;
        }
    }

    sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
