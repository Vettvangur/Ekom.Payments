using Ekom.Models.Payments;
using Ekom.Payments.Helpers;
using Ekom.Payments.Netgiro;
using Ekom.Payments.Valitor;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Cryptography;
using System.Text;
using Xunit;
using ValitorPayment = Ekom.Payments.Valitor.Payment;
using ValitorResponse = Ekom.Payments.Valitor.Response;

namespace Ekom.Payments.Tests;

/// <summary>
/// Valitor stores its payments on the Ekom order: a signed confirmation records one capture
/// with the card details, anything else records nothing. All secrets are made up.
/// </summary>
public sealed class ValitorTests
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
        var response = new ValitorResponse
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

        var result = await Controller(site).Post(new ValitorResponse
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
            // Neither payment has a Valitor verification code
            var response = new ValitorResponse
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
        var legacy = site.AddLegacyPayment(1990, new ValitorSettings { MerchantId = "1", VerificationCode = Code }, "valitor");

        Assert.IsType<OkResult>(await Controller(site).Post(new ValitorResponse
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
    public async Task HelperDoesNotVerifyWithoutAVerificationCodeAndReturnsTheOrdersPayment()
    {
        var site = new ProviderTestSite();
        var (payment, _) = await RequestAsync(site, 1990);
        var helper = new ValitorResponseHelper(
            NullLogger<ValitorResponseHelper>.Instance, site.Payments, new ConfigurationBuilder().Build());
        var reference = payment.PaymentId.ToString();

        Assert.Null(await helper.Verify(new ValitorResponse
        {
            ReferenceNumber = reference,
            DigitalSignatureResponse = CryptoHelpers.GetMD5StringSum(reference),
        }));
        var verified = await helper.Verify(
            new ValitorResponse
            {
                ReferenceNumber = reference,
                DigitalSignatureResponse = CryptoHelpers.GetMD5StringSum(Code + reference),
            },
            Code);
        Assert.Equal(site.OrderId, verified!.OrderUniqueId);
    }

    static async Task<(OrderPayment Payment, Dictionary<string, string> Form)> RequestAsync(ProviderTestSite site, decimal total)
    {
        var settings = site.Settings(total);
        settings.CustomSettings[typeof(ValitorSettings)] = new ValitorSettings
        {
            MerchantId = "1", VerificationCode = Code,
            PaymentPageUrl = new Uri("https://valitor.example/"),
        };
        var html = await new ValitorPayment(NullLogger<ValitorPayment>.Instance, site.Configuration, site, site.Payments, site,
            new ConfigurationBuilder().Build()).RequestAsync(settings);
        return (site.Payments.Payments[^1], ProviderTestSite.ParseForm(html));
    }

    static ValitorResponseController Controller(ProviderTestSite site) => ProviderTestSite.WithRequest(new ValitorResponseController(
        NullLogger<ValitorResponseController>.Instance, site.Configuration, site.Payments, site.LegacyOrders, site));

    static string Sha256Hex(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
