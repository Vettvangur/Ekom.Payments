using Newtonsoft.Json;

namespace Ekom.Payments.Stripe;

/// <summary>
/// Settings of the Stripe payment provider. Read from the provider's Umbraco node, then from
/// <c>Ekom:Payments:stripe</c> in configuration, unless set in code with
/// <see cref="PaymentSettingsExtensions.SetStripeSettings"/>.
/// </summary>
public class StripeSettings : PaymentSettingsBase<StripeSettings>
{
    /// <summary>
    /// The Stripe secret API key (<c>sk_live_...</c>, <c>sk_test_...</c>) or a restricted key that may
    /// write Checkout Sessions. Only used while the payment is requested; it is not stored with the payment.
    /// </summary>
    [JsonIgnore]
    public string? SecretKey { get; set; }

    /// <summary>
    /// The signing secret (<c>whsec_...</c>) of the Stripe webhook endpoint that points to
    /// <c>/ekom/payments/striperesponse</c>. Webhooks that are not signed with it record nothing.
    /// </summary>
    public string? WebhookSecret { get; set; }

    /// <summary>
    /// Only reserve the amount when the customer pays. The payment is recorded as authorized and
    /// as captured when it is captured at Stripe, which has to happen before the authorization expires.
    /// </summary>
    public bool UseDelayedCapture { get; set; }

    /// <summary>
    /// Optional number of minutes the Checkout page stays open. Stripe allows 30 minutes to 24 hours
    /// and uses 24 hours when this is empty.
    /// </summary>
    public int? CheckoutExpiresInMinutes { get; set; }

    /// <summary>
    /// Optional Stripe Checkout locale, e.g. "en" or "da". By default the language of the payment is used
    /// when Stripe supports it, otherwise Stripe follows the customer's browser.
    /// </summary>
    public string? Locale { get; set; }

    /// <summary>
    /// Optional comma separated list of Stripe payment method types, e.g. "card,klarna".
    /// Leave empty to offer the payment methods enabled in the Stripe Dashboard.
    /// </summary>
    public string? PaymentMethodTypes { get; set; }
}
