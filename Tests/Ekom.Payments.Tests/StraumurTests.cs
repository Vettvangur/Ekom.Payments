using Ekom.Models.Payments;
using Ekom.Payments.Straumur;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using StraumurPayment = Ekom.Payments.Straumur.Payment;
using StraumurResponse = Ekom.Payments.Straumur.Response;

namespace Ekom.Payments.Tests;

/// <summary>
/// Straumur reports payment events by webhook. A verified event is recorded on the Ekom order as what it
/// means for the order (authorized, captured, failed, refunded); an unverified one records nothing.
/// </summary>
public sealed class StraumurTests
{
    const string Key = "a1b2c3d4e5f60718293a4b5c6d7e8f90";

    [Fact]
    public async Task AuthorizationIsThePaymentWhenStraumurCapturesStraightAway()
    {
        var site = new ProviderTestSite();
        var payment = await CreateAsync(site, new StraumurSettings { HmacKey = Key });

        Assert.IsType<OkResult>(await Controller(site).Post(Event(payment.PaymentId, PaymentEvent.Authorization)));
        // The capture event that follows is the same payment.
        Assert.IsType<OkResult>(await Controller(site).Post(Event(payment.PaymentId, PaymentEvent.Capture, payfacReference: "payfac-2")));

        var capture = Assert.Single(site.Payments.Recorded);
        Assert.Equal(PaymentTransactionType.Captured, capture.Type);
        Assert.Equal(1990m, capture.Amount);
        Assert.Equal("payfac-1", capture.TransactionId);
        Assert.Equal("383528", capture.AuthorizationCode);
        Assert.Equal("****1111", capture.MaskedCardNumber);
        Assert.Contains(capture.Details!, x => x.Label == "Checkout reference" && x.Value == "checkout-1");
    }

    [Fact]
    public async Task DelayedCaptureRecordsTheAuthorizationFirstAndTheCaptureWhenItHappens()
    {
        var site = new ProviderTestSite();
        var payment = await CreateAsync(site, new StraumurSettings { HmacKey = Key, UseDelayedCapture = true });

        Assert.IsType<OkResult>(await Controller(site).Post(Event(payment.PaymentId, PaymentEvent.Authorization)));
        Assert.IsType<OkResult>(await Controller(site).Post(Event(payment.PaymentId, PaymentEvent.Authorization)));
        Assert.IsType<OkResult>(await Controller(site).Post(Event(payment.PaymentId, PaymentEvent.Capture, payfacReference: "payfac-2")));

        Assert.Equal(
            new[] { PaymentTransactionType.Authorized, PaymentTransactionType.Captured },
            site.Payments.Recorded.Select(x => x.Type));
    }

    [Fact]
    public async Task DeclinedPaymentIsRecordedAsFailedWithStraumursReason()
    {
        var site = new ProviderTestSite();
        var payment = await CreateAsync(site, new StraumurSettings { HmacKey = Key });

        var result = await Controller(site).Post(Event(payment.PaymentId, PaymentEvent.Authorization, success: "false", reason: "Refused"));

        Assert.IsType<OkObjectResult>(result);
        var failed = Assert.Single(site.Payments.Recorded);
        Assert.Equal(PaymentTransactionType.Failed, failed.Type);
        Assert.Equal("Refused", failed.Message);
    }

    [Fact]
    public async Task RefundMadeAtStraumurIsRecordedWithItsAmount()
    {
        var site = new ProviderTestSite();
        var payment = await CreateAsync(site, new StraumurSettings { HmacKey = Key });
        await Controller(site).Post(Event(payment.PaymentId, PaymentEvent.Authorization));

        Assert.IsType<OkResult>(await Controller(site).Post(
            Event(payment.PaymentId, PaymentEvent.Refund, payfacReference: "refund-1", amount: "50000")));

        var refund = site.Payments.Recorded[^1];
        Assert.Equal(PaymentTransactionType.Refunded, refund.Type);
        Assert.Equal(500m, refund.Amount);
        Assert.Equal("refund-1", refund.TransactionId);
    }

