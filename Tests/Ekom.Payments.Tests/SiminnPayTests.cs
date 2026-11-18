using Ekom.Models.Payments;
using Ekom.Payments.Helpers;
using Ekom.Payments.SiminnPay;
using Ekom.Payments.SiminnPay.Exceptions;
using Ekom.Payments.SiminnPay.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;
using SiminnPayment = Ekom.Payments.SiminnPay.Payment;

namespace Ekom.Payments.Tests;

/// <summary>
/// Síminn Pay notifies about a payment by its own order key. A signed notification is recorded as what it
/// means for the order; an unsigned one records nothing. The secret is made up.
/// </summary>
public sealed class SiminnPayTests
{
    const string Secret = "siminn-test-secret";

    [Fact]
    public async Task SuccessfulPaymentIsFoundByOrderKeyAndRecordedOnce()
    {
        var (site, payment, orderKey) = await CreateAsync();
        var notification = Notification(orderKey, SiminnPayStatus.PaymentSuccessful);

        Assert.IsType<OkResult>(await Controller(site).Post(notification));
        Assert.IsType<OkResult>(await Controller(site).Post(notification));

        var capture = Assert.Single(site.Payments.Recorded);
        Assert.Equal(payment.PaymentId, capture.PaymentId);
        Assert.Equal(PaymentTransactionType.Captured, capture.Type);
        Assert.Equal("siminn-tx-1", capture.TransactionId);
        Assert.Contains(capture.Details!, x => x.Label == "Síminn Pay order key" && x.Value == orderKey.ToString());
    }

    [Theory]
    [InlineData(SiminnPayStatus.CancelledByCustomer, PaymentTransactionType.Cancelled)]
    [InlineData(SiminnPayStatus.CancelledByMerchant, PaymentTransactionType.Cancelled)]
    [InlineData(SiminnPayStatus.Expired, PaymentTransactionType.Failed)]
    [InlineData(SiminnPayStatus.AcquirerError, PaymentTransactionType.Failed)]
    public async Task CancelledAndFailedAnswersAreRecorded(SiminnPayStatus status, PaymentTransactionType expected)
    {
        var (site, _, orderKey) = await CreateAsync();

        Assert.IsType<OkResult>(await Controller(site).Post(Notification(orderKey, status)));

        var recorded = Assert.Single(site.Payments.Recorded);
        Assert.Equal(expected, recorded.Type);
        Assert.False(string.IsNullOrWhiteSpace(recorded.Message));
    }

    [Fact]
    public async Task WaitingForTheCustomerRecordsNothing()
    {
        var (site, _, orderKey) = await CreateAsync();

        Assert.IsType<OkResult>(await Controller(site).Post(Notification(orderKey, SiminnPayStatus.WaitingForCustomer)));
        Assert.Empty(site.Payments.Recorded);
    }

    [Fact]
    public async Task UnsignedNotificationWrongAmountOrUnknownKeyRecordsNothing()
    {
        var (site, _, orderKey) = await CreateAsync();
        var forged = Notification(orderKey, SiminnPayStatus.PaymentSuccessful);
        forged.HMAC = new string('0', 64);
        var otherAmount = Notification(orderKey, SiminnPayStatus.PaymentSuccessful, amount: 1m);

        Assert.IsType<UnauthorizedResult>(await Controller(site).Post(forged));
        Assert.IsType<BadRequestResult>(await Controller(site).Post(otherAmount));
        Assert.IsType<NotFoundResult>(await Controller(site).Post(Notification(Guid.NewGuid(), SiminnPayStatus.PaymentSuccessful)));
        Assert.Empty(site.Payments.Recorded);
    }

    [Fact]
    public async Task PaymentStartedBeforeTheUpgradeIsFoundByItsOrderKey()
    {
        var site = NewSite();
        var orderKey = Guid.NewGuid();
        var legacy = site.AddLegacyPayment(1990, new SiminnPaySettings(), provider: null);
        legacy.CustomData = orderKey.ToString();

        Assert.IsType<OkResult>(await Controller(site).Post(Notification(orderKey, SiminnPayStatus.PaymentSuccessful)));

        var payment = Assert.Single(site.Payments.Payments);
        Assert.Equal(legacy.UniqueId, payment.PaymentId);
        Assert.Equal(site.OrderId, payment.OrderUniqueId);
        Assert.Single(site.Payments.Captures(legacy.UniqueId));
    }

    [Fact]
    public async Task PaymentTypeTimeToLiveAndCurrencyCanBeSetOnTheProviderNode()
    {
        using var api = new SiminnPayApi();
        var site = NewSite(api, settings =>
        {
            // Like Ekom, the node fills a setting only while it is empty.
            settings.PaymentType ??= "card";
            if (settings.TimeToLive == default) settings.TimeToLive = 300;
            settings.Currency ??= "EUR";
        });

        await RequestAsync(site, new SiminnPaySettings());

        Assert.Equal("card", (string?)api.CreatedOrder!["paymentType"]);
        Assert.Equal(300, (int)api.CreatedOrder["timeToLive"]!);
        Assert.Equal("EUR", (string?)api.CreatedOrder["currency"]);
    }

