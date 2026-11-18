namespace Ekom.Payments.PayPal;

/// <summary>
/// Sets PayPal settings in code, ahead of the provider's Umbraco node and configuration.
/// </summary>
public static class PaymentSettingsExtensions
{
    /// <summary>
    /// Uses the given PayPal settings for the payment. Values left empty are still read from the
    /// provider's Umbraco node and configuration. The client secret is always needed there as well:
    /// it is read again when PayPal sends the customer back.
    /// </summary>
    public static void SetPayPalSettings(
        this PaymentSettings settings,
        PayPalSettings payPalSettings)
    {
        settings.CustomSettings[typeof(PayPalSettings)] = payPalSettings;
    }
}
