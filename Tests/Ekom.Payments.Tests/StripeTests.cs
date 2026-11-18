using Ekom.Models.Payments;
using Ekom.Payments.Stripe;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Specialized;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Xunit;
using StripePayment = Ekom.Payments.Stripe.Payment;

namespace Ekom.Payments.Tests;

/// <summary>
/// Stripe takes the payment on its Checkout page and reports what happened by webhook. A signed event is
/// recorded on the Ekom order as what it means for the order; an event that is not signed records nothing.
/// </summary>
public sealed class StripeTests
{
    const string Secret = "whsec_test_4eC39HqLyjWDarjtT1zdp7dc";

    [Fact]
    public async Task RequestCreatesACheckoutSessionAndSendsTheCustomerToIt()
    {
        var site = new ProviderTestSite { ProviderNode = Node() };
        var stripe = new StripeApi();

        var html = await Provider(site, stripe).RequestAsync(site.Settings(1990));

        var payment = Assert.Single(site.Payments.Payments);
        Assert.Equal("stripe", payment.ProviderType);
        Assert.Equal(1990m, payment.Amount);

        Assert.Equal("https://api.stripe.com/v1/checkout/sessions", stripe.Url);
        Assert.Equal("Bearer sk_test_123", stripe.Authorization);
        Assert.Equal(payment.PaymentId.ToString(), stripe.IdempotencyKey);

        var form = stripe.Form!;
        Assert.Equal("payment", form["mode"]);
        Assert.Equal(payment.PaymentId.ToString(), form["client_reference_id"]);
        Assert.Equal(payment.PaymentId.ToString(), form["metadata[ekomPaymentId]"]);
        Assert.Equal(payment.PaymentId.ToString(), form["payment_intent_data[metadata][ekomPaymentId]"]);
        Assert.Equal("https://shop.example/cancel", form["cancel_url"]);
        Assert.StartsWith("https://shop.example/receipt?", form["success_url"]);
        Assert.Contains("reference=" + payment.PaymentId, form["success_url"]);
        Assert.Contains("orderId=" + site.OrderId, form["success_url"]);
        // Stripe takes ISK with two decimals.
        Assert.Equal("isk", form["line_items[0][price_data][currency]"]);
        Assert.Equal("199000", form["line_items[0][price_data][unit_amount]"]);
        Assert.Equal("1", form["line_items[0][quantity]"]);
        Assert.Equal("Item", form["line_items[0][price_data][product_data][name]"]);
        Assert.Null(form["payment_intent_data[capture_method]"]);
        // Stripe has no Icelandic Checkout page: it follows the customer's browser.
        Assert.Null(form["locale"]);

        Assert.Contains("https://checkout.stripe.com/c/pay/cs_test_1#fid", html);
    }

    [Fact]
    public async Task TheSecretKeyIsNotStoredWithThePayment()
    {
        var site = new ProviderTestSite { ProviderNode = Node() };

        await Provider(site, new StripeApi()).RequestAsync(site.Settings(1990));

        var payment = Assert.Single(site.Payments.Payments);
        Assert.DoesNotContain("sk_test_123", payment.ProviderData);
        Assert.DoesNotContain("sk_test_123", payment.Settings);
        // The webhook is verified with what was stored.
        Assert.Equal(Secret, payment.GetProviderData<StripeSettings>()!.WebhookSecret);
    }

