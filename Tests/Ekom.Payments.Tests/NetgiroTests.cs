using Ekom.Models.Payments;
using Ekom.Payments.Netgiro;
using Ekom.Payments.Valitor;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Cryptography;
using System.Text;
using Xunit;
using NetgiroPayment = Ekom.Payments.Netgiro.Payment;
using NetgiroResponse = Ekom.Payments.Netgiro.Response;

namespace Ekom.Payments.Tests;

/// <summary>
/// Netgiro stores its payments on the Ekom order: a confirmation carrying the documented signature
/// records one capture, anything else records nothing. All secrets are made up.
/// </summary>
public sealed class NetgiroTests
{
    const string Secret = "netgiro-test-secret";
    static readonly Guid ApplicationId = Guid.Parse("2f1f6c1e-7c4b-4d0c-9a57-3b8d6e241905");

    [Fact]
    public async Task RequestStoresThePaymentOnTheOrderAndSignsItsId()
    {
        var site = new ProviderTestSite();

        var (payment, form) = await RequestAsync(site, 1990);

        Assert.Equal(site.OrderId, payment.OrderUniqueId);
        Assert.Equal("IS0001", payment.OrderNumber);
        Assert.Equal("netgiro", payment.ProviderType);
        Assert.Equal(payment.PaymentId.ToString(), form["ReferenceNumber"]);
        Assert.Equal(Sha256Hex(Secret + payment.PaymentId + "1990" + ApplicationId), form["Signature"]);
    }

