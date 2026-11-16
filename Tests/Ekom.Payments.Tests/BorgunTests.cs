using Ekom.Models.Payments;
using Ekom.Payments.Borgun;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Net.Mail;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;
using BorgunPayment = Ekom.Payments.Borgun.Payment;
using BorgunResponse = Ekom.Payments.Borgun.Response;

namespace Ekom.Payments.Tests;

/// <summary>
/// Borgun stores its payments on the Ekom order: a verified callback records one capture,
/// anything that cannot be verified records nothing.
/// </summary>
public sealed class BorgunTests
{
    const string Secret = "borgun-test-secret";

    [Fact]
    public async Task RequestStoresThePaymentOnTheOrderAndSendsItsIdAsReference()
    {
        var f = new Fixture();

        var started = await f.RequestAsync(1990, "IS0001");

        Assert.Equal(f.OrderId, started.Payment.OrderUniqueId);
        Assert.Equal("IS0001", started.Payment.OrderNumber);
        Assert.Equal("borgun", started.Payment.ProviderType);
        Assert.Equal(1990m, started.Payment.Amount);
        Assert.Equal(started.Payment.PaymentId.ToString(), started.Form["reference"]);
        Assert.Equal("IS0001", started.Form["orderid"]);
        Assert.Equal("1990,00", started.Form["amount"]);
        // The secret is needed to verify the callback, card details are never stored.
        Assert.Equal(Secret, started.Payment.GetProviderData<BorgunSettings>()!.SecretCode);
        Assert.DoesNotContain("\"CardNumber\"", started.Payment.Settings, StringComparison.Ordinal);
        Assert.DoesNotContain("CardCVV", started.Payment.Settings, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifiedCallbackRecordsOneCaptureWithCardDetailsHoweverOftenItIsRepeated()
    {
        var f = new Fixture();
        var started = await f.RequestAsync(1990);
        var response = Confirmation(started.Payment.PaymentId, started.Form["orderid"], started.Form["amount"]);

        Assert.IsType<OkResult>(await f.Controller().Post(response));
        Assert.IsType<OkResult>(await f.Controller().Post(response));

        var capture = Assert.Single(f.Payments.Captures(started.Payment.PaymentId));
        Assert.Equal(1990m, capture.Amount);
        Assert.Equal("123456", capture.AuthorizationCode);
        Assert.Equal("****1111", capture.MaskedCardNumber);
        Assert.Contains(capture.Details!, x => x.Label == "Borgun order id" && x.Value == started.Form["orderid"]);
        // The stored response never holds the signature.
        Assert.DoesNotContain(response.OrderHash, capture.RawData!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsThePaymentRequestHashAsOrderHash()
    {
        var f = new Fixture();
        var started = await f.RequestAsync(1990);

        // The payment request is signed for merchantid|returnurlsuccess|returnurlsuccessserver|orderid|amount|currency
        var response = new BorgunResponse
        {
            Reference = started.Payment.PaymentId.ToString(),
            Status = "OK",
            OrderHash = started.Form["checkhash"],
            OrderId = string.Join('|',
                started.Form["merchantid"], started.Form["returnurlsuccess"], started.Form["returnurlsuccessserver"], started.Form["orderid"]),
        };

        Assert.IsType<BadRequestResult>(await f.Controller().Post(response));
        Assert.Empty(f.Payments.Recorded);
    }

    [Fact]
    public async Task RejectsTheOrderHashOfAnotherPayment()
    {
        var f = new Fixture();
        var paid = await f.RequestAsync(1990, "IS0001");
        var unpaid = await f.RequestAsync(1990, "IS0002");

        var response = Confirmation(unpaid.Payment.PaymentId, paid.Form["orderid"], paid.Form["amount"]);

        Assert.IsType<BadRequestResult>(await f.Controller().Post(response));
        Assert.Empty(f.Payments.Recorded);
    }

    [Fact]
    public async Task UnverifiableCallbackIsNotRecordedAsAFailedPaymentAndTheExpectedHashIsNotLogged()
    {
        var f = new Fixture();
        var started = await f.RequestAsync(1990, "IS0001");
        var response = Confirmation(started.Payment.PaymentId, started.Form["orderid"], started.Form["amount"]);
        var expected = response.OrderHash;
        response.OrderHash = new string('0', expected.Length);

        var log = new RecordingLogger();
        Assert.IsType<BadRequestResult>(await f.Controller(log).Post(response));

        Assert.Empty(f.Payments.Recorded);
        Assert.DoesNotContain(log.Messages, x => x.Contains(expected, StringComparison.OrdinalIgnoreCase));
        // The rejection is logged with the order first.
        Assert.Contains(log.Messages, x => x.Contains("OrderNumber: IS0001", StringComparison.Ordinal)
            && x.Contains($"PaymentId: {started.Payment.PaymentId}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DoesNotConfirmPaymentsOfOtherProvidersOrUnknownReferences()
    {
        var f = new Fixture();
        var netgiro = await f.Payments.CreateAsync(f.Settings(1990, "IS0001"), "netgiro", 1990m,
            new BorgunSettings { MerchantId = "9275444", SecretCode = Secret });

        Assert.IsType<NotFoundResult>(await f.Controller().Post(Confirmation(netgiro.PaymentId, "IS0001", "1990,00")));
        Assert.IsType<NotFoundResult>(await f.Controller().Post(Confirmation(Guid.NewGuid(), "IS0001", "1990,00")));
        Assert.IsType<BadRequestResult>(await f.Controller().Post(new BorgunResponse { Reference = "not-a-guid" }));
        Assert.Empty(f.Payments.Recorded);
    }

    [Fact]
    public async Task PaymentStartedBeforeTheUpgradeIsCopiedToEkomWhenItsCallbackArrives()
    {
        var f = new Fixture();
        var settings = f.Settings(1990, "IS0001");
        var legacy = new OrderStatus
        {
            UniqueId = Guid.NewGuid(),
            Amount = 1990m,
            EkomPaymentSettings = settings,
            EkomPaymentProviderData = "{\"MerchantId\":\"9275444\",\"SecretCode\":\"" + Secret + "\",\"ekomPaymentProvider\":\"borgun\"}",
            CustomData = f.OrderId.ToString(),
        };
        f.LegacyOrders.Rows.Add(legacy);

        Assert.IsType<OkResult>(await f.Controller().Post(Confirmation(legacy.UniqueId, "IS0001", "1990,00")));

        var payment = Assert.Single(f.Payments.Payments);
        Assert.Equal(legacy.UniqueId, payment.PaymentId);
        Assert.Equal(f.OrderId, payment.OrderUniqueId);
        Assert.Equal("borgun", payment.ProviderType);
        Assert.Single(f.Payments.Captures(legacy.UniqueId));
    }

    static BorgunResponse Confirmation(Guid reference, string orderId, string amount) => new()
    {
        Reference = reference.ToString(),
        Status = "OK",
        OrderId = orderId,
        AuthorizationCode = "123456",
        CreditCardNumber = "****1111",
        // HMAC SHA256 of orderid|amount|currency
        OrderHash = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes($"{orderId}|{amount}|ISK"))),
    };

    sealed record Started(OrderPayment Payment, Dictionary<string, string> Form);

    sealed class Fixture : IUmbracoService, IHttpContextAccessor, IMailService
    {
        readonly PaymentsConfiguration _settings = new() { SendEmailAlerts = false };

        public Guid OrderId { get; } = Guid.NewGuid();
        public InMemoryOrderPayments Payments { get; } = new();
        public LegacyOrders LegacyOrders { get; } = new();
        public HttpContext? HttpContext { get; set; } = CreateHttpContext();
        public string From { get; set; } = "";
        public string To { get; set; } = "";

        public PaymentSettings Settings(decimal total, string orderNumber = "")
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

        public async Task<Started> RequestAsync(decimal total, string orderNumber = "")
        {
            var settings = Settings(total, orderNumber);
            settings.CustomSettings[typeof(BorgunSettings)] = new BorgunSettings
            {
                MerchantId = "9275444", SecretCode = Secret, PaymentGatewayId = 16,
                PaymentPageUrl = new Uri("https://borgun.example/securepay/"),
            };
            var html = await new BorgunPayment(NullLogger<BorgunPayment>.Instance, _settings, this, Payments, this,
                new ConfigurationBuilder().Build()).RequestAsync(settings);
            var form = Regex.Matches(html, "<input type=\"hidden\" name=\"([^\"]*)\" value=\"([^\"]*)\">")
                .ToDictionary(x => WebUtility.HtmlDecode(x.Groups[1].Value), x => WebUtility.HtmlDecode(x.Groups[2].Value));
            return new Started(Payments.Payments[^1], form);
        }

        public BorgunResponseController Controller(ILogger<BorgunResponseController>? logger = null)
            => new(logger ?? NullLogger<BorgunResponseController>.Instance, _settings, Payments, LegacyOrders, this)
            {
                ControllerContext = new ControllerContext { HttpContext = CreateHttpContext() },
            };

        static HttpContext CreateHttpContext()
        {
            var http = new DefaultHttpContext();
            http.Request.Scheme = "https";
            http.Request.Host = new HostString("shop.example");
            return http;
        }

        public void PopulatePaymentProviderProperties(
            PaymentSettings settings, string ppNodeName, object? customProperties, IEnumerable<PropertyInfo>? customPropertyList)
            => settings.PaymentProviderName = "Card payment";

        public Task SendAsync(MailMessage mailMessage) => Task.CompletedTask;
    }

    /// <summary>The payment rows Borgun used before it recorded payments on the Ekom order.</summary>
    sealed class LegacyOrders : IOrderService
    {
        public List<OrderStatus> Rows { get; } = [];

        public Task<OrderStatus?> GetAsync(Guid id)
            => Task.FromResult(Rows.FirstOrDefault(x => x.UniqueId == id || x.CustomData == id.ToString()));
        public Task<OrderStatus?> GetByCustomAsync(string id) => Task.FromResult(Rows.FirstOrDefault(x => x.CustomData == id));
        public Task<OrderStatus> InsertAsync(decimal total, PaymentSettings paymentSettings, object paymentProviderSettings,
            string? netPaymentData, HttpContext httpContext) => throw new NotSupportedException();
        public Task UpdateAsync(OrderStatus orderStatus) => throw new NotSupportedException();
    }

    sealed class RecordingLogger : ILogger<BorgunResponseController>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