    [Fact]
    public async Task OrderLinesAreListedWhenTheyAddUpToThePaymentOtherwiseTheOrderIsOneLine()
    {
        var site = new ProviderTestSite { ProviderNode = Node(x => x.UseDelayedCapture = true) };
        var stripe = new StripeApi();
        var settings = site.Settings(0);
        settings.Currency = "EUR";
        settings.Language = "da-DK";
        settings.Orders =
        [
            new OrderItem { Title = "Blue dress", Price = 10.25m, GrandTotal = 20.50m, Quantity = 2 },
            new OrderItem { Title = "Shirt", Price = 9.40m, GrandTotal = 9.40m, Quantity = 1 },
        ];

        await Provider(site, stripe).RequestAsync(settings);

        Assert.Equal("Blue dress × 2", stripe.Form!["line_items[0][price_data][product_data][name]"]);
        Assert.Equal("2050", stripe.Form["line_items[0][price_data][unit_amount]"]);
        Assert.Equal("Shirt", stripe.Form["line_items[1][price_data][product_data][name]"]);
        Assert.Equal("940", stripe.Form["line_items[1][price_data][unit_amount]"]);
        Assert.Equal("eur", stripe.Form["line_items[1][price_data][currency]"]);
        Assert.Equal("manual", stripe.Form["payment_intent_data[capture_method]"]);
        Assert.Equal("da", stripe.Form["locale"]);

        // A discount line cannot be a Stripe line item: the customer is asked for the order's total.
        settings = site.Settings(0);
        settings.Orders =
        [
            new OrderItem { Title = "Blue dress", Price = 2000, GrandTotal = 2000, Quantity = 1 },
            new OrderItem { Title = "Discount", Price = -10, GrandTotal = -10, Quantity = 1 },
        ];

        await Provider(site, stripe).RequestAsync(settings);

        Assert.Equal("199000", stripe.Form!["line_items[0][price_data][unit_amount]"]);
        Assert.Equal("Order", stripe.Form["line_items[0][price_data][product_data][name]"]);
        Assert.Null(stripe.Form["line_items[1][price_data][unit_amount]"]);
    }

    [Fact]
    public async Task NoPaymentIsStartedWithoutAWebhookSecretOrWhenStripeRefuses()
    {
        var keyless = new ProviderTestSite { ProviderNode = Node(x => x.WebhookSecret = null) };
        var stripe = new StripeApi();

        await Assert.ThrowsAsync<ArgumentException>(() => Provider(keyless, stripe).RequestAsync(keyless.Settings(1990)));
        Assert.Empty(keyless.Payments.Payments);
        Assert.Null(stripe.Url);

        var site = new ProviderTestSite { ProviderNode = Node() };
        stripe.Status = HttpStatusCode.Unauthorized;

        await Assert.ThrowsAsync<HttpRequestException>(() => Provider(site, stripe).RequestAsync(site.Settings(1990)));
    }

    [Theory]
    [InlineData("ISK", 1990, 199000)]
    [InlineData("ISK", 1989.6, 199000)]
    [InlineData("EUR", 19.90, 1990)]
    [InlineData("usd", 0.5, 50)]
    [InlineData("JPY", 500, 500)]
    [InlineData("KWD", 5.678, 5680)]
    public void AmountsAreSentInTheMinorUnitStripeExpectsOfTheCurrency(string currency, decimal amount, long expected)
        => Assert.Equal(expected, StripeAmount.ToMinorUnits(amount, currency));

    [Fact]
    public async Task PaidCheckoutIsThePaymentHoweverOftenStripeDeliversIt()
    {
        var site = new ProviderTestSite();
        var payment = await CreateAsync(site);

        Assert.IsType<OkResult>(await PostAsync(site, Session(payment.PaymentId, "checkout.session.completed", "paid")));
        Assert.IsType<OkResult>(await PostAsync(site, Session(payment.PaymentId, "checkout.session.completed", "paid")));

        var capture = Assert.Single(site.Payments.Recorded);
        Assert.Equal(PaymentTransactionType.Captured, capture.Type);
        Assert.Null(capture.Amount); // the payment's own amount
        Assert.Equal("pi_1", capture.TransactionId);
        Assert.Contains(capture.Details!, x => x.Label == "Checkout session" && x.Value == "cs_test_1");
        Assert.Contains(capture.Details!, x => x.Label == "Stripe mode" && x.Value == "Test");
        Assert.DoesNotContain("customer@example.com", capture.RawData);
    }