    [Fact]
    public async Task PaymentTypeTimeToLiveAndCurrencyDefaultWhenNotConfigured()
    {
        using var api = new SiminnPayApi();

        await RequestAsync(NewSite(api), new SiminnPaySettings());

        Assert.Equal("loan", (string?)api.CreatedOrder!["paymentType"]);
        Assert.Equal(60, (int)api.CreatedOrder["timeToLive"]!);
        Assert.Equal("ISK", (string?)api.CreatedOrder["currency"]);
    }

    [Fact]
    public async Task SettingsSetInCodeAreNotReplacedByTheProviderNode()
    {
        using var api = new SiminnPayApi();
        var site = NewSite(api, settings =>
        {
            settings.PaymentType ??= "card";
            if (settings.TimeToLive == default) settings.TimeToLive = 300;
            settings.Currency ??= "EUR";
        });

        await RequestAsync(site, new SiminnPaySettings { PaymentType = "loan", TimeToLive = 120, Currency = "ISK" });

        Assert.Equal("loan", (string?)api.CreatedOrder!["paymentType"]);
        Assert.Equal(120, (int)api.CreatedOrder["timeToLive"]!);
        Assert.Equal("ISK", (string?)api.CreatedOrder["currency"]);
    }

    [Fact]
    public async Task RequestStoresThePaymentAndSendsTheCustomerToTheReceiptPage()
    {
        using var api = new SiminnPayApi();
        var site = NewSite(api);

        Assert.Equal(string.Empty, await RequestAsync(site, new SiminnPaySettings()));

        var payment = Assert.Single(site.Payments.Payments);
        Assert.Equal(api.OrderKey.ToString(), payment.ProviderReference);
        Assert.Contains($"siminnPayOrderKey={api.OrderKey}", site.HttpContext!.Response.Headers.Location.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SiminnPayStatus.WaitingForNewAmount)]
    [InlineData(SiminnPayStatus.Expired)]
    [InlineData(SiminnPayStatus.UnknownError)]
    public async Task NewPaymentOrderThatIsNotWaitingForTheCustomerFailsTheRequest(SiminnPayStatus status)
    {
        using var api = new SiminnPayApi { OrderStatus = status };
        var site = NewSite(api);

        var ex = await Assert.ThrowsAsync<SiminnPayApiResponseException>(() => RequestAsync(site, new SiminnPaySettings()));

        Assert.Contains(status.ToString(), ex.Message, StringComparison.Ordinal);
        Assert.Empty(site.Payments.Recorded);
    }

    [Fact]
    public async Task AmountWithDecimalsIsRequestedAndVerifiedAsAWholeAmount()
    {
        using var api = new SiminnPayApi();
        var site = NewSite(api);

        await RequestAsync(site, new SiminnPaySettings(), total: 1990.5m);
        Assert.Equal(1991m, (decimal)api.CreatedOrder!["amount"]!);

        // A fraction is not covered by the signature, so an amount with one is not accepted.
        Assert.IsType<UnauthorizedResult>(await Controller(site).Post(Notification(api.OrderKey, SiminnPayStatus.PaymentSuccessful, amount: 1990.5m)));
        Assert.IsType<BadRequestResult>(await Controller(site).Post(Notification(api.OrderKey, SiminnPayStatus.PaymentSuccessful, amount: 1990m)));
        Assert.Empty(site.Payments.Recorded);

        Assert.IsType<OkResult>(await Controller(site).Post(Notification(api.OrderKey, SiminnPayStatus.PaymentSuccessful, amount: 1991m)));
        var capture = Assert.Single(site.Payments.Recorded);
        Assert.Equal(PaymentTransactionType.Captured, capture.Type);
        Assert.Equal(1990.5m, capture.Amount);
    }

    [Fact]
    public async Task StatusExpiresAfterTheConfiguredTimeToLive()
    {
        using var api = new SiminnPayApi();
        var site = NewSite(api, settings => settings.TimeToLive = 300);
        var payment = await site.Payments.CreateAsync(site.Settings(1990), "siminnPay", 1990m, providerReference: api.OrderKey.ToString());

        var result = Assert.IsType<JsonResult>(await Controller(site).StatusAsync(api.OrderKey));

        var view = Assert.IsType<SiminnPayStatusView>(result.Value);
        Assert.Equal(SiminnPayStatus.WaitingForCustomer, view.PayStatus);
        Assert.Equal(payment.CreatedUtc.AddSeconds(300), view.Expires);
    }

