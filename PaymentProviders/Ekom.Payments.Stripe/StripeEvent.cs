using System.Text.Json;

namespace Ekom.Payments.Stripe;

/// <summary>
/// A Stripe snapshot event: something that happened to a Checkout Session or a charge.
/// See https://docs.stripe.com/api/events/object
/// </summary>
sealed class StripeEvent
{
    public const string CheckoutCompleted = "checkout.session.completed";
    public const string CheckoutAsyncPaymentSucceeded = "checkout.session.async_payment_succeeded";
    public const string CheckoutAsyncPaymentFailed = "checkout.session.async_payment_failed";
    public const string CheckoutExpired = "checkout.session.expired";
    public const string ChargeCaptured = "charge.captured";
    public const string ChargeRefunded = "charge.refunded";
    public const string ChargeExpired = "charge.expired";

    /// <summary>The events that say something about an Ekom payment. Every other event is ignored.</summary>
    static readonly HashSet<string> _handled = new(StringComparer.Ordinal)
    {
        CheckoutCompleted, CheckoutAsyncPaymentSucceeded, CheckoutAsyncPaymentFailed, CheckoutExpired,
        ChargeCaptured, ChargeRefunded, ChargeExpired,
    };

    JsonElement _object;
    JsonElement _previous;

    public string? Id { get; private init; }
    public string Type { get; private init; } = string.Empty;
    public DateTime? CreatedUtc { get; private init; }
    public bool LiveMode { get; private init; }

    public bool IsHandled => _handled.Contains(Type);

    /// <summary>True for events about a Checkout Session, false for events about a charge.</summary>
    public bool IsCheckoutSession => Type.StartsWith("checkout.session.", StringComparison.Ordinal);

    /// <summary>The id of the Checkout Session or charge the event is about.</summary>
    public string? ObjectId => String(_object, "id");

    /// <summary>The Stripe payment intent, which the session and all of its charges share.</summary>
    public string? PaymentIntent => String(_object, "payment_intent");

    /// <summary>Checkout Session: paid, unpaid or no_payment_required.</summary>
    public string? PaymentStatus => String(_object, "payment_status");

    public string? Currency => String(_object, "currency")?.ToUpperInvariant();

    /// <summary>Checkout Session: what the customer was asked to pay, in minor units.</summary>
    public long? AmountTotal => Number(_object, "amount_total");

    /// <summary>Charge: the amount, in minor units.</summary>
    public long? Amount => Number(_object, "amount");
    public long? AmountCaptured => Number(_object, "amount_captured");

    /// <summary>Charge: everything refunded so far, in minor units.</summary>
    public long? AmountRefunded => Number(_object, "amount_refunded");

    /// <summary>Charge: what had been refunded before this event, when Stripe says so.</summary>
    public long? PreviousAmountRefunded => Number(_previous, "amount_refunded");

    /// <summary>Charge: false while the amount is only reserved.</summary>
    public bool? Captured => Boolean(_object, "captured");
    public bool? Refunded => Boolean(_object, "refunded");

    public string? PaymentMethodType => String(Child(_object, "payment_method_details"), "type");
    public string? CardBrand => String(Card, "brand");
    public string? CardLast4 => String(Card, "last4");

    JsonElement Card => Child(Child(_object, "payment_method_details"), "card");

    /// <summary>
    /// The Ekom payment the event is about. A Checkout Session carries it as its client reference,
    /// a charge in the metadata it inherits from the session's payment intent.
    /// </summary>
    public bool TryGetPaymentId(out Guid paymentId)
        => Guid.TryParse(String(Child(_object, "metadata"), Payment.PaymentIdMetadataKey), out paymentId)
        || Guid.TryParse(String(_object, "client_reference_id"), out paymentId);

    /// <summary>Reads a webhook body. False when it is not a Stripe event.</summary>
    public static bool TryParse(string? payload, out StripeEvent stripeEvent)
    {
        stripeEvent = new StripeEvent();

        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            var type = String(root, "type");
            var data = Child(root, "data");
            var obj = Child(data, "object");

            if (string.IsNullOrEmpty(type) || obj.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            stripeEvent = new StripeEvent
            {
                Id = String(root, "id"),
                Type = type,
                CreatedUtc = Number(root, "created") is long created
                    ? DateTimeOffset.FromUnixTimeSeconds(created).UtcDateTime
                    : null,
                LiveMode = Boolean(root, "livemode") == true,
                // Cloned so the values outlive the parsed document.
                _object = obj.Clone(),
                _previous = Child(data, "previous_attributes") is { ValueKind: JsonValueKind.Object } previous
                    ? previous.Clone()
                    : default,
            };

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            // "created" is not a time.
            return false;
        }
    }

    static JsonElement Child(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var child) ? child : default;

    static string? String(JsonElement element, string name)
    {
        var value = Child(element, name);

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    static long? Number(JsonElement element, string name)
    {
        var value = Child(element, name);

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
    }

    static bool? Boolean(JsonElement element, string name)
        => Child(element, name).ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
}