    [Fact]
    public async Task DelayedCaptureRecordsTheAuthorizationFirstAndTheCaptureWhenItHappens()
    {
        var site = new ProviderTestSite();
        var payment = await CreateAsync(site, new StripeSettings { WebhookSecret = Secret, UseDelayedCapture = true });

        Assert.IsType<OkResult>(await PostAsync(site, Session(payment.PaymentId, "checkout.session.completed", "unpaid")));
        Assert.IsType<OkResult>(await PostAsync(site, Charge(payment.PaymentId, "charge.captured", amountCaptured: 150000)));

        Assert.Equal(
            new[] { PaymentTransactionType.Authorized, PaymentTransactionType.Captured },
            site.Payments.Recorded.Select(x => x.Type));
        var capture = site.Payments.Recorded[^1];
        Assert.Equal(1500m, capture.Amount);
        Assert.Equal("pi_1", capture.TransactionId);
        Assert.Equal("visa", capture.CardBrand);
        Assert.Equal("****4242", capture.MaskedCardNumber);
    }

    [Fact]
    public async Task PaymentMethodThatConfirmsLaterIsRecordedWhenStripeKnowsItsOutcome()
    {
        var site = new ProviderTestSite();
        var succeeds = await CreateAsync(site);
        var fails = await CreateAsync(site);

        Assert.IsType<OkResult>(await PostAsync(site, Session(succeeds.PaymentId, "checkout.session.completed", "unpaid")));
        Assert.Empty(site.Payments.Recorded);

        Assert.IsType<OkResult>(await PostAsync(site, Session(succeeds.PaymentId, "checkout.session.async_payment_succeeded", "paid")));
        Assert.IsType<OkResult>(await PostAsync(site, Session(fails.PaymentId, "checkout.session.async_payment_failed", "unpaid")));

        Assert.Single(site.Payments.Captures(succeeds.PaymentId));
        Assert.Equal(PaymentTransactionType.Failed, site.Payments.Recorded.Single(x => x.PaymentId == fails.PaymentId).Type);
    }

    [Fact]
    public async Task CheckoutThatExpiresUnpaidIsRecordedAsCancelled()
    {
        var site = new ProviderTestSite();
        var payment = await CreateAsync(site);

        Assert.IsType<OkResult>(await PostAsync(site, Session(payment.PaymentId, "checkout.session.expired", "unpaid", paymentIntent: null)));

        Assert.Equal(PaymentTransactionType.Cancelled, Assert.Single(site.Payments.Recorded).Type);
    }

    [Fact]
    public async Task RefundsMadeAtStripeAreRecordedWithTheAmountOfEachRefund()
    {
        var site = new ProviderTestSite();
        var payment = await CreateAsync(site);
        await PostAsync(site, Session(payment.PaymentId, "checkout.session.completed", "paid"));

        var partial = Charge(payment.PaymentId, "charge.refunded", amountRefunded: 50000, previousAmountRefunded: 0);
        Assert.IsType<OkResult>(await PostAsync(site, partial));
        Assert.IsType<OkResult>(await PostAsync(site, partial));
        Assert.IsType<OkResult>(await PostAsync(site,
            Charge(payment.PaymentId, "charge.refunded", amountRefunded: 199000, previousAmountRefunded: 50000)));

        var refunds = site.Payments.Recorded.Where(x => x.Type == PaymentTransactionType.Refunded).ToList();
        Assert.Equal(new decimal?[] { 500m, 1490m }, refunds.Select(x => x.Amount));
    }

    [Fact]
    public async Task ReleasedOrExpiredAuthorizationIsRecordedAsVoided()
    {
        var site = new ProviderTestSite();
        var released = await CreateAsync(site, new StripeSettings { WebhookSecret = Secret, UseDelayedCapture = true });
        var expired = await CreateAsync(site, new StripeSettings { WebhookSecret = Secret, UseDelayedCapture = true });

        Assert.IsType<OkResult>(await PostAsync(site,
            Charge(released.PaymentId, "charge.refunded", captured: false, amountRefunded: 199000, previousAmountRefunded: 0)));
        Assert.IsType<OkResult>(await PostAsync(site, Charge(expired.PaymentId, "charge.expired", captured: false)));

        Assert.All(site.Payments.Recorded, x => Assert.Equal(PaymentTransactionType.Voided, x.Type));
        Assert.Equal(2, site.Payments.Recorded.Count);
    }

