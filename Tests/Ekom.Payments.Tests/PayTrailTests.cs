using Ekom.Models.Payments;
using Ekom.Payments.PayTrail;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Xunit;
using PayTrailPayment = Ekom.Payments.PayTrail.Payment;

namespace Ekom.Payments.Tests;

/// <summary>
/// PayTrail answers by redirect and by server callback. A signed "ok" records one capture, a signed
/// "fail" records a failed payment, and anything unsigned records nothing. The secret is made up.
/// </summary>
public sealed class PayTrailTests
{
    const string Secret = "paytrail-test-secret";

    [Fact]
    public async Task SignedOkRecordsOneCaptureForRedirectAndCallbackTogether()
    {
        var site = new ProviderTestSite();
        var payment = await CreateAsync(site);
        var parameters = Signed(payment.PaymentId, "ok", amount: "1990");

        Assert.IsType<RedirectResult>(await Controller(site, parameters).Post());
        Assert.IsType<OkResult>(await Controller(site, parameters, callback: true).Post());

        var capture = Assert.Single(site.Payments.Recorded);
        Assert.Equal(PaymentTransactionType.Captured, capture.Type);
        Assert.Equal(1990m, capture.Amount);
        Assert.Equal("tx-123", capture.TransactionId);
        Assert.Equal("osuuspankki", capture.PaymentMethod);
        Assert.DoesNotContain(parameters["signature"], capture.RawData!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SignedFailIsRecordedAsAFailedPaymentAndPendingIsNot()
    {
        var site = new ProviderTestSite();
        var payment = await CreateAsync(site);

        Assert.IsType<OkResult>(await Controller(site, Signed(payment.PaymentId, "pending", "1990"), callback: true).Post());
        Assert.Empty(site.Payments.Recorded);

        Assert.IsType<OkResult>(await Controller(site, Signed(payment.PaymentId, "fail", "1990"), callback: true).Post());
        Assert.Equal(PaymentTransactionType.Failed, Assert.Single(site.Payments.Recorded).Type);
    }

    [Fact]
    public async Task WrongSignatureOrAmountRecordsNothing()
    {
        var site = new ProviderTestSite();
        var payment = await CreateAsync(site);
        var forged = Signed(payment.PaymentId, "ok", "1990");
        forged["signature"] = new string('0', 64);

        Assert.IsType<UnauthorizedResult>(await Controller(site, forged, callback: true).Post());
        Assert.IsType<BadRequestResult>(await Controller(site, Signed(payment.PaymentId, "ok", amount: "1"), callback: true).Post());
        Assert.IsType<NotFoundResult>(await Controller(site, Signed(Guid.NewGuid(), "ok", "1990")).Post());
        Assert.Empty(site.Payments.Recorded);
    }

    [Fact]
    public async Task TheCustomersBrowserIsSentToTheErrorPageWhenTheAnswerCannotBeTrusted()
    {
        var site = new ProviderTestSite();
        var payment = await CreateAsync(site);
        var forged = Signed(payment.PaymentId, "ok", "1990");
        forged["signature"] = new string('0', 64);

        Assert.Equal("https://shop.example/error", Assert.IsType<RedirectResult>(await Controller(site, forged).Post()).Url);
        Assert.Equal("https://shop.example/error", Assert.IsType<RedirectResult>(await Controller(site, Signed(payment.PaymentId, "ok", amount: "1")).Post()).Url);
        Assert.Empty(site.Payments.Recorded);
    }

    [Fact]
    public async Task MalformedAmountFailsVerification()
    {
        var site = new ProviderTestSite();
        var payment = await CreateAsync(site);
        var malformed = Signed(payment.PaymentId, "ok", amount: "19x0");

        Assert.IsType<UnauthorizedResult>(await Controller(site, malformed, callback: true).Post());
        Assert.Equal("https://shop.example/error", Assert.IsType<RedirectResult>(await Controller(site, malformed).Post()).Url);
        Assert.Empty(site.Payments.Recorded);
    }

    [Theory]
    [InlineData("sha1")]
    [InlineData("md5")]
    public async Task CallbackSignedWithAnAlgorithmPayTrailDoesNotUseIsRejected(string algorithm)
    {
        var site = new ProviderTestSite();
        var payment = await CreateAsync(site);
        var parameters = Signed(payment.PaymentId, "ok", "1990");
        parameters["checkout-algorithm"] = algorithm;
        // Unknown algorithms used to be checked with HMAC-SHA256
        parameters["signature"] = HmacSha256(parameters.Where(x => x.Key != "signature"));

        Assert.IsType<UnauthorizedResult>(await Controller(site, parameters, callback: true).Post());
        Assert.Empty(site.Payments.Recorded);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("delayed")]
    public async Task PaymentThatIsNotFinalSendsTheCustomerToTheSuccessPageAndRecordsNothing(string status)
    {
        var site = new ProviderTestSite();
        var payment = await CreateAsync(site);

        var redirect = Assert.IsType<RedirectResult>(await Controller(site, Signed(payment.PaymentId, status, "1990")).Post());

        Assert.Equal($"https://shop.example/receipt?orderId={site.OrderId}", redirect.Url);
        Assert.Empty(site.Payments.Recorded);
    }

    [Fact]
    public async Task SignedFailSendsTheCustomerToTheCancelPage()
    {
        var site = new ProviderTestSite();
        var payment = await CreateAsync(site);

        Assert.Equal("https://shop.example/cancel", Assert.IsType<RedirectResult>(await Controller(site, Signed(payment.PaymentId, "fail", "1990")).Post()).Url);
    }

    [Fact]
    public async Task EmptySettingsGetTheDefaultsTheReadmeDescribes()
    {
        var site = new ProviderTestSite();
        var api = new PayTrailApi();

        await Provider(site, api).RequestAsync(RequestSettings(site, new PayTrailSettings { AccountId = "375917", SecretKey = Secret }));

        Assert.Equal("https://services.paytrail.com/payments", api.RequestUri);
        Assert.Equal("sha256", api.Headers["checkout-algorithm"]);
        Assert.Equal("ekom-vettvangur", api.Headers["platform-name"]);
        Assert.Equal(PayTrailHmacHelper.CalculateHmac(Secret, api.Headers, api.Body!, "sha256"), api.Headers["signature"]);
        // The provider data stored with the payment is as configured
        Assert.Null(Assert.Single(site.Payments.Payments).GetProviderData<PayTrailSettings>()!.Algorithm);
    }

    [Fact]
    public async Task TheProviderNodeOrConfigurationCanStillSetWhatTheDefaultsCover()
    {
        var site = new ProviderTestSite
        {
            ProviderNode = x =>
            {
                var settings = (PayTrailSettings)x!;
                settings.Algorithm ??= "sha512";
                settings.ApiBaseUrl ??= new Uri("https://paytrail.example");
                settings.PlatformName ??= "my-platform";
            },
        };
        var api = new PayTrailApi();

        await Provider(site, api).RequestAsync(RequestSettings(site, new PayTrailSettings { AccountId = "375917", SecretKey = Secret }));

        Assert.Equal("https://paytrail.example/payments", api.RequestUri);
        Assert.Equal("sha512", api.Headers["checkout-algorithm"]);
        Assert.Equal("my-platform", api.Headers["platform-name"]);
        Assert.Equal(PayTrailHmacHelper.CalculateHmac(Secret, api.Headers, api.Body!, "sha512"), api.Headers["signature"]);
    }

    [Fact]
    public async Task UnsupportedAlgorithmFailsBeforeThePaymentIsCreated()
    {
        var site = new ProviderTestSite();
        var api = new PayTrailApi();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(site, api)
            .RequestAsync(RequestSettings(site, new PayTrailSettings { AccountId = "375917", SecretKey = Secret, Algorithm = "sha1" })));

        Assert.Contains("sha1", ex.Message, StringComparison.Ordinal);
        Assert.Empty(site.Payments.Payments);
        Assert.Null(api.RequestUri);
        Assert.Throws<ArgumentException>(() => PayTrailHmacHelper.CalculateHmac(Secret, [], algorithm: "sha1"));
    }

    static Task<OrderPayment> CreateAsync(ProviderTestSite site)
        => site.Payments.CreateAsync(site.Settings(1990), "payTrail", 1990m, new PayTrailSettings { AccountId = "375917", SecretKey = Secret, Algorithm = "sha256" });

    static string HmacSha256(IEnumerable<KeyValuePair<string, string>> parameters)
    {
        var payload = string.Join("\n", parameters.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key}:{x.Value}").Concat([""]));
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    static PayTrailPayment Provider(ProviderTestSite site, PayTrailApi api) => new(
        NullLogger<PayTrailPayment>.Instance, site.Configuration, site, site.Payments, site, api, new ConfigurationBuilder().Build());

    static PaymentSettings RequestSettings(ProviderTestSite site, PayTrailSettings payTrail)
    {
        var settings = site.Settings(1990);
        settings.Currency = "EUR";
        settings.Language = "fi-FI";
        settings.CustomSettings[typeof(PayTrailSettings)] = payTrail;
        return settings;
    }

    static Dictionary<string, string> Signed(Guid paymentId, string status, string amount)
    {
        var parameters = new Dictionary<string, string>
        {
            ["checkout-account"] = "375917",
            ["checkout-algorithm"] = "sha256",
            ["checkout-amount"] = amount,
            ["checkout-stamp"] = paymentId.ToString(),
            ["checkout-reference"] = "IS0001",
            ["checkout-transaction-id"] = "tx-123",
            ["checkout-status"] = status,
            ["checkout-provider"] = "osuuspankki",
        };
        parameters["signature"] = PayTrailHmacHelper.CalculateHmac(Secret, parameters);
        return parameters;
    }

    static PayTrailResponseController Controller(ProviderTestSite site, Dictionary<string, string> parameters, bool callback = false)
    {
        var controller = ProviderTestSite.WithRequest(new PayTrailResponseController(
            NullLogger<PayTrailResponseController>.Instance, site.Configuration, site.Payments, site.LegacyOrders, site));
        var query = new Dictionary<string, string?>(parameters!);
        if (callback) query["callback"] = "true";
        controller.HttpContext.Request.QueryString = new QueryString(QueryHelpers.AddQueryString(string.Empty, query));
        return controller;
    }

    /// <summary>Stands in for PayTrail's create payment API and keeps the request it was sent.</summary>
    sealed class PayTrailApi : HttpMessageHandler, IHttpClientFactory
    {
        public string? RequestUri { get; private set; }
        public string? Body { get; private set; }
        public Dictionary<string, string> Headers { get; } = [];

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri!.ToString();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            foreach (var header in request.Headers)
            {
                Headers[header.Key] = header.Value.Single();
            }

            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("{\"transactionId\":\"tx-123\",\"href\":\"https://pay.paytrail.example/pay/tx-123\"}", Encoding.UTF8, "application/json"),
            };
        }
    }
}