    [Fact]
    public async Task StatusWithoutAnAnswerFromSiminnPayIsABadGateway()
    {
        using var api = new SiminnPayApi { StatusBody = "null" };
        var site = NewSite(api);
        await site.Payments.CreateAsync(site.Settings(1990), "siminnPay", 1990m, providerReference: api.OrderKey.ToString());

        var result = Assert.IsType<ObjectResult>(await Controller(site).StatusAsync(api.OrderKey));

        Assert.Equal(StatusCodes.Status502BadGateway, result.StatusCode);
    }

    static ProviderTestSite NewSite() => new()
    {
        // Síminn Pay's secret lives on the payment provider node, not with the payment.
        ProviderNode = settings => ((SiminnPaySettings)settings!).Secret = Secret,
    };

    /// <summary>A site whose payment provider node points Siminn Pay at <paramref name="api"/>.</summary>
    static ProviderTestSite NewSite(SiminnPayApi api, Action<SiminnPaySettings>? node = null) => new()
    {
        ProviderNode = o =>
        {
            var settings = (SiminnPaySettings)o!;
            settings.Secret = Secret;
            settings.ApiKey = "siminn-test-key";
            settings.ApiUrl = api.Url;
            node?.Invoke(settings);
        },
    };

    static Task<string> RequestAsync(ProviderTestSite site, SiminnPaySettings settings, decimal total = 1990m)
    {
        var paymentSettings = site.Settings(total);
        paymentSettings.CustomerInfo = new CustomerInfo { PhoneNumber = "5551234" };
        paymentSettings.SetSiminnPaySettings(settings);
        return new SiminnPayment(NullLogger<SiminnPayment>.Instance, site, site.Payments, site).RequestAsync(paymentSettings);
    }

    static async Task<(ProviderTestSite Site, OrderPayment Payment, Guid OrderKey)> CreateAsync()
    {
        var site = NewSite();
        var orderKey = Guid.NewGuid();
        var payment = await site.Payments.CreateAsync(site.Settings(1990), "siminnPay", 1990m, providerReference: orderKey.ToString());
        return (site, payment, orderKey);
    }

    static SiminnPayResponseController Controller(ProviderTestSite site) => ProviderTestSite.WithRequest(new SiminnPayResponseController(
        NullLogger<SiminnPayResponseController>.Instance, site.Configuration, site.Payments, site.LegacyOrders, site, site));

    static SiminnPayOrderStatus Notification(Guid orderKey, SiminnPayStatus status, decimal amount = 1990m)
    {
        var notification = new SiminnPayOrderStatus
        {
            OrderKey = orderKey,
            Amount = amount,
            Status = status,
            ExpiresAt = new DateTime(2026, 11, 13, 12, 0, 0),
            TransactionDetails = new Ekom.Payments.SiminnPay.apimodels.TransactionDetails { TransactionId = "siminn-tx-1" },
        };
        // HMAC SHA256 of orderKey + amount + expiry
        notification.HMAC = CryptoHelpers.GetHMACSHA256(Secret,
            orderKey.ToString() + (int)amount + notification.ExpiresAt.ToString("dd.MM.yyyy HH:mm:ss"));
        return notification;
    }

    /// <summary>Siminn Pay's API on a local port: it hands out a token, creates payment orders and reports their status.</summary>
    sealed class SiminnPayApi : IDisposable
    {
        readonly HttpListener _listener = new();

        public SiminnPayApi()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Url = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public string Url { get; }
        public Guid OrderKey { get; } = Guid.NewGuid();
        public SiminnPayStatus OrderStatus { get; init; } = SiminnPayStatus.WaitingForCustomer;
        public string StatusBody { get; init; } = "{\"status\":201}";

        /// <summary>The payment order the provider asked for.</summary>
        public JObject? CreatedOrder { get; private set; }

        async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }

                var path = context.Request.Url!.AbsolutePath;
                string? body = null;
                if (path.EndsWith("/authentication/accesstoken", StringComparison.Ordinal))
                {
                    body = "{\"token\":\"siminn-test-token\"}";
                }
                else if (path.EndsWith("/paymentorder/order", StringComparison.Ordinal))
                {
                    using var reader = new StreamReader(context.Request.InputStream);
                    CreatedOrder = JObject.Parse(await reader.ReadToEndAsync());
                    body = JsonConvert.SerializeObject(new { orderKey = OrderKey, status = (int)OrderStatus });
                }
                else if (path.EndsWith($"/paymentorder/order/{OrderKey}/status", StringComparison.Ordinal))
                {
                    body = StatusBody;
                }

                if (body == null)
                {
                    context.Response.StatusCode = 404;
                }
                else
                {
                    var bytes = Encoding.UTF8.GetBytes(body);
                    context.Response.ContentType = "application/json";
                    await context.Response.OutputStream.WriteAsync(bytes);
                }
                context.Response.Close();
            }
        }

        public void Dispose() => _listener.Close();
    }
}