    [Theory]
    [InlineData(RecurringProccessingModel.Subscription)]
    [InlineData(RecurringProccessingModel.CardOnFile)]
    public async Task RecurringPaymentIsConfirmedByTheEventAfterItsAuthorization(RecurringProccessingModel model)
    {
        var site = new ProviderTestSite();
        var payment = await CreateAsync(site, new StraumurSettings { HmacKey = Key, RecurringProcessingModel = model });

        Assert.IsType<OkResult>(await Controller(site).Post(Event(payment.PaymentId, PaymentEvent.Authorization)));
        Assert.Empty(site.Payments.Recorded);

        Assert.IsType<OkResult>(await Controller(site).Post(Event(payment.PaymentId, PaymentEvent.Tokenization)));
        Assert.Equal(PaymentTransactionType.Captured, Assert.Single(site.Payments.Recorded).Type);
    }

    [Fact]
    public async Task ReferenceWithItemNamesStillFindsThePayment()
    {
        var site = new ProviderTestSite();
        var payment = await CreateAsync(site, new StraumurSettings { HmacKey = Key });

        var response = Event(payment.PaymentId, PaymentEvent.Authorization, merchantReference: $"Blue dress;Shirt;{payment.PaymentId}");

        Assert.IsType<OkResult>(await Controller(site).Post(response));
        Assert.Single(site.Payments.Captures(payment.PaymentId));
    }

    [Fact]
    public async Task UnverifiedWebhookRecordsNothingNotEvenAFailure()
    {
        var site = new ProviderTestSite();
        var payment = await CreateAsync(site, new StraumurSettings { HmacKey = Key });
        var forged = Event(payment.PaymentId, PaymentEvent.Authorization, success: "false", reason: "Refused");
        forged.HmacSignature = Convert.ToBase64String(new byte[32]);
        var unsigned = Event(payment.PaymentId, PaymentEvent.Authorization);
        unsigned.Amount = "1";

        Assert.IsNotType<OkResult>(await Controller(site).Post(forged));
        Assert.IsNotType<OkResult>(await Controller(site).Post(unsigned));
        Assert.Empty(site.Payments.Recorded);
    }

    [Fact]
    public async Task DoesNotConfirmPaymentsWithoutAHmacKeyOrOfOtherProviders()
    {
        var site = new ProviderTestSite();
        var keyless = await CreateAsync(site, new StraumurSettings { HmacKey = "" });
        // Shares no settings with Straumur, apart from the name of the key
        var other = await site.Payments.CreateAsync(site.Settings(1990), "otherprovider", 1990m, new { HmacKey = "" });

        Assert.IsNotType<OkResult>(await Controller(site).Post(Event(keyless.PaymentId, PaymentEvent.Authorization, key: "")));
        Assert.IsType<NotFoundResult>(await Controller(site).Post(Event(other.PaymentId, PaymentEvent.Authorization, key: "")));
        Assert.IsType<NotFoundResult>(await Controller(site).Post(Event(Guid.NewGuid(), PaymentEvent.Authorization)));
        Assert.Empty(site.Payments.Recorded);
    }

    [Fact]
    public async Task PaymentStartedBeforeTheUpgradeIsCopiedToEkomWhenItsWebhookArrives()
    {
        var site = new ProviderTestSite();
        var legacy = site.AddLegacyPayment(1990, new StraumurSettings { HmacKey = Key }, "straumur");

        Assert.IsType<OkResult>(await Controller(site).Post(Event(legacy.UniqueId, PaymentEvent.Authorization)));

        var payment = Assert.Single(site.Payments.Payments);
        Assert.Equal(legacy.UniqueId, payment.PaymentId);
        Assert.Equal(site.OrderId, payment.OrderUniqueId);
        Assert.Single(site.Payments.Captures(legacy.UniqueId));
    }