    [Fact]
    public async Task UnverifiedWebhookRecordsNothing()
    {
        var site = new ProviderTestSite();
        var payment = await CreateAsync(site);
        var paid = Session(payment.PaymentId, "checkout.session.completed", "paid");
        var now = DateTimeOffset.UtcNow;

        // Signed with another secret, signed too long ago, changed after signing, and not signed at all.
        Assert.IsNotType<OkResult>(await PostAsync(site, paid, Sign(paid, "whsec_other", now)));
        Assert.IsNotType<OkResult>(await PostAsync(site, paid, Sign(paid, Secret, now.AddMinutes(-10))));
        Assert.IsNotType<OkResult>(await PostAsync(site, paid, Sign(paid.Replace("cs_test_1", "cs_test_2"), Secret, now)));
        Assert.IsNotType<OkResult>(await PostAsync(site, paid, "t=1,v0=abc"));
        Assert.IsNotType<OkResult>(await PostAsync(site, paid, ""));
        Assert.IsNotType<OkResult>(await PostAsync(site, "not json", Sign("not json", Secret, now)));

        Assert.Empty(site.Payments.Recorded);
    }

    [Fact]
    public async Task SignatureOfARolledSecretIsAcceptedNextToTheOldOne()
    {
        var site = new ProviderTestSite();
        var payment = await CreateAsync(site);
        var paid = Session(payment.PaymentId, "checkout.session.completed", "paid");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var header = $"t={timestamp},v1={new string('0', 64)},v1={Hmac(paid, timestamp, Secret)},v0=ignored,destination=we_1";

        Assert.IsType<OkResult>(await PostAsync(site, paid, header));
        Assert.Single(site.Payments.Captures(payment.PaymentId));
    }

    [Fact]
    public async Task DoesNotConfirmPaymentsWithoutAWebhookSecretOrOfOtherProviders()
    {
        var site = new ProviderTestSite();
        var keyless = await CreateAsync(site, new StripeSettings { WebhookSecret = "" });
        // Shares no settings with Stripe, apart from the name of the secret
        var other = await site.Payments.CreateAsync(site.Settings(1990), "otherprovider", 1990m, new { WebhookSecret = Secret });

        Assert.IsNotType<OkResult>(await PostAsync(site, Session(keyless.PaymentId, "checkout.session.completed", "paid")));
        Assert.IsType<NotFoundResult>(await PostAsync(site, Session(other.PaymentId, "checkout.session.completed", "paid")));
        Assert.IsType<NotFoundResult>(await PostAsync(site, Session(Guid.NewGuid(), "checkout.session.completed", "paid")));
        Assert.Empty(site.Payments.Recorded);
    }

    [Fact]
    public async Task EventsThatAreNotAboutAnEkomPaymentAreAcknowledgedAndIgnored()
    {
        var site = new ProviderTestSite();
        var payment = await CreateAsync(site);

        // A payment made at Stripe outside Ekom, and an event Ekom has no use for.
        Assert.IsType<OkResult>(await PostAsync(site, Session(null, "checkout.session.completed", "paid")));
        Assert.IsType<OkResult>(await PostAsync(site, Session(payment.PaymentId, "payment_intent.created", "unpaid")));

        Assert.Empty(site.Payments.Recorded);
    }

    static Action<object?> Node(Action<StripeSettings>? configure = null) => o =>
    {
        if (o is not StripeSettings settings) return;

        settings.SecretKey = "sk_test_123";
        settings.WebhookSecret = Secret;
        configure?.Invoke(settings);
    };

    static StripePayment Provider(ProviderTestSite site, StripeApi stripe) => new(
        NullLogger<StripePayment>.Instance, site, site.Payments, site, stripe, new ConfigurationBuilder().Build());

    static Task<OrderPayment> CreateAsync(ProviderTestSite site, StripeSettings? settings = null)
        => site.Payments.CreateAsync(site.Settings(1990), StripePayment._ppNodeName, 1990m,
            settings ?? new StripeSettings { WebhookSecret = Secret });

