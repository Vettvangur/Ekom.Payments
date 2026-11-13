using Ekom.Models.Payments;
using Ekom.Payments.PayTrail;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

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

        Assert.IsType<UnauthorizedResult>(await Controller(site, forged).Post());
        Assert.IsType<BadRequestResult>(await Controller(site, Signed(payment.PaymentId, "ok", amount: "1")).Post());
        Assert.IsType<NotFoundResult>(await Controller(site, Signed(Guid.NewGuid(), "ok", "1990")).Post());
        Assert.Empty(site.Payments.Recorded);
    }

    static Task<OrderPayment> CreateAsync(ProviderTestSite site)
        => site.Payments.CreateAsync(site.Settings(1990), "payTrail", 1990m, new PayTrailSettings { AccountId = "375917", SecretKey = Secret, Algorithm = "sha256" });

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
}