    [Theory]
    [InlineData("ISK", 1990, 199000)]
    [InlineData("ISK", 19.99, 2000)]
    [InlineData("ISK", 19.5, 2000)]
    [InlineData("EUR", 19.99, 1999)]
    [InlineData("EUR", 19.995, 2000)]
    [InlineData("EUR", 19.994, 1999)]
    public async Task RequestSendsAmountsInMinorUnitsWithoutLosingTheDecimals(string currency, decimal price, int expected)
    {
        var site = new ProviderTestSite();
        using var api = new StraumurApi();
        var settings = site.Settings(price);
        settings.Currency = currency;
        settings.CustomSettings[typeof(StraumurSettings)] = new StraumurSettings
        {
            TerminalIdentifier = "terminal-1", ApiKey = "straumur-test-api-key", HmacKey = Key,
            PaymentPageUrl = new Uri("https://straumur.example/api/v1/hostedcheckout"),
        };

        await new StraumurPayment(NullLogger<StraumurPayment>.Instance, site.Configuration, site, site.Payments, site, api,
            new ConfigurationBuilder().Build()).RequestAsync(settings);

        var request = api.Request!.RootElement;
        Assert.Equal(expected, request.GetProperty("amount").GetInt32());
        var item = Assert.Single(request.GetProperty("items").EnumerateArray());
        Assert.Equal(expected, item.GetProperty("unitPrice").GetInt32());
        Assert.Equal(expected, item.GetProperty("amount").GetInt32());
    }

    [Fact]
    public async Task RefundInACurrencyWithDecimalsIsRecordedInTheCurrencysOwnUnit()
    {
        var site = new ProviderTestSite();
        var payment = await CreateAsync(site, new StraumurSettings { HmacKey = Key });
        await Controller(site).Post(Event(payment.PaymentId, PaymentEvent.Authorization));

        var refund = Event(payment.PaymentId, PaymentEvent.Refund, payfacReference: "refund-1", amount: "1999", currency: "EUR");
        Assert.IsType<OkResult>(await Controller(site).Post(refund));

        Assert.Equal(19.99m, site.Payments.Recorded[^1].Amount);
    }

    static Task<OrderPayment> CreateAsync(ProviderTestSite site, StraumurSettings settings)
        => site.Payments.CreateAsync(site.Settings(1990), StraumurPayment._ppNodeName, 1990m, settings);

    static StraumurResponseController Controller(ProviderTestSite site) => ProviderTestSite.WithRequest(new StraumurResponseController(
        NullLogger<StraumurResponseController>.Instance, site.Configuration, site.Payments, site.LegacyOrders, site));

    static StraumurResponse Event(Guid paymentId, PaymentEvent eventType, string success = "true", string? reason = "383528:1111:03/2030",
        string payfacReference = "payfac-1", string amount = "199000", string? merchantReference = null, string key = Key,
        string currency = "ISK")
    {
        var response = new StraumurResponse
        {
            CheckoutReference = "checkout-1",
            PayfacReference = payfacReference,
            MerchantReference = merchantReference ?? paymentId.ToString(),
            Amount = amount,
            Currency = currency,
            Reason = reason,
            Success = success,
            AdditionalData = new PaymentEventDetails { EventType = eventType, CardNumber = "****1111", AuthCode = "383528", Token = "card-token" },
        };
        // HMAC SHA256, hex key, of checkoutReference:payfacReference:merchantReference:amount:currency:reason:success
        response.HmacSignature = Convert.ToBase64String(new HMACSHA256(Convert.FromHexString(key)).ComputeHash(
            Encoding.UTF8.GetBytes(string.Join(':',
                response.CheckoutReference, response.PayfacReference, response.MerchantReference,
                response.Amount, response.Currency, response.Reason, response.Success))));

        return response;
    }

    /// <summary>Stands in for Straumur's hosted checkout API: keeps the payment request and answers with a checkout url.</summary>
    sealed class StraumurApi : HttpMessageHandler, IHttpClientFactory
    {
        public JsonDocument? Request { get; private set; }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"url\":\"https://straumur.example/checkout/1\",\"checkoutReference\":\"checkout-1\"}",
                    Encoding.UTF8, "application/json"),
            };
        }
    }
}