    /// <summary>Delivers a webhook the way Stripe does: the event as the body, its signature in a header.</summary>
    static Task<IActionResult> PostAsync(ProviderTestSite site, string payload, string? signature = null)
    {
        var controller = ProviderTestSite.WithRequest(new StripeResponseController(
            NullLogger<StripeResponseController>.Instance, site.Configuration, site.Payments, site));
        controller.Request.Method = "POST";
        controller.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        controller.Request.Headers["Stripe-Signature"] = signature ?? Sign(payload, Secret, DateTimeOffset.UtcNow);

        return controller.Post();
    }

    static string Sign(string payload, string secret, DateTimeOffset signedAt)
    {
        var timestamp = signedAt.ToUnixTimeSeconds().ToString();

        return $"t={timestamp},v1={Hmac(payload, timestamp, secret)}";
    }

    // HMAC SHA256 of "timestamp.payload" with the endpoint's signing secret, as lowercase hex
    static string Hmac(string payload, string timestamp, string secret)
        => Convert.ToHexString(new HMACSHA256(Encoding.UTF8.GetBytes(secret)).ComputeHash(
            Encoding.UTF8.GetBytes($"{timestamp}.{payload}"))).ToLowerInvariant();

    static string Session(Guid? paymentId, string type, string paymentStatus, string? paymentIntent = "pi_1")
        => Event(type, new
        {
            id = "cs_test_1",
            @object = "checkout.session",
            client_reference_id = paymentId?.ToString(),
            metadata = paymentId == null ? new Dictionary<string, string>() : new() { ["ekomPaymentId"] = paymentId.Value.ToString() },
            payment_intent = paymentIntent,
            payment_status = paymentStatus,
            amount_total = 199000,
            currency = "isk",
            customer_details = new { email = "customer@example.com", name = "Jón Jónsson" },
        });

    static string Charge(Guid paymentId, string type, bool captured = true, long amountCaptured = 199000,
        long amountRefunded = 0, long? previousAmountRefunded = null)
        => Event(type, new
        {
            id = "ch_1",
            @object = "charge",
            metadata = new Dictionary<string, string> { ["ekomPaymentId"] = paymentId.ToString() },
            payment_intent = "pi_1",
            amount = 199000,
            amount_captured = captured ? amountCaptured : 0,
            amount_refunded = amountRefunded,
            captured,
            refunded = amountRefunded == 199000,
            currency = "isk",
            payment_method_details = new { type = "card", card = new { brand = "visa", last4 = "4242" } },
        }, previousAmountRefunded == null ? null : new { amount_refunded = previousAmountRefunded });

    static string Event(string type, object stripeObject, object? previousAttributes = null)
        => JsonSerializer.Serialize(new
        {
            id = "evt_1",
            @object = "event",
            api_version = "2025-09-30.clover",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            livemode = false,
            type,
            data = previousAttributes == null
                ? (object)new { @object = stripeObject }
                : new { @object = stripeObject, previous_attributes = previousAttributes },
        });

    /// <summary>Stands in for Stripe's API: keeps the Checkout Session request and answers with a session.</summary>
    sealed class StripeApi : HttpMessageHandler, IHttpClientFactory
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string? Url { get; private set; }
        public string? Authorization { get; private set; }
        public string? IdempotencyKey { get; private set; }
        public NameValueCollection? Form { get; private set; }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Url = request.RequestUri!.ToString();
            Authorization = request.Headers.Authorization?.ToString();
            IdempotencyKey = request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : null;
            Form = HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(cancellationToken));

            return new HttpResponseMessage(Status)
            {
                Content = new StringContent(Status == HttpStatusCode.OK
                    ? "{\"id\":\"cs_test_1\",\"object\":\"checkout.session\",\"url\":\"https://checkout.stripe.com/c/pay/cs_test_1#fid\"}"
                    : "{\"error\":{\"type\":\"invalid_request_error\",\"message\":\"Invalid API Key provided\"}}",
                    Encoding.UTF8, "application/json"),
            };
        }
    }
}
