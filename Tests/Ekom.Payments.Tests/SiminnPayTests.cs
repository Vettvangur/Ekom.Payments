using Ekom.Models.Payments;
using Ekom.Payments.Helpers;
using Ekom.Payments.SiminnPay;
using Ekom.Payments.SiminnPay.Model;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ekom.Payments.Tests;

/// <summary>
/// Síminn Pay notifies about a payment by its own order key. A signed notification is recorded as what it
/// means for the order; an unsigned one records nothing. The secret is made up.
/// </summary>
public sealed class SiminnPayTests
{
    const string Secret = "siminn-test-secret";

    [Fact]
    public async Task SuccessfulPaymentIsFoundByOrderKeyAndRecordedOnce()
    {
        var (site, payment, orderKey) = await CreateAsync();
        var notification = Notification(orderKey, SiminnPayStatus.PaymentSuccessful);

        Assert.IsType<OkResult>(await Controller(site).Post(notification));
        Assert.IsType<OkResult>(await Controller(site).Post(notification));

        var capture = Assert.Single(site.Payments.Recorded);
        Assert.Equal(payment.PaymentId, capture.PaymentId);
        Assert.Equal(PaymentTransactionType.Captured, capture.Type);
        Assert.Equal("siminn-tx-1", capture.TransactionId);
        Assert.Contains(capture.Details!, x => x.Label == "Síminn Pay order key" && x.Value == orderKey.ToString());
    }

    [Theory]
    [InlineData(SiminnPayStatus.CancelledByCustomer, PaymentTransactionType.Cancelled)]
    [InlineData(SiminnPayStatus.CancelledByMerchant, PaymentTransactionType.Cancelled)]
    [InlineData(SiminnPayStatus.Expired, PaymentTransactionType.Failed)]
    [InlineData(SiminnPayStatus.AcquirerError, PaymentTransactionType.Failed)]
    public async Task CancelledAndFailedAnswersAreRecorded(SiminnPayStatus status, PaymentTransactionType expected)
    {
        var (site, _, orderKey) = await CreateAsync();

        Assert.IsType<OkResult>(await Controller(site).Post(Notification(orderKey, status)));

        var recorded = Assert.Single(site.Payments.Recorded);
        Assert.Equal(expected, recorded.Type);
        Assert.False(string.IsNullOrWhiteSpace(recorded.Message));
    }

    [Fact]
    public async Task WaitingForTheCustomerRecordsNothing()
    {
        var (site, _, orderKey) = await CreateAsync();

        Assert.IsType<OkResult>(await Controller(site).Post(Notification(orderKey, SiminnPayStatus.WaitingForCustomer)));
        Assert.Empty(site.Payments.Recorded);
    }

    [Fact]
    public async Task UnsignedNotificationWrongAmountOrUnknownKeyRecordsNothing()
    {
        var (site, _, orderKey) = await CreateAsync();
        var forged = Notification(orderKey, SiminnPayStatus.PaymentSuccessful);
        forged.HMAC = new string('0', 64);
        var otherAmount = Notification(orderKey, SiminnPayStatus.PaymentSuccessful, amount: 1m);

        Assert.IsType<UnauthorizedResult>(await Controller(site).Post(forged));
        Assert.IsType<BadRequestResult>(await Controller(site).Post(otherAmount));
        Assert.IsType<NotFoundResult>(await Controller(site).Post(Notification(Guid.NewGuid(), SiminnPayStatus.PaymentSuccessful)));
        Assert.Empty(site.Payments.Recorded);
    }

    [Fact]
    public async Task PaymentStartedBeforeTheUpgradeIsFoundByItsOrderKey()
    {
        var site = NewSite();
        var orderKey = Guid.NewGuid();
        var legacy = site.AddLegacyPayment(1990, new SiminnPaySettings(), provider: null);
        legacy.CustomData = orderKey.ToString();

        Assert.IsType<OkResult>(await Controller(site).Post(Notification(orderKey, SiminnPayStatus.PaymentSuccessful)));

        var payment = Assert.Single(site.Payments.Payments);
        Assert.Equal(legacy.UniqueId, payment.PaymentId);
        Assert.Equal(site.OrderId, payment.OrderUniqueId);
        Assert.Single(site.Payments.Captures(legacy.UniqueId));
    }

    static ProviderTestSite NewSite() => new()
    {
        // Síminn Pay's secret lives on the payment provider node, not with the payment.
        ProviderNode = settings => ((SiminnPaySettings)settings!).Secret = Secret,
    };

    static async Task<(ProviderTestSite Site, OrderPayment Payment, Guid OrderKey)> CreateAsync()
    {
        var site = NewSite();
        var orderKey = Guid.NewGuid();
        var payment = await site.Payments.CreateAsync(site.Settings(1990), "siminnPay", 1990m, providerReference: orderKey.ToString());
        return (site, payment, orderKey);
    }

    static SiminnPayResponseController Controller(ProviderTestSite site) => ProviderTestSite.WithRequest(new SiminnPayResponseController(
        NullLogger<SiminnPayResponseController>.Instance, site.Configuration, site.Payments, site.LegacyOrders, site, site));

    static SiminnPayOrderStatus Notification(Guid orderKey, SiminnPayStatus status, decimal amount = 1990m)
    {
        var notification = new SiminnPayOrderStatus
        {
            OrderKey = orderKey,
            Amount = amount,
            Status = status,
            ExpiresAt = new DateTime(2026, 11, 13, 12, 0, 0),
            TransactionDetails = new Ekom.Payments.SiminnPay.apimodels.TransactionDetails { TransactionId = "siminn-tx-1" },
        };
        // HMAC SHA256 of orderKey + amount + expiry
        notification.HMAC = CryptoHelpers.GetHMACSHA256(Secret,
            orderKey.ToString() + (int)amount + notification.ExpiresAt.ToString("dd.MM.yyyy HH:mm:ss"));
        return notification;
    }
}
