using Ekom.Models.Payments;
using Ekom.Payments.AltaPay;
using Ekom.Payments.AltaPay.Model;
using Ekom.Payments.Netgiro;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;
using AltaPayment = Ekom.Payments.AltaPay.Payment;

namespace Ekom.Payments.Tests;

/// <summary>
/// AltaPay refers to a payment by the order id. A response carrying the checksum of the order's latest
/// AltaPay payment records one capture; anything else records nothing. All secrets are made up.
/// </summary>
public sealed class AltaPayTests
{
    const string Secret = "alta-test-secret";

    [Fact]
    public async Task ChecksummedConfirmationRecordsOneCaptureEvenWhenAnotherProviderWasTriedFirst()
    {
        var site = new ProviderTestSite();
        var netgiro = await site.Payments.CreateAsync(site.Settings(1990), "netgiro", 1990m, new NetgiroSettings { Secret = "x" });
        var alta = await CreateAsync(site, Secret);
        var confirmation = Confirmation(site.OrderId, Checksum(site.OrderId, "1990.00", Secret));

        Assert.IsType<RedirectResult>(await Controller(site).Post(confirmation));
        // AltaPay sends a browser response and a server notification for the same payment.
        Assert.IsType<RedirectResult>(await Controller(site).Post(confirmation));

        var capture = Assert.Single(site.Payments.Recorded);
        Assert.Equal(alta.PaymentId, capture.PaymentId);
        Assert.Equal(PaymentTransactionType.Captured, capture.Type);
        Assert.Equal("1", capture.TransactionId);
        Assert.Equal("411111******1111", capture.MaskedCardNumber);
        Assert.Empty(site.Payments.Captures(netgiro.PaymentId));
    }

    [Fact]
    public async Task TheLatestAltaPayAttemptOfTheOrderIsTheOneConfirmed()
    {
        var site = new ProviderTestSite();
        var first = await CreateAsync(site, Secret);
        var retry = await CreateAsync(site, Secret);

        Assert.IsType<RedirectResult>(await Controller(site).Post(Confirmation(site.OrderId, Checksum(site.OrderId, "1990.00", Secret))));

        Assert.Single(site.Payments.Captures(retry.PaymentId));
        Assert.Empty(site.Payments.Captures(first.PaymentId));
    }

    [Fact]
    public async Task WrongOrMissingChecksumRecordsNothing()
    {
        var site = new ProviderTestSite();
        await CreateAsync(site, Secret);

        Assert.IsNotType<RedirectResult>(await Controller(site).Post(Confirmation(site.OrderId, Checksum(site.OrderId, "1.00", Secret))));
        Assert.IsNotType<RedirectResult>(await Controller(site).Post(Confirmation(site.OrderId, checksum: null)));
        Assert.Empty(site.Payments.Recorded);
    }

