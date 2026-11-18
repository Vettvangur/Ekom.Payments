using System.Text.Json;

namespace Ekom.Payments.PayPal;

/// <summary>
/// A PayPal order: what the customer approves, and the payments made for it.
/// See https://developer.paypal.com/docs/api/orders/v2/#orders_get
/// </summary>
sealed class PayPalOrder
{
    public string? Id { get; private init; }

    /// <summary>CREATED, PAYER_ACTION_REQUIRED, APPROVED, COMPLETED, VOIDED, SAVED.</summary>
    public string? Status { get; private init; }

    /// <summary>The Ekom payment id the order was created for.</summary>
    public string? CustomId { get; private init; }

    /// <summary>Where the customer approves the order.</summary>
    public string? ApproveUrl { get; private init; }

    public PayPalPayment? Capture { get; private init; }
    public PayPalPayment? Authorization { get; private init; }

    public bool IsFor(Guid paymentId)
        => Guid.TryParse(CustomId, out var orderPaymentId) && orderPaymentId == paymentId;

    public static PayPalOrder From(JsonElement order)
    {
        var unit = PayPalJson.First(order, "purchase_units");
        var payments = PayPalJson.Child(unit, "payments");
        var capture = PayPalJson.First(payments, "captures");
        var authorization = PayPalJson.First(payments, "authorizations");

        string? approveUrl = null;
        if (PayPalJson.Child(order, "links") is { ValueKind: JsonValueKind.Array } links)
        {
            // "payer-action" when the order names PayPal as its payment source, "approve" otherwise.
            approveUrl = links.EnumerateArray()
                .Where(x => PayPalJson.String(x, "rel") is "payer-action" or "approve")
                .Select(x => PayPalJson.String(x, "href"))
                .FirstOrDefault();
        }

        return new PayPalOrder
        {
            Id = PayPalJson.String(order, "id"),
            Status = PayPalJson.String(order, "status"),
            CustomId = PayPalJson.String(unit, "custom_id"),
            ApproveUrl = approveUrl,
            Capture = capture.ValueKind == JsonValueKind.Object ? PayPalPayment.From(capture) : null,
            Authorization = authorization.ValueKind == JsonValueKind.Object ? PayPalPayment.From(authorization) : null,
        };
    }
}

/// <summary>
/// A capture, authorization or refund at PayPal. They share the fields the provider reads.
/// See https://developer.paypal.com/docs/api/payments/v2/
/// </summary>
sealed class PayPalPayment
{
    /// <summary>PayPal's transaction id.</summary>
    public string? Id { get; private init; }

    /// <summary>
    /// Capture: COMPLETED, PENDING, DECLINED, FAILED, REFUNDED, PARTIALLY_REFUNDED.
    /// Authorization: CREATED, PENDING, DENIED, VOIDED, CAPTURED, PARTIALLY_CAPTURED.
    /// </summary>
    public string? Status { get; private init; }

    /// <summary>Why a payment is pending or was declined, when PayPal says so.</summary>
    public string? Reason { get; private init; }

    public decimal? Amount { get; private init; }
    public string? Currency { get; private init; }

    /// <summary>The Ekom payment id, which PayPal copies from the order.</summary>
    public string? CustomId { get; private init; }

    public static PayPalPayment From(JsonElement payment)
    {
        var amount = PayPalJson.Child(payment, "amount");

        return new PayPalPayment
        {
            Id = PayPalJson.String(payment, "id"),
            Status = PayPalJson.String(payment, "status"),
            Reason = PayPalJson.String(PayPalJson.Child(payment, "status_details"), "reason"),
            Amount = PayPalAmount.Parse(PayPalJson.String(amount, "value")),
            Currency = PayPalJson.String(amount, "currency_code"),
            CustomId = PayPalJson.String(payment, "custom_id"),
        };
    }
}
