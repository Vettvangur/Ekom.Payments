using Ekom.Models.Payments;
using Ekom.Payments.AltaPay;
using Ekom.Payments.AltaPay.Model;
using Ekom.Payments.Netgiro;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Xunit;

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

    static Task<OrderPayment> CreateAsync(ProviderTestSite site, string? secret)
        => site.Payments.CreateAsync(site.Settings(1990), "altaPay", 1990m, Settings(secret), providerReference: site.OrderId.ToString());

    static AltaResponseController Controller(ProviderTestSite site) => ProviderTestSite.WithRequest(new AltaResponseController(
        NullLogger<AltaResponseController>.Instance, site.Configuration, site.Payments, site.LegacyOrders, site, site));

    static AltaSettings Settings(string? secret) => new()
    {
        Terminal = "Test Terminal",
        ApiUserName = "api-user",
        ApiPassword = "not-a-secret",
        BaseAddress = new Uri("https://alta.example/merchant/API/"),
        CustomerInformationSharedSecret = secret,
    };

    static PaymentResponse Confirmation(Guid shopOrderId, string? checksum) => new()
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
    static string Checksum(Guid shopOrderId, string amount, string secret)
        => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(
            $"amount={amount},currency=352,secret={secret},shop_orderid={shopOrderId}"))).ToLowerInvariant();
}
