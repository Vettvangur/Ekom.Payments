using Newtonsoft.Json;

namespace Ekom.Payments.PayPal;

/// <summary>
/// Settings of the PayPal payment provider. Read from the provider's Umbraco node, then from
/// <c>Ekom:Payments:payPal</c> in configuration, unless set in code with
/// <see cref="PaymentSettingsExtensions.SetPayPalSettings"/>.
/// </summary>
public class PayPalSettings : PaymentSettingsBase<PayPalSettings>
{
    /// <summary>
    /// The client id of the PayPal REST app.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// The secret of the PayPal REST app. It is not stored with the payment: when PayPal sends the
    /// customer back, and for webhooks, it is read from the provider's node or configuration again.
    /// </summary>
    [JsonIgnore]
    public string? ClientSecret { get; set; }

    /// <summary>
    /// The id of the PayPal webhook that points to <c>/ekom/payments/paypalresponse/webhook</c>.
    /// Without it webhooks are refused and only customers who return from PayPal complete their payment.
    /// </summary>
    public string? WebhookId { get; set; }

    /// <summary>
    /// Use PayPal's sandbox instead of the live PayPal.
    /// </summary>
    public bool Sandbox { get; set; }

    /// <summary>
    /// Only reserve the amount when the customer pays. The payment is recorded as authorized and
    /// as captured when it is captured at PayPal, which has to happen before the authorization expires.
    /// </summary>
    public bool UseDelayedCapture { get; set; }

    /// <summary>
    /// Optional name of the shop on the PayPal pages, instead of the name of the PayPal account.
    /// </summary>
    public string? BrandName { get; set; }

    /// <summary>
    /// Optional language of the PayPal pages as a BCP 47 tag, e.g. "en-GB" or "da-DK".
    /// By default PayPal chooses the language for the customer.
    /// </summary>
    public string? Locale { get; set; }
}