    [Fact]
    public async Task AcceptsConfirmationSignedWithAmountAndStatusAndRecordsInvoiceDetails()
    {
        var site = new ProviderTestSite();
        var (payment, _) = await RequestAsync(site, 1990);

        Assert.IsType<OkResult>(await Controller(site).Post(Confirmation(payment.PaymentId, "1990", status: "1")));

        var capture = Assert.Single(site.Payments.Captures(payment.PaymentId));
        Assert.Equal(1990m, capture.Amount);
        Assert.Equal("6f9619ff-8b86-d011-b42d-00c04fc964ff", capture.TransactionId);
        Assert.Contains(capture.Details!, x => x.Label == "Invoice number" && x.Value == "1234");
        Assert.DoesNotContain("Signature", capture.RawData!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AcceptsConfirmationCodeSignatureWhenNetgiroSignatureIsNotSent()
    {
        var site = new ProviderTestSite();
        var (payment, _) = await RequestAsync(site, 1990);

        var response = new NetgiroResponse
        {
            ReferenceNumber = payment.PaymentId,
            ConfirmationCode = "6f9619ff-8b86-d011-b42d-00c04fc964ff",
            InvoiceNumber = "1234",
        };
        response.Signature = Sha256Hex(Secret + payment.PaymentId + response.ConfirmationCode + response.InvoiceNumber);

        Assert.IsType<OkResult>(await Controller(site).Post(response));
        Assert.Single(site.Payments.Captures(payment.PaymentId));
    }

    [Fact]
    public async Task UnconfirmedThenConfirmedCallbackRecordsOneCapture()
    {
        var site = new ProviderTestSite();
        var (payment, _) = await RequestAsync(site, 1990);

        Assert.IsType<OkResult>(await Controller(site).Post(Confirmation(payment.PaymentId, "1990", status: "1")));
        Assert.IsType<OkResult>(await Controller(site).Post(Confirmation(payment.PaymentId, "1990", status: "2")));

        Assert.Single(site.Payments.Captures(payment.PaymentId));
    }

    [Theory]
    // The values a payment request is signed with, presented as a payment confirmation
    [InlineData("1990", "2f1f6c1e-7c4b-4d0c-9a57-3b8d6e241905")]
    [InlineData("19902f1f6c1e-7c4b-4d0c-9a57-3b8d6e", "241905")]
    public async Task RejectsThePaymentRequestSignatureAsConfirmation(string confirmationCode, string invoiceNumber)
    {
        var site = new ProviderTestSite();
        var (payment, form) = await RequestAsync(site, 1990);

        var response = new NetgiroResponse
        {
            ReferenceNumber = payment.PaymentId,
            ConfirmationCode = confirmationCode,
            InvoiceNumber = invoiceNumber,
            Signature = form["Signature"],
        };

        Assert.IsType<BadRequestResult>(await Controller(site).Post(response));
        Assert.Empty(site.Payments.Recorded);
    }

    [Fact]
    public async Task RejectsThePaymentRequestSignatureAsNetgiroSignature()
    {
        var site = new ProviderTestSite();
        var (payment, form) = await RequestAsync(site, 5);

        // TransactionId + InvoiceNumber + TotalAmount + Status spells out TotalAmount + ApplicationId
        var response = new NetgiroResponse
        {
            ReferenceNumber = payment.PaymentId,
            TransactionId = "52f1f6c1e-7c4b-4d0c-9a57-3b8d6e241",
            InvoiceNumber = "90",
            TotalAmount = "5",
            Status = "5",
            NetgiroSignature = form["Signature"],
        };

        Assert.IsType<BadRequestResult>(await Controller(site).Post(response));
        Assert.Empty(site.Payments.Recorded);
    }

    [Theory]
    [InlineData("1", "1")]
    [InlineData("1990", "5")]
    [InlineData("1990", "")]
    public async Task RejectsConfirmationForAnotherAmountOrACanceledPayment(string totalAmount, string status)
    {
        var site = new ProviderTestSite();
        var (payment, _) = await RequestAsync(site, 1990);

        // Correctly signed, but not a payment of this order
        Assert.IsType<BadRequestResult>(await Controller(site).Post(Confirmation(payment.PaymentId, totalAmount, status)));
        Assert.Empty(site.Payments.Recorded);
    }

    [Fact]
    public async Task RejectsUnsignedConfirmationWithoutRecordingAFailedPayment()
    {
        var site = new ProviderTestSite();
        var (payment, _) = await RequestAsync(site, 1990);

        var response = Confirmation(payment.PaymentId, "1990", status: "1");
        response.NetgiroSignature = Sha256Hex("guess");

        Assert.IsType<BadRequestResult>(await Controller(site).Post(response));
        Assert.Empty(site.Payments.Recorded);
    }

    [Fact]
    public async Task DoesNotConfirmPaymentsOfOtherProviders()
    {
        var site = new ProviderTestSite();
        var valitor = await site.Payments.CreateAsync(site.Settings(1990), "valitor", 1990m,
            new ValitorSettings { MerchantId = "1", VerificationCode = "valitor-test-code" });

        // The payment has no Netgiro secret, anyone can compute a signature without one
        var response = new NetgiroResponse
        {
            ReferenceNumber = valitor.PaymentId,
            ConfirmationCode = "6f9619ff-8b86-d011-b42d-00c04fc964ff",
            InvoiceNumber = "1234",
        };
        response.Signature = Sha256Hex(valitor.PaymentId + response.ConfirmationCode + response.InvoiceNumber);

        Assert.IsType<NotFoundResult>(await Controller(site).Post(response));
        Assert.Empty(site.Payments.Recorded);
    }

    [Fact]
    public async Task DoesNotConfirmUnstampedLegacyPaymentsWithoutANetgiroSecret()
    {
        var site = new ProviderTestSite();
        // A payment of another provider created before providers were recorded on payments
        var legacy = site.AddLegacyPayment(1990, new ValitorSettings { MerchantId = "1", VerificationCode = "valitor-test-code" }, provider: null);

        var response = new NetgiroResponse
        {
            ReferenceNumber = legacy.UniqueId,
            ConfirmationCode = "6f9619ff-8b86-d011-b42d-00c04fc964ff",
            InvoiceNumber = "1234",
        };
        response.Signature = Sha256Hex(legacy.UniqueId + response.ConfirmationCode + response.InvoiceNumber);

        Assert.IsType<BadRequestResult>(await Controller(site).Post(response));
        Assert.Empty(site.Payments.Recorded);
    }

    [Theory]
    [InlineData("netgiro")]
    // Created before providers were recorded on payments
    [InlineData(null)]
    public async Task PaymentStartedBeforeTheUpgradeIsCopiedToEkomWhenItsCallbackArrives(string? stamp)
    {
        var site = new ProviderTestSite();
        var legacy = site.AddLegacyPayment(1990, new NetgiroSettings { ApplicationId = ApplicationId, Secret = Secret }, stamp);

        Assert.IsType<OkResult>(await Controller(site).Post(Confirmation(legacy.UniqueId, "1990", status: "1")));

        var payment = Assert.Single(site.Payments.Payments);
        Assert.Equal(legacy.UniqueId, payment.PaymentId);
        Assert.Equal(site.OrderId, payment.OrderUniqueId);
        Assert.Single(site.Payments.Captures(legacy.UniqueId));
    }

    [Fact]
    public async Task BindsJsonConfirmationWithNumericValues()
    {
        var reference = Guid.NewGuid();
        var http = new DefaultHttpContext();
        http.Request.ContentType = "application/json";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
            $$"""{"referenceNumber":"{{reference}}","transactionid":"t-1","invoiceNumber":1234,"totalAmount":1990,"status":1,"netgiroSignature":"abc","signature":"def"}"""));

        var context = new DefaultModelBindingContext
        {
            ActionContext = new ActionContext { HttpContext = http },
            ModelState = new ModelStateDictionary(),
            ModelName = "netgiroResponse",
        };
        await new NetgiroResponseModelBinder().BindModelAsync(context);

        var response = Assert.IsType<NetgiroResponse>(context.Result.Model);
        Assert.Equal(reference, response.ReferenceNumber);
        Assert.Equal("t-1", response.TransactionId);
        Assert.Equal("1234", response.InvoiceNumber);
        Assert.Equal("1990", response.TotalAmount);
        Assert.Equal("1", response.Status);
        Assert.Equal("abc", response.NetgiroSignature);
        Assert.Equal("def", response.Signature);
    }

