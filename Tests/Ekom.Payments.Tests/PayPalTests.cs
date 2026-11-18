using Ekom.Models.Payments;
using Ekom.Payments.PayPal;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using PayPalPayment = Ekom.Payments.PayPal.Payment;

namespace Ekom.Payments.Tests;

/// <summary>
/// The customer approves a PayPal order at PayPal and the provider takes the payment when they return,
/// or when PayPal's webhook says the order was approved. What PayPal answers is recorded on the Ekom order;
/// a webhook PayPal does not vouch for records nothing.
/// </summary>
public sealed class PayPalTests
{
    [Fact]
    public async Task RequestCreatesAPayPalOrderAndSendsTheCustomerToApproveIt()
    {
        var site = Site(x => x.ClientId = "client-of-the-request-test");
        var payPal = new PayPalApi();

        var html = await Provider(site, payPal).RequestAsync(Settings(site, 19.90m));

        var payment = Assert.Single(site.Payments.Payments);
        Assert.Equal("payPal", payment.ProviderType);
        Assert.Equal(19.90m, payment.Amount);
        Assert.DoesNotContain("paypal-secret", payment.ProviderData);

        Assert.Contains("POST https://api-m.sandbox.paypal.com/v1/oauth2/token", payPal.Requests);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("client-of-the-request-test:paypal-secret")), payPal.TokenAuthorization);
        Assert.Contains("POST https://api-m.sandbox.paypal.com/v2/checkout/orders", payPal.Requests);
        Assert.Equal(payment.PaymentId.ToString(), payPal.RequestIds[^1]);

        var order = payPal.CreatedOrder!;
        Assert.Equal("CAPTURE", (string?)order["intent"]);
        Assert.Equal(payment.PaymentId.ToString(), (string?)order["purchase_units"]![0]!["custom_id"]);
        Assert.Equal("EUR", (string?)order["purchase_units"]![0]!["amount"]!["currency_code"]);
        Assert.Equal("19.90", (string?)order["purchase_units"]![0]!["amount"]!["value"]);
        var experience = order["payment_source"]!["paypal"]!["experience_context"]!;
        Assert.Equal($"https://shop.example/ekom/payments/paypalresponse?paymentId={payment.PaymentId}", (string?)experience["return_url"]);
        Assert.Equal("https://shop.example/cancel", (string?)experience["cancel_url"]);
        Assert.Equal("PAY_NOW", (string?)experience["user_action"]);

        Assert.Contains("https://www.sandbox.paypal.com/checkoutnow?token=ORDER1", html);
    }

    [Fact]
    public async Task LivePayPalIsUsedUnlessTheSandboxIsChosenAndNothingStartsWithoutCredentials()
    {
        var live = Site(x => x.Sandbox = false);
        var payPal = new PayPalApi();

        await Provider(live, payPal).RequestAsync(Settings(live, 19.90m));

        Assert.Contains("POST https://api-m.paypal.com/v2/checkout/orders", payPal.Requests);

        var keyless = Site(x => x.ClientSecret = null);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => Provider(keyless, payPal).RequestAsync(Settings(keyless, 19.90m)));
        Assert.Empty(keyless.Payments.Payments);
    }

    [Fact]
    public async Task ReturningCustomerPaysOnceHoweverOftenTheReturnAddressIsOpened()
    {
        var site = Site();
        var payPal = new PayPalApi();
        var payment = await RequestAsync(site, payPal);
        payPal.Approve();

        var result = await Controller(site, payPal).Get(payment.PaymentId, "ORDER1");
        await Controller(site, payPal).Get(payment.PaymentId, "ORDER1");

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.StartsWith("https://shop.example/receipt?", redirect.Url);
        Assert.Contains("reference=" + payment.PaymentId, redirect.Url);

        Assert.Equal(1, payPal.Requests.Count(x => x.EndsWith("/ORDER1/capture")));
        var capture = Assert.Single(site.Payments.Recorded);
        Assert.Equal(PaymentTransactionType.Captured, capture.Type);
        Assert.Equal(19.90m, capture.Amount);
        Assert.Equal("CAPTURE1", capture.TransactionId);
        Assert.Contains(capture.Details!, x => x.Label == "PayPal order" && x.Value == "ORDER1");
    }

    [Fact]
    public async Task OrderThatWasNotApprovedOrBelongsToAnotherPaymentIsNotCaptured()
    {
        var site = Site();
        var payPal = new PayPalApi();
        var payment = await RequestAsync(site, payPal);

        // The customer opens the return address without approving the order.
        var result = await Controller(site, payPal).Get(payment.PaymentId, "ORDER1");
        Assert.Equal("https://shop.example/error", Assert.IsType<RedirectResult>(result).Url);

        // An approved order that was created for another payment.
        payPal.Approve();
        var other = await site.Payments.CreateAsync(Settings(site, 19.90m), PayPalPayment._ppNodeName, 19.90m, new PayPalSettings());
        result = await Controller(site, payPal).Get(other.PaymentId, "ORDER1");
        Assert.Equal("https://shop.example/error", Assert.IsType<RedirectResult>(result).Url);

        Assert.IsType<BadRequestResult>(await Controller(site, payPal).Get(payment.PaymentId, "../../v1/other"));
        Assert.IsType<NotFoundResult>(await Controller(site, payPal).Get(Guid.NewGuid(), "ORDER1"));

        Assert.DoesNotContain(payPal.Requests, x => x.EndsWith("/capture"));
        Assert.Empty(site.Payments.Recorded);
    }

    [Fact]
    public async Task DelayedCaptureRecordsTheAuthorizationFirstAndTheCaptureWhenItHappens()
    {
        var site = Site(x => x.UseDelayedCapture = true);
        var payPal = new PayPalApi();
        var payment = await RequestAsync(site, payPal);
        Assert.Equal("AUTHORIZE", (string?)payPal.CreatedOrder!["intent"]);
        payPal.Approve();

        Assert.IsType<RedirectResult>(await Controller(site, payPal).Get(payment.PaymentId, "ORDER1"));
        Assert.Contains(payPal.Requests, x => x.EndsWith("/ORDER1/authorize"));

        Assert.IsType<OkResult>(await WebhookAsync(site, payPal,
            Event("PAYMENT.CAPTURE.COMPLETED", Resource("CAPTURE1", "COMPLETED", "15.00", payment.PaymentId))));

        Assert.Equal(
            new[] { PaymentTransactionType.Authorized, PaymentTransactionType.Captured },
            site.Payments.Recorded.Select(x => x.Type));
        Assert.Equal("AUTH1", site.Payments.Recorded[0].TransactionId);
        Assert.Equal(15.00m, site.Payments.Recorded[1].Amount);
    }

    [Fact]
    public async Task PaymentPayPalRefusesIsRecordedAsFailedAndTheCustomerIsSentToTheErrorPage()
    {
        var site = Site();
        var payPal = new PayPalApi { CaptureIssue = "INSTRUMENT_DECLINED" };
        var payment = await RequestAsync(site, payPal);
        payPal.Approve();

        var result = await Controller(site, payPal).Get(payment.PaymentId, "ORDER1");

        Assert.Equal("https://shop.example/error", Assert.IsType<RedirectResult>(result).Url);
        var failed = Assert.Single(site.Payments.Recorded);
        Assert.Equal(PaymentTransactionType.Failed, failed.Type);
        Assert.Contains("INSTRUMENT_DECLINED", failed.Message);
    }

    [Fact]
    public async Task PaymentPayPalIsReviewingIsRecordedWhenItsWebhookTellsTheOutcome()
    {
        var site = Site();
        var payPal = new PayPalApi { CaptureStatus = "PENDING" };
        var payment = await RequestAsync(site, payPal);
        payPal.Approve();

        var result = await Controller(site, payPal).Get(payment.PaymentId, "ORDER1");

        Assert.StartsWith("https://shop.example/receipt?", Assert.IsType<RedirectResult>(result).Url);
        Assert.Empty(site.Payments.Recorded);

        Assert.IsType<OkResult>(await WebhookAsync(site, payPal,
            Event("PAYMENT.CAPTURE.PENDING", Resource("CAPTURE1", "PENDING", "19.90", payment.PaymentId))));
        Assert.Empty(site.Payments.Recorded);

        Assert.IsType<OkResult>(await WebhookAsync(site, payPal,
            Event("PAYMENT.CAPTURE.COMPLETED", Resource("CAPTURE1", "COMPLETED", "19.90", payment.PaymentId))));
        Assert.Single(site.Payments.Captures(payment.PaymentId));
    }

    [Fact]
    public async Task ApprovedOrderIsCompletedByWebhookWhenTheCustomerNeverReturns()
    {
        var site = Site();
        var payPal = new PayPalApi();
        var payment = await RequestAsync(site, payPal);
        payPal.Approve();
        var approved = Event("CHECKOUT.ORDER.APPROVED", new JsonObject
        {
            ["id"] = "ORDER1",
            ["status"] = "APPROVED",
            ["purchase_units"] = new JsonArray(new JsonObject { ["custom_id"] = payment.PaymentId.ToString() }),
        });

        Assert.IsType<OkResult>(await WebhookAsync(site, payPal, approved));
        Assert.IsType<OkResult>(await WebhookAsync(site, payPal, approved));
        // PayPal's own capture event and a late return of the customer are the same payment.
        Assert.IsType<OkResult>(await WebhookAsync(site, payPal,
            Event("PAYMENT.CAPTURE.COMPLETED", Resource("CAPTURE1", "COMPLETED", "19.90", payment.PaymentId))));
        Assert.IsType<RedirectResult>(await Controller(site, payPal).Get(payment.PaymentId, "ORDER1"));

        Assert.Equal(1, payPal.Requests.Count(x => x.EndsWith("/ORDER1/capture")));
        Assert.Equal(PaymentTransactionType.Captured, Assert.Single(site.Payments.Recorded).Type);
    }

    [Fact]
    public async Task RefundsAndVoidsMadeAtPayPalAreRecorded()
    {
        var site = Site();
        var payPal = new PayPalApi();
        var payment = await RequestAsync(site, payPal);
        var refund = Event("PAYMENT.CAPTURE.REFUNDED", Resource("REFUND1", "COMPLETED", "5.00", payment.PaymentId));

        Assert.IsType<OkResult>(await WebhookAsync(site, payPal, refund));
        Assert.IsType<OkResult>(await WebhookAsync(site, payPal, refund));
        Assert.IsType<OkResult>(await WebhookAsync(site, payPal,
            Event("PAYMENT.CAPTURE.REFUNDED", Resource("REFUND2", "COMPLETED", "14.90", payment.PaymentId))));
        Assert.IsType<OkResult>(await WebhookAsync(site, payPal,
            Event("PAYMENT.AUTHORIZATION.VOIDED", Resource("AUTH1", "VOIDED", "19.90", payment.PaymentId))));

        Assert.Equal(
            new[] { PaymentTransactionType.Refunded, PaymentTransactionType.Refunded, PaymentTransactionType.Voided },
            site.Payments.Recorded.Select(x => x.Type));
        Assert.Equal(new decimal?[] { 5.00m, 14.90m }, site.Payments.Recorded.Take(2).Select(x => x.Amount));
    }

    [Fact]
    public async Task WebhookPayPalDoesNotVouchForRecordsNothing()
    {
        var site = Site();
        var payPal = new PayPalApi();
        var payment = await RequestAsync(site, payPal);
        var completed = Event("PAYMENT.CAPTURE.COMPLETED", Resource("CAPTURE1", "COMPLETED", "19.90", payment.PaymentId));

        payPal.VerificationStatus = "FAILURE";
        Assert.IsNotType<OkResult>(await WebhookAsync(site, payPal, completed));

        // PayPal is asked about the event exactly as it was received, with the webhook's own id.
        using var verification = JsonDocument.Parse(payPal.Verification!);
        Assert.Equal("WEBHOOK1", verification.RootElement.GetProperty("webhook_id").GetString());
        Assert.Equal("transmission-1", verification.RootElement.GetProperty("transmission_id").GetString());
        Assert.Contains("\"webhook_event\":" + completed, payPal.Verification);

        payPal.VerificationStatus = "SUCCESS";
        Assert.IsNotType<OkResult>(await WebhookAsync(site, payPal, completed, signed: false));
        Assert.IsNotType<OkResult>(await WebhookAsync(site, payPal, "not json"));

        var withoutWebhook = Site(x => x.WebhookId = null);
        var unverifiable = await RequestAsync(withoutWebhook, payPal);
        Assert.IsNotType<OkResult>(await WebhookAsync(withoutWebhook, payPal,
            Event("PAYMENT.CAPTURE.COMPLETED", Resource("CAPTURE1", "COMPLETED", "19.90", unverifiable.PaymentId))));

        Assert.Empty(site.Payments.Recorded);
        Assert.Empty(withoutWebhook.Payments.Recorded);
    }

    [Fact]
    public async Task EventsOfOtherProvidersOrNotAboutAnEkomPaymentAreNotRecorded()
    {
        var site = Site();
        var payPal = new PayPalApi();
        var other = await site.Payments.CreateAsync(Settings(site, 19.90m), "otherprovider", 19.90m, new { WebhookId = "WEBHOOK1" });

        Assert.IsType<NotFoundResult>(await WebhookAsync(site, payPal,
            Event("PAYMENT.CAPTURE.COMPLETED", Resource("CAPTURE1", "COMPLETED", "19.90", other.PaymentId))));
        Assert.IsType<NotFoundResult>(await WebhookAsync(site, payPal,
            Event("PAYMENT.CAPTURE.COMPLETED", Resource("CAPTURE1", "COMPLETED", "19.90", Guid.NewGuid()))));
        // A payment made at PayPal outside Ekom, and an event Ekom has no use for.
        Assert.IsType<OkResult>(await WebhookAsync(site, payPal,
            Event("PAYMENT.CAPTURE.COMPLETED", Resource("CAPTURE1", "COMPLETED", "19.90", null))));
        Assert.IsType<OkResult>(await WebhookAsync(site, payPal,
            Event("BILLING.SUBSCRIPTION.CREATED", Resource("SUB1", "ACTIVE", "19.90", other.PaymentId))));

        Assert.Empty(site.Payments.Recorded);
        Assert.Null(payPal.Verification);
    }

    [Theory]
    [InlineData("EUR", 19.9, "19.90")]
    [InlineData("USD", 0.505, "0.51")]
    [InlineData("JPY", 500.4, "500")]
    [InlineData("HUF", 1999.5, "2000")]
    public void AmountsAreSentWithTheDecimalsPayPalExpectsOfTheCurrency(string currency, decimal amount, string expected)
        => Assert.Equal(expected, PayPalAmount.Format(amount, currency));

    static ProviderTestSite Site(Action<PayPalSettings>? configure = null) => new()
    {
        ProviderNode = o =>
        {
            if (o is not PayPalSettings settings) return;

            settings.ClientId = "paypal-client";
            settings.ClientSecret = "paypal-secret";
            settings.WebhookId = "WEBHOOK1";
            settings.Sandbox = true;
            configure?.Invoke(settings);
        },
    };

    // PayPal has no ISK.
    static PaymentSettings Settings(ProviderTestSite site, decimal total)
    {
        var settings = site.Settings(total);
        settings.Currency = "EUR";
        return settings;
    }

    static PayPalPayment Provider(ProviderTestSite site, PayPalApi payPal) => new(
        NullLogger<PayPalPayment>.Instance, site, site.Payments, site, payPal, new ConfigurationBuilder().Build());

    static async Task<OrderPayment> RequestAsync(ProviderTestSite site, PayPalApi payPal)
    {
        await Provider(site, payPal).RequestAsync(Settings(site, 19.90m));

        return site.Payments.Payments[^1];
    }

    static PayPalResponseController Controller(ProviderTestSite site, PayPalApi payPal) => ProviderTestSite.WithRequest(
        new PayPalResponseController(NullLogger<PayPalResponseController>.Instance, site.Configuration, site.Payments, site, payPal, site));

    /// <summary>Delivers a webhook the way PayPal does: the event as the body, its signature in headers.</summary>
    static Task<IActionResult> WebhookAsync(ProviderTestSite site, PayPalApi payPal, string payload, bool signed = true)
    {
        var controller = Controller(site, payPal);
        controller.Request.Method = "POST";
        controller.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        if (signed)
        {
            controller.Request.Headers["PAYPAL-TRANSMISSION-ID"] = "transmission-1";
            controller.Request.Headers["PAYPAL-TRANSMISSION-TIME"] = "2026-11-18T12:00:00Z";
            controller.Request.Headers["PAYPAL-TRANSMISSION-SIG"] = "c2lnbmF0dXJl";
            controller.Request.Headers["PAYPAL-CERT-URL"] = "https://api.paypal.com/v1/notifications/certs/CERT-1";
            controller.Request.Headers["PAYPAL-AUTH-ALGO"] = "SHA256withRSA";
        }

        return controller.Webhook();
    }

    static JsonObject Resource(string id, string status, string amount, Guid? paymentId)
    {
        var resource = new JsonObject
        {
            ["id"] = id,
            ["status"] = status,
            ["amount"] = new JsonObject { ["currency_code"] = "EUR", ["value"] = amount },
        };
        if (paymentId != null) resource["custom_id"] = paymentId.Value.ToString();

        return resource;
    }

    static string Event(string type, JsonObject resource) => new JsonObject
    {
        ["id"] = "WH-1",
        ["event_version"] = "1.0",
        ["create_time"] = "2026-11-18T12:00:00.000Z",
        ["resource_type"] = "capture",
        ["event_type"] = type,
        ["resource"] = resource,
    }.ToJsonString();

    /// <summary>
    /// Stands in for PayPal's API: keeps the one order that is created and what happens to it,
    /// and answers whether it vouches for a webhook.
    /// </summary>
    sealed class PayPalApi : HttpMessageHandler, IHttpClientFactory
    {
        string _status = "PAYER_ACTION_REQUIRED";
        JsonObject? _payments;

        public List<string> Requests { get; } = [];
        public List<string?> RequestIds { get; } = [];
        public string? TokenAuthorization { get; private set; }
        public JsonNode? CreatedOrder { get; private set; }
        public string? Verification { get; private set; }

        /// <summary>The status PayPal gives the capture of the order.</summary>
        public string CaptureStatus { get; set; } = "COMPLETED";

        /// <summary>Makes PayPal refuse to capture the order with this issue.</summary>
        public string? CaptureIssue { get; set; }
        public string VerificationStatus { get; set; } = "SUCCESS";

        /// <summary>The customer approves the order at PayPal.</summary>
        public void Approve() => _status = "APPROVED";

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add($"{request.Method} {request.RequestUri}");
            RequestIds.Add(request.Headers.TryGetValues("PayPal-Request-Id", out var ids) ? ids.Single() : null);

            if (path == "/v1/oauth2/token")
            {
                TokenAuthorization = request.Headers.Authorization!.ToString();
                return Json(HttpStatusCode.OK, new JsonObject { ["access_token"] = "token-1", ["token_type"] = "Bearer", ["expires_in"] = 32400 });
            }

            Assert.Equal("Bearer token-1", request.Headers.Authorization!.ToString());

            if (path == "/v1/notifications/verify-webhook-signature")
            {
                Verification = body;
                return Json(HttpStatusCode.OK, new JsonObject { ["verification_status"] = VerificationStatus });
            }

            if (path == "/v2/checkout/orders")
            {
                CreatedOrder = JsonNode.Parse(body!);
                return Json(HttpStatusCode.Created, Order());
            }

            if (path != "/v2/checkout/orders/ORDER1" && !path.StartsWith("/v2/checkout/orders/ORDER1/"))
            {
                return Json(HttpStatusCode.NotFound, new JsonObject { ["name"] = "RESOURCE_NOT_FOUND" });
            }

            if (request.Method == HttpMethod.Post)
            {
                if (_status != "APPROVED")
                {
                    return Error("ORDER_NOT_APPROVED");
                }
                if (CaptureIssue != null)
                {
                    return Error(CaptureIssue);
                }

                var amount = CreatedOrder!["purchase_units"]![0]!["amount"]!.DeepClone();
                var customId = (string?)CreatedOrder["purchase_units"]![0]!["custom_id"];
                _status = "COMPLETED";
                _payments = path.EndsWith("/authorize")
                    ? new JsonObject
                    {
                        ["authorizations"] = new JsonArray(new JsonObject
                        {
                            ["id"] = "AUTH1", ["status"] = "CREATED", ["amount"] = amount, ["custom_id"] = customId,
                        }),
                    }
                    : new JsonObject
                    {
                        ["captures"] = new JsonArray(new JsonObject
                        {
                            ["id"] = "CAPTURE1", ["status"] = CaptureStatus, ["amount"] = amount, ["custom_id"] = customId,
                        }),
                    };
            }

            return Json(HttpStatusCode.OK, Order());
        }

        JsonObject Order()
        {
            var unit = CreatedOrder!["purchase_units"]![0]!.DeepClone().AsObject();
            if (_payments != null) unit["payments"] = _payments.DeepClone();

            return new JsonObject
            {
                ["id"] = "ORDER1",
                ["status"] = _status,
                ["purchase_units"] = new JsonArray(unit),
                ["links"] = new JsonArray(
                    new JsonObject { ["rel"] = "self", ["href"] = "https://api-m.sandbox.paypal.com/v2/checkout/orders/ORDER1" },
                    new JsonObject { ["rel"] = "payer-action", ["href"] = "https://www.sandbox.paypal.com/checkoutnow?token=ORDER1" }),
            };
        }

        static HttpResponseMessage Error(string issue) => Json(HttpStatusCode.UnprocessableEntity, new JsonObject
        {
            ["name"] = "UNPROCESSABLE_ENTITY",
            ["details"] = new JsonArray(new JsonObject { ["issue"] = issue, ["description"] = "PayPal refused." }),
        });

        static HttpResponseMessage Json(HttpStatusCode status, JsonObject body) => new(status)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
    }
}
