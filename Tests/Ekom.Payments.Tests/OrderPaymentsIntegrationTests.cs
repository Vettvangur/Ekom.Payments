using Ekom.Models.Orders;
using Ekom.Models.Payments;
using Ekom.Payments.AsynchronousExample;
using Ekom.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net.Mail;
using Xunit;

namespace Ekom.Payments.Tests;

/// <summary>
/// Payment providers report to Ekom's order payment records: directly (the example provider),
/// or through the bridge for providers that still work with the legacy payment row.
/// </summary>
[Collection("OrderPayments")]
public sealed class OrderPaymentsIntegrationTests
{
    [Fact]
    public async Task ExampleProviderRecordsCaptureWithProviderDetails()
    {
        var payments = new InMemoryOrderPayments();
        var payment = await payments.CreateAsync(Settings(), "AsynchronousExample", 24990m);
        var controller = Controller(payments);

        var result = await controller.Post(new Response
        {
            ReferenceNumber = payment.PaymentId.ToString(),
            TransactionNumber = 55813,
            AuthorizationNumber = "123456",
            CardNumberMasked = "5587-40**-****-0014",
            CardType = "Mastercard",
        });

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.StartsWith("https://shop.test/receipt", redirect.Url, StringComparison.Ordinal);
        var captured = Assert.Single(payments.Recorded);
        Assert.Equal(PaymentTransactionType.Captured, captured.Type);
        Assert.Equal("55813", captured.TransactionId);
        Assert.Equal("123456", captured.AuthorizationCode);
        Assert.Equal("Mastercard", captured.CardBrand);
        Assert.Equal("5587-40**-****-0014", captured.MaskedCardNumber);
        Assert.NotNull(captured.RawData);
    }

    [Fact]
    public async Task ExampleProviderRecordsDeclineAndSendsCustomerToCancelUrl()
    {
        var payments = new InMemoryOrderPayments();
        var payment = await payments.CreateAsync(Settings(), "AsynchronousExample", 24990m);

        var result = await Controller(payments).Post(new Response
        {
            ReferenceNumber = payment.PaymentId.ToString(), Failed = true, Message = "Insufficient funds",
        });

        Assert.StartsWith("https://shop.test/cancel", Assert.IsType<RedirectResult>(result).Url, StringComparison.Ordinal);
        var failed = Assert.Single(payments.Recorded);
        Assert.Equal(PaymentTransactionType.Failed, failed.Type);
        Assert.Equal("Insufficient funds", failed.Message);
    }

    [Fact]
    public async Task ExampleProviderRefusesPaymentsOfOtherProvidersAndUnknownReferences()
    {
        var payments = new InMemoryOrderPayments();
        var foreign = await payments.CreateAsync(Settings(), "borgun", 24990m);
        var controller = Controller(payments);

        Assert.IsType<NotFoundResult>(await controller.Post(new Response { ReferenceNumber = foreign.PaymentId.ToString() }));
        Assert.IsType<NotFoundResult>(await controller.Post(new Response { ReferenceNumber = Guid.NewGuid().ToString() }));
        Assert.IsType<BadRequestResult>(await controller.Post(new Response { ReferenceNumber = "not-a-guid" }));
        Assert.Empty(payments.Recorded);
    }

    [Fact]
    public async Task LegacyProviderSuccessReachesEkomThroughTheBridgeOnce()
    {
        var payments = new InMemoryOrderPayments();
        var previous = TryGetResolver();
        Ekom.Configuration.Resolver = new ServiceCollection().AddSingleton<IOrderPayments>(payments).BuildServiceProvider();
        try
        {
            var settings = Settings();
            var row = new OrderStatus
            {
                UniqueId = Guid.NewGuid(),
                Amount = 24990m,
                EkomPaymentSettings = settings,
                EkomPaymentProviderData = "{\"ekomPaymentProvider\":\"borgun\"}",
            };

            // An error raised by a legacy provider may be an unverifiable callback: it is not a failed payment.
            await Events.OnErrorAsync(this, new ErrorEventArgs { OrderStatus = row, Exception = new InvalidOperationException("Signature mismatch") });
            Assert.Empty(payments.Payments);

            // A payment started before the upgrade has no Ekom payment yet: the bridge creates it.
            await Events.OnSuccessAsync(this, new SuccessEventArgs { OrderStatus = row });
            await Events.OnSuccessAsync(this, new SuccessEventArgs { OrderStatus = row });

            var payment = Assert.Single(payments.Payments);
            Assert.Equal(row.UniqueId, payment.PaymentId);
            Assert.Equal(settings.OrderUniqueId, payment.OrderUniqueId);
            Assert.Equal("IS-1042", payment.OrderNumber);
            Assert.Equal("borgun", payment.ProviderType);
            var captured = Assert.Single(payments.Recorded);
            Assert.Equal(PaymentTransactionType.Captured, captured.Type);
            Assert.Equal(row.UniqueId, captured.PaymentId);
        }
        finally
        {
            Ekom.Configuration.Resolver = previous!;
        }
    }

    static IServiceProvider? TryGetResolver()
    {
        try { return Ekom.Configuration.Resolver; }
        catch (InvalidOperationException) { return null; }
    }

    static PaymentSettings Settings()
    {
        var settings = new PaymentSettings
        {
            OrderUniqueId = Guid.NewGuid(),
            OrderNumber = "IS-1042",
            PaymentProviderName = "Card payment",
            Currency = "ISK",
            Store = "main",
            Language = "is-IS",
            SuccessUrl = new Uri("https://shop.test/receipt"),
            CancelUrl = new Uri("https://shop.test/cancel"),
            ErrorUrl = new Uri("https://shop.test/error"),
        };
        settings.OrderCustomData["ekomPaymentAttemptId"] = Guid.NewGuid().ToString();
        return settings;
    }

    static AsynchronousExampleResponseController Controller(IOrderPayments payments)
    {
        var http = new DefaultHttpContext();
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("shop.test");
        return new AsynchronousExampleResponseController(
            NullLogger<AsynchronousExampleResponseController>.Instance,
            new PaymentsConfiguration { SendEmailAlerts = false }, payments, new NoMail())
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    sealed class NoMail : IMailService
    {
        public string From { get; set; } = string.Empty;
        public string To { get; set; } = string.Empty;
        public Task SendAsync(MailMessage mailMessage) => Task.CompletedTask;
    }
}
