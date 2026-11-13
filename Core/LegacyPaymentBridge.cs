using Ekom.Models.Payments;
using Ekom.Services;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ekom.Payments;

/// <summary>
/// Feeds Ekom's order payment records from payment providers that still work with the
/// <see cref="OrderStatus"/> row. A provider that uses <see cref="IOrderPayments"/> directly does not need it.
/// </summary>
static class LegacyPaymentBridge
{
    static IServiceProvider? Services
    {
        get
        {
            try { return Ekom.Configuration.Resolver; }
            catch (InvalidOperationException) { return null; }
        }
    }

    static IOrderPayments? Payments => Services?.GetService<IOrderPayments>();

    static ILogger? Logger => Services?.GetService<ILoggerFactory>()?.CreateLogger(typeof(LegacyPaymentBridge));

    /// <summary>Stores the payment request. A failure here must not stop the customer from paying.</summary>
    internal static async Task RequestedAsync(OrderStatus order, PaymentSettings settings, object? providerSettings, HttpContext? httpContext)
    {
        var payments = Payments;
        if (payments == null) return;

        try
        {
            await payments.CreateAsync(settings, ProviderType(order), order.Amount, providerSettings, order.CustomData, httpContext, order.UniqueId)
                .ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The capture path creates the payment again if this failed.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Logger?.LogError(ex,
                "Failed to store payment request. OrderNumber: {OrderNumber} OrderId: {OrderUniqueId} PaymentId: {PaymentId}",
                settings.OrderNumber, settings.OrderUniqueId, order.UniqueId);
        }
    }

    /// <summary>
    /// Records the capture, which also completes the order's checkout in Ekom.
    /// Failures propagate: an unrecorded capture is an order that was paid but not completed.
    /// </summary>
    internal static async Task CapturedAsync(OrderStatus order)
    {
        var payments = Payments;
        if (payments == null) return;

        await EnsurePaymentAsync(payments, order).ConfigureAwait(false);
        var paymentData = await ReadPaymentDataAsync(order.UniqueId).ConfigureAwait(false);
        await payments.RecordAsync(new PaymentTransaction
        {
            PaymentId = order.UniqueId,
            Type = PaymentTransactionType.Captured,
            Amount = order.Amount,
            MaskedCardNumber = paymentData?.CardNumber,
            PaymentMethod = paymentData?.PaymentMethod,
            RawData = IsJson(paymentData?.CustomData) ? paymentData!.CustomData : null,
            AuthorizationCode = IsJson(paymentData?.CustomData) ? null : paymentData?.CustomData,
        }).ConfigureAwait(false);
    }

    /// <summary>Records a failed payment. Never throws: it runs while a provider is already handling an error.</summary>
    internal static async Task FailedAsync(OrderStatus? order, Exception? exception)
    {
        var payments = Payments;
        if (payments == null || order == null || order.Paid) return;

        try
        {
            await EnsurePaymentAsync(payments, order).ConfigureAwait(false);
            await payments.RecordAsync(new PaymentTransaction
            {
                PaymentId = order.UniqueId,
                Type = PaymentTransactionType.Failed,
                Amount = order.Amount,
                Message = exception?.Message ?? "Payment verification failed",
            }).ConfigureAwait(false);
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Logger?.LogError(ex, "Failed to store payment failure. PaymentId: {PaymentId}", order.UniqueId);
        }
    }

    // Payments started before the upgrade have no Ekom payment row yet.
    static async Task EnsurePaymentAsync(IOrderPayments payments, OrderStatus order)
    {
        if (await payments.GetAsync(order.UniqueId).ConfigureAwait(false) != null) return;

        var settings = order.EkomPaymentSettings
            ?? throw new InvalidOperationException($"Payment {order.UniqueId} has no payment settings to find its order with.");
        await payments.CreateAsync(settings, ProviderType(order), order.Amount, null, order.CustomData, null, order.UniqueId).ConfigureAwait(false);
    }

    static async Task<PaymentData?> ReadPaymentDataAsync(Guid paymentId)
    {
        var factory = Services?.GetService<IDatabaseFactory>();
        if (factory == null) return null;

        try
        {
            await using var db = factory.GetDatabase();
            return await db.PaymentData.FirstOrDefaultAsync(x => x.Id == paymentId).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Card details are a nice-to-have on the record; the capture itself matters.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    // Providers stamp their base provider name into the stored provider settings.
    static string ProviderType(OrderStatus order)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(order.EkomPaymentProviderData)
                && Newtonsoft.Json.Linq.JToken.Parse(order.EkomPaymentProviderData) is Newtonsoft.Json.Linq.JObject json
                && json["ekomPaymentProvider"]?.Type == Newtonsoft.Json.Linq.JTokenType.String)
            {
                return json["ekomPaymentProvider"]!.ToString();
            }
        }
        catch (Newtonsoft.Json.JsonException)
        {
        }
        return string.Empty;
    }

    static bool IsJson(string? value)
    {
        var trimmed = value?.TrimStart();
        return !string.IsNullOrEmpty(trimmed) && (trimmed[0] == '{' || trimmed[0] == '[');
    }
}
