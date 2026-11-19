using Ekom.Models.Payments;
using Ekom.Payments.Helpers;
using Ekom.Payments.Netgiro;
using Ekom.Payments.Rapyd;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Xunit;
using RapydPayment = Ekom.Payments.Rapyd.Payment;
using RapydResponse = Ekom.Payments.Rapyd.Response;

namespace Ekom.Payments.Tests;

/// <summary>
/// Rapyd stores its payments on the Ekom order: a signed confirmation records one capture
/// with the card details, anything else records nothing. All secrets are made up.
/// </summary>
public sealed class RapydTests
{
    const string Code = "valitor-test-code";

    [Fact]
    public async Task RequestStoresThePaymentOnTheOrderAndSendsItsIdAsReferenceNumber()
    {
        var site = new ProviderTestSite();

        var (payment, form) = await RequestAsync(site, 1990);

        Assert.Equal(site.OrderId, payment.OrderUniqueId);
        Assert.Equal("IS0001", payment.OrderNumber);
        Assert.Equal("valitor", payment.ProviderType);
        Assert.Equal(payment.PaymentId.ToString(), form["ReferenceNumber"]);
        Assert.Contains("reference=" + payment.PaymentId, form["PaymentSuccessfulURL"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignedConfirmationRecordsOneCaptureWithCardDetailsHoweverOftenItIsRepeated()
    {
        var site = new ProviderTestSite();
        var (payment, _) = await RequestAsync(site, 1990);
        var response = new RapydResponse
        {
            ReferenceNumber = payment.PaymentId.ToString(),
            DigitalSignatureResponse = Sha256Hex(Code + payment.PaymentId),
            CardType = "VISA",
            CardNumberMasked = "************1111",
            AuthorizationNumber = "654321",
            TransactionNumber = 778899,
            ContractNumber = 12345,
        };

        Assert.IsType<OkResult>(await Controller(site).Post(response));
        Assert.IsType<OkResult>(await Controller(site).Post(response));

        var capture = Assert.Single(site.Payments.Captures(payment.PaymentId));
        Assert.Equal(1990m, capture.Amount);
        Assert.Equal("778899", capture.TransactionId);
        Assert.Equal("654321", capture.AuthorizationCode);
        Assert.Equal("VISA", capture.CardBrand);
        Assert.Equal("************1111", capture.MaskedCardNumber);
        Assert.Contains(capture.Details!, x => x.Label == "Contract number" && x.Value == "12345");
        Assert.DoesNotContain(response.DigitalSignatureResponse, capture.RawData!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WrongSignatureRecordsNothing()
    {
        var site = new ProviderTestSite();
        var (payment, _) = await RequestAsync(site, 1990);

        var result = await Controller(site).Post(new RapydResponse
        {
            ReferenceNumber = payment.PaymentId.ToString(),
            DigitalSignatureResponse = Sha256Hex("guess" + payment.PaymentId),
        });

        Assert.IsNotType<OkResult>(result);
        Assert.Empty(site.Payments.Recorded);
    }

    [Fact]
    public async Task DoesNotConfirmPaymentsOfOtherProviders()
    {
        var site = new ProviderTestSite();
        var netgiroSettings = new NetgiroSettings { ApplicationId = Guid.NewGuid(), Secret = "netgiro-test-secret" };
        var netgiro = await site.Payments.CreateAsync(site.Settings(1990), "netgiro", 1990m, netgiroSettings);
        var legacy = site.AddLegacyPayment(1990, netgiroSettings, provider: null);

        foreach (var paymentId in new[] { netgiro.PaymentId, legacy.UniqueId })
        {
            // Neither payment has a Rapyd verification code
            var response = new RapydResponse
            {
                ReferenceNumber = paymentId.ToString(),
                DigitalSignatureResponse = Sha256Hex(paymentId.ToString()),
            };

            Assert.IsNotType<OkResult>(await Controller(site).Post(response));
        }

        Assert.Empty(site.Payments.Recorded);
    }

    [Fact]
    public async Task PaymentStartedBeforeTheUpgradeIsCopiedToEkomWhenItsCallbackArrives()
    {
        var site = new ProviderTestSite();
        var legacy = site.AddLegacyPayment(1990, new RapydSettings { MerchantId = "1", VerificationCode = Code }, "valitor");

        Assert.IsType<OkResult>(await Controller(site).Post(new RapydResponse
        {
            ReferenceNumber = legacy.UniqueId.ToString(),
            DigitalSignatureResponse = Sha256Hex(Code + legacy.UniqueId),
        }));

        var payment = Assert.Single(site.Payments.Payments);
        Assert.Equal(legacy.UniqueId, payment.PaymentId);
        Assert.Equal("valitor", payment.ProviderType);
        Assert.Single(site.Payments.Captures(legacy.UniqueId));
    }

    [Fact]
    public async Task HelperVerifiesTheSha256SignatureRapydSendsWithTheStoredVerificationCode()
    {
        var site = new ProviderTestSite();
        var (payment, _) = await RequestAsync(site, 1990);
        var helper = new RapydResponseHelper(
            NullLogger<RapydResponseHelper>.Instance, site.Payments, new ConfigurationBuilder().Build());
        var reference = payment.PaymentId.ToString();

        // Signed like the callback the response controller accepts.
        var verified = await helper.Verify(new RapydResponse
        {
            ReferenceNumber = reference,
            DigitalSignatureResponse = Sha256Hex(Code + reference),
        });
        Assert.Equal(site.OrderId, verified!.OrderUniqueId);

        Assert.NotNull(await helper.Verify(
            new RapydResponse { ReferenceNumber = reference, DigitalSignatureResponse = Sha256Hex(Code + reference) },
            Code));
        // The old MD5 signature and a signature without the code are not accepted.
        Assert.Null(await helper.Verify(new RapydResponse
        {
            ReferenceNumber = reference,
            DigitalSignatureResponse = CryptoHelpers.GetMD5StringSum(Code + reference),
        }));
        Assert.Null(await helper.Verify(new RapydResponse
        {
            ReferenceNumber = reference,
            DigitalSignatureResponse = Sha256Hex(reference),
        }));
    }

    [Theory]
    [InlineData(19.99, "20")]
    [InlineData(19.49, "19")]
    [InlineData(19.5, "20")]
    [InlineData(1990, "1990")]
    public async Task UnitPriceIsRoundedToWholeUnitsAndSignedAsSent(decimal price, string expected)
    {
        var site = new ProviderTestSite();

        var (payment, form) = await RequestAsync(site, price);

        Assert.Equal(expected, form["Product_1_Price"]);
        Assert.Equal(ExpectedSignature(payment, form), form["DigitalSignature"]);
    }

    [Fact]
    public async Task QuantityAndDiscountAreSentAndSignedWithTheInvariantCulture()
    {
        var site = new ProviderTestSite();
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("is-IS");
        try
        {
            var (payment, form) = await RequestAsync(site, 1990, order =>
            {
                order.Quantity = 1.5m;
                order.Discount = 12.5m;
            });

            Assert.Equal("1.5", form["Product_1_Quantity"]);
            Assert.Equal("12.5", form["Product_1_Discount"]);
            Assert.Equal(ExpectedSignature(payment, form), form["DigitalSignature"]);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    /// <summary>
    /// The request signature: verification code, "0", each line's quantity, price and discount, merchant id,
    /// reference number, success url, server side url and currency.
    /// </summary>
    static string ExpectedSignature(OrderPayment payment, Dictionary<string, string> form)
        => Sha256Hex(Code + "0"
            + form["Product_1_Quantity"] + form["Product_1_Price"] + form["Product_1_Discount"]
            + form["MerchantID"] + payment.PaymentId + form["PaymentSuccessfulURL"]
            + form["PaymentSuccessfulServerSideURL"] + form["Currency"]);

    static async Task<(OrderPayment Payment, Dictionary<string, string> Form)> RequestAsync(
        ProviderTestSite site, decimal total, Action<OrderItem>? order = null)
    {
        var settings = site.Settings(total);
        foreach (var item in settings.Orders) order?.Invoke(item);
        settings.CustomSettings[typeof(RapydSettings)] = new RapydSettings
        {
            MerchantId = "1", VerificationCode = Code,
            PaymentPageUrl = new Uri("https://valitor.example/"),
        };
        var html = await new RapydPayment(NullLogger<RapydPayment>.Instance, site.Configuration, site, site.Payments, site,
            new ConfigurationBuilder().Build()).RequestAsync(settings);
        return (site.Payments.Payments[^1], ProviderTestSite.ParseForm(html));
    }

    static RapydResponseController Controller(ProviderTestSite site) => ProviderTestSite.WithRequest(new RapydResponseController(
        NullLogger<RapydResponseController>.Instance, site.Configuration, site.Payments, site.LegacyOrders, site));

    static string Sha256Hex(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