    [Fact]
    public async Task DoesNotConfirmPaymentsOfOtherProviders()
    {
        var site = new ProviderTestSite();
        await site.Payments.CreateAsync(site.Settings(1990), "netgiro", 1990m, new NetgiroSettings { Secret = "x" });
        site.AddLegacyPayment(1990, new NetgiroSettings { Secret = "x" }, provider: "netgiro");

        // Neither payment has an AltaPay secret to check a checksum with
        Assert.IsType<NotFoundResult>(await Controller(site).Post(Confirmation(site.OrderId, checksum: null)));
        Assert.Empty(site.Payments.Recorded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task DoesNotConfirmPaymentsWithoutASharedSecret(string? secret)
    {
        var site = new ProviderTestSite();
        await CreateAsync(site, secret);

        // Without a shared secret AltaPay sends no checksum, and one calculated without a secret proves nothing
        foreach (var checksum in new[] { null, Checksum(site.OrderId, "1990.00", secret: "") })
        {
            Assert.IsNotType<RedirectResult>(await Controller(site).Post(Confirmation(site.OrderId, checksum)));
        }

        Assert.Empty(site.Payments.Recorded);
    }

    [Fact]
    public async Task PaymentStartedBeforeTheUpgradeIsCopiedToEkomWhenItsResponseArrives()
    {
        var site = new ProviderTestSite();
        var legacy = site.AddLegacyPayment(1990, Settings(Secret), "altaPay");

        Assert.IsType<RedirectResult>(await Controller(site).Post(Confirmation(site.OrderId, Checksum(site.OrderId, "1990.00", Secret))));

        var payment = Assert.Single(site.Payments.Payments);
        Assert.Equal(legacy.UniqueId, payment.PaymentId);
        Assert.Single(site.Payments.Captures(legacy.UniqueId));
    }

    [Fact]
    public async Task WithoutAPaymentPageTheCustomerIsSentToTheErrorPageWithOneValidQuery()
    {
        var site = new ProviderTestSite();
        var api = new AltaPayApi { PaymentPageUrl = null };
        var settings = RequestSettings(site, Settings(Secret));
        settings.ErrorUrl = new Uri($"https://shop.example/ekom/checkout/payment-return?orderId={site.OrderId}&outcome=error");

        var url = RedirectUrl(await Provider(site, api).RequestAsync(settings));

        Assert.Single(url.Split('?').Skip(1));
        var query = QueryHelpers.ParseQuery(new Uri(url).Query);
        Assert.Equal(site.OrderId.ToString(), query["orderId"]);
        Assert.Equal("error", query["outcome"]);
        Assert.Equal("paymenterror", query["errorStatus"]);
        Assert.Equal("We could not process your payment. Please try again.", query["errorMessage"]);
    }

    [Fact]
    public async Task CallbackAddressesKeepAHostStartingWithEkomAndLoseDoubleSlashesInThePath()
    {
        var site = new ProviderTestSite();
        var api = new AltaPayApi();
        var alta = Settings(Secret);
        alta.HostOverride = "ekomshop.is";
        var settings = RequestSettings(site, alta);
        settings.ReportUrl = new Uri("https://ekomshop.is//ekom/payments/altaresponse");

        await Provider(site, api).RequestAsync(settings);

        Assert.Equal("https://ekomshop.is/ekom/payments/altaresponse?browser=true", api.Form!["config[callback_ok]"]);
        Assert.Equal("https://ekomshop.is/ekom/payments/altaresponse/fail", api.Form["config[callback_fail]"]);
        Assert.Equal("https://ekomshop.is/ekom/payments/altaresponse", api.Form["config[callback_notification]"]);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("/altapay/form", "https://shop.example/altapay/form")]
    [InlineData("https://forms.example/altapay/form", "https://forms.example/altapay/form")]
    public async Task PaymentFormUrlIsOptionalAndMayBeAPathOrAnAbsoluteUrl(string? paymentFormUrl, string? expected)
    {
        var site = new ProviderTestSite();
        var api = new AltaPayApi();
        var alta = Settings(Secret);
        alta.PaymentFormUrl = paymentFormUrl;

        await Provider(site, api).RequestAsync(RequestSettings(site, alta));

        Assert.Equal(expected, api.Form!.GetValueOrDefault("config[callback_form]"));
        Assert.Single(site.Payments.Payments);
    }

    [Theory]
    [InlineData(nameof(AltaSettings.Terminal))]
    [InlineData(nameof(AltaSettings.BaseAddress))]
    [InlineData(nameof(AltaSettings.CustomerInformationSharedSecret))]
    [InlineData(nameof(AltaSettings.PaymentFormUrl))]
    public async Task RequiredSettingsAreCheckedBeforeThePaymentIsCreated(string setting)
    {
        var site = new ProviderTestSite();
        var api = new AltaPayApi();
        var alta = Settings(Secret);
        switch (setting)
        {
            case nameof(AltaSettings.Terminal): alta.Terminal = " "; break;
            case nameof(AltaSettings.BaseAddress): alta.BaseAddress = null!; break;
            case nameof(AltaSettings.CustomerInformationSharedSecret): alta.CustomerInformationSharedSecret = null; break;
            case nameof(AltaSettings.PaymentFormUrl): alta.PaymentFormUrl = "https://forms example/form"; break;
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(site, api).RequestAsync(RequestSettings(site, alta)));

        Assert.Contains(setting, ex.Message);
        Assert.Empty(site.Payments.Payments);
        Assert.Null(api.Form);
    }

    [Fact]
    public async Task VerifiedFailureIsRecordedOnceAndAnUnverifiedOneRecordsNothing()
    {
        var site = new ProviderTestSite();
        var alta = await CreateAsync(site, Secret);

        Assert.IsType<OkObjectResult>(await Controller(site).Post(Confirmation(site.OrderId, Checksum(site.OrderId, "1.00", Secret), "Failed")));
        Assert.Empty(site.Payments.Recorded);

        var failed = Confirmation(site.OrderId, Checksum(site.OrderId, "1990.00", Secret), "Failed");
        Assert.IsType<OkObjectResult>(await Controller(site).Post(failed));
        Assert.IsType<OkObjectResult>(await Controller(site).Post(failed));

        var failure = Assert.Single(site.Payments.Recorded);
        Assert.Equal(alta.PaymentId, failure.PaymentId);
        Assert.Equal(PaymentTransactionType.Failed, failure.Type);
    }

    [Fact]
    public async Task ResultsThatAreNotFinalRecordNothing()
    {
        var site = new ProviderTestSite();
        await CreateAsync(site, Secret);

        Assert.IsType<OkObjectResult>(await Controller(site).Post(Confirmation(site.OrderId, Checksum(site.OrderId, "1990.00", Secret), "Open")));
        Assert.Empty(site.Payments.Recorded);
    }

    [Fact]
    public async Task FailRouteRecordsAVerifiedFailureAndStillSendsTheCustomerToTheErrorPage()
    {
        var site = new ProviderTestSite();
        await CreateAsync(site, Secret);

        Assert.IsType<RedirectResult>(await Controller(site).Fail(Confirmation(site.OrderId, checksum: null, "Failed")));
        Assert.Empty(site.Payments.Recorded);

        var redirect = Assert.IsType<RedirectResult>(await Controller(site).Fail(Confirmation(site.OrderId, Checksum(site.OrderId, "1990.00", Secret), "Failed")));
        Assert.StartsWith("https://shop.example/error?", redirect.Url);
        Assert.Equal(PaymentTransactionType.Failed, Assert.Single(site.Payments.Recorded).Type);
    }

    [Fact]
    public async Task TheCustomersBrowserIsSentToTheErrorPageWhenThePaymentDidNotGoThrough()
    {
        var site = new ProviderTestSite();
        await CreateAsync(site, Secret);
        var failed = Confirmation(site.OrderId, Checksum(site.OrderId, "1990.00", Secret), "Failed");

        var redirect = Assert.IsType<RedirectResult>(await Controller(site, browser: true).Post(failed));
        Assert.StartsWith("https://shop.example/error?", redirect.Url);
        Assert.Contains("altapayFailed=true", redirect.Url);

        // The server notification is answered as before
        Assert.Equal("Payment not Valid", Assert.IsType<OkObjectResult>(await Controller(site).Post(failed)).Value);
    }

    [Fact]
    public async Task CallbackFormTemplateIsReturnedAsHtml()
    {
        var site = new ProviderTestSite();
        AltaPay.Events.AsyncEventHandler<CallbackUrlEventArgs> handler = (_, e) =>
        {
            e.Template = "<html><body><div id=\"PensioPaymentForm\"></div></body></html>";
            return Task.CompletedTask;
        };

        AltaPay.Events.CallbackUrl += handler;
        try
        {
            var result = Assert.IsType<ContentResult>(await Controller(site).CallbackFormAsync(new CallbackFromRequest()));
            Assert.Equal("text/html", result.ContentType);
            Assert.Contains("PensioPaymentForm", result.Content);
        }
        finally
        {
            AltaPay.Events.CallbackUrl -= handler;
        }
    }

    static Task<OrderPayment> CreateAsync(ProviderTestSite site, string? secret)
        => site.Payments.CreateAsync(site.Settings(1990), "altaPay", 1990m, Settings(secret), providerReference: site.OrderId.ToString());

    static AltaResponseController Controller(ProviderTestSite site, bool browser = false)
    {
        var controller = ProviderTestSite.WithRequest(new AltaResponseController(
            NullLogger<AltaResponseController>.Instance, site.Configuration, site.Payments, site.LegacyOrders, site, site));
        if (browser) controller.HttpContext.Request.QueryString = new QueryString("?browser=true");
        return controller;
    }

    static AltaPayment Provider(ProviderTestSite site, AltaPayApi api) => new(
        NullLogger<AltaPayment>.Instance, site.Configuration, site, site.Payments, site, api, new ConfigurationBuilder().Build());

    static PaymentSettings RequestSettings(ProviderTestSite site, AltaSettings alta)
    {
        var settings = site.Settings(1990);
        settings.CustomSettings[typeof(AltaSettings)] = alta;
        return settings;
    }

    /// <summary>The address the page returned by a payment request sends the customer to.</summary>
    // The address as the browser reads it: a JavaScript string, with its escapes resolved.
    static string RedirectUrl(string html)
        => Regex.Unescape(Regex.Match(html, "window.location.href = \"([^\"]*)\"").Groups[1].Value);

    static AltaSettings Settings(string? secret) => new()
    {
        Terminal = "Test Terminal",
        ApiUserName = "api-user",
        ApiPassword = "not-a-secret",
        BaseAddress = new Uri("https://alta.example/merchant/API/"),
        CustomerInformationSharedSecret = secret,
    };

    static PaymentResponse Confirmation(Guid shopOrderId, string? checksum, string result = "Success") => new()
    {
        ShopOrderId = shopOrderId,
        Checksum = checksum,
        Xml = WebUtility.UrlEncode(
            "<?xml version=\"1.0\"?><APIResponse version=\"20170228\"><Header><ErrorCode>0</ErrorCode></Header><Body>"
            + $"<Result>{result}</Result><Transactions><Transaction><TransactionId>1</TransactionId>"
            + $"<ShopOrderId>{shopOrderId}</ShopOrderId><MerchantCurrency>352</MerchantCurrency>"
            + "<MerchantCurrencyAlpha>ISK</MerchantCurrencyAlpha><CreditCardMaskedPan>411111******1111</CreditCardMaskedPan>"
            + "</Transaction></Transactions></Body></APIResponse>"),
    };

    /// <summary>
    /// MD5 of the comma separated key=value pairs, sorted by key
    /// </summary>
    static string Checksum(Guid shopOrderId, string amount, string secret)
        => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(
            $"amount={amount},currency=352,secret={secret},shop_orderid={shopOrderId}"))).ToLowerInvariant();

    /// <summary>
    /// Stands in for AltaPay's createPaymentRequest: keeps the form it was sent and answers with a payment page,
    /// or with an error when there is none.
    /// </summary>
    sealed class AltaPayApi : HttpMessageHandler, IHttpClientFactory
    {
        public string? PaymentPageUrl { get; set; } = "https://alta.example/payment-page";
        public Dictionary<string, string>? Form { get; private set; }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://alta.example/merchant/API/createPaymentRequest", request.RequestUri!.ToString());
            Form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken))
                .ToDictionary(x => x.Key, x => x.Value.ToString());

            var body = PaymentPageUrl == null
                ? "<APIResponse><Header><ErrorCode>0</ErrorCode></Header><Body><Result>Error</Result><ErrorMessage>No terminal</ErrorMessage></Body></APIResponse>"
                : $"<APIResponse><Header><ErrorCode>0</ErrorCode></Header><Body><Result>Success</Result><Url>{PaymentPageUrl}</Url></Body></APIResponse>";

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/xml") };
        }
    }
}
