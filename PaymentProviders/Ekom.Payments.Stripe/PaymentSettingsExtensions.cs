namespace Ekom.Payments.Stripe;

/// <summary>
/// Sets Stripe settings in code, ahead of the provider's Umbraco node and configuration.
/// </summary>
public static class PaymentSettingsExtensions
{
    /// <summary>
    /// Uses the given Stripe settings for the payment. Values left empty are still read from the
    /// provider's Umbraco node and configuration.
    /// </summary>
    public static void SetStripeSettings(
        this PaymentSettings settings,
        StripeSettings stripeSettings)
    {
        settings.CustomSettings[typeof(StripeSettings)] = stripeSettings;
    }
}