    static async Task<(OrderPayment Payment, Dictionary<string, string> Form)> RequestAsync(ProviderTestSite site, decimal total)
    {
        var settings = site.Settings(total);
        settings.CustomSettings[typeof(NetgiroSettings)] = new NetgiroSettings
        {
            ApplicationId = ApplicationId, Secret = Secret,
            PaymentPageUrl = new Uri("https://netgiro.example/securepay/"),
        };
        var html = await new NetgiroPayment(NullLogger<NetgiroPayment>.Instance, site.Configuration, site, site.Payments, site,
            new ConfigurationBuilder().Build()).RequestAsync(settings);
        return (site.Payments.Payments[^1], ProviderTestSite.ParseForm(html));
    }

    static NetgiroResponseController Controller(ProviderTestSite site) => ProviderTestSite.WithRequest(new NetgiroResponseController(
        NullLogger<NetgiroResponseController>.Instance, site.Configuration, site.Payments, site.LegacyOrders, site));

    static NetgiroResponse Confirmation(Guid reference, string totalAmount, string status)
    {
        var response = new NetgiroResponse
        {
            ReferenceNumber = reference,
            TransactionId = "6f9619ff-8b86-d011-b42d-00c04fc964ff",
            InvoiceNumber = "1234",
            TotalAmount = totalAmount,
            Status = status,
        };
        // SHA256(SecretKey + ReferenceNumber + TransactionId + InvoiceNumber + TotalAmount + Status)
        response.NetgiroSignature = Sha256Hex(
            Secret + reference + response.TransactionId + response.InvoiceNumber + totalAmount + status);

        return response;
    }

    static string Sha256Hex(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
