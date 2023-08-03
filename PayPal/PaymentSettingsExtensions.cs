namespace Ekom.Payments.PayPal;

/// <summary>
/// Helper that facilitates easy addition of a prepopulated instance of your payment providers custom settings
/// prior to calling RequestAsync on f.x. Ekom.Payments.PayPal.Payment
/// </summary>
public static class PaymentSettingsExtensions
{
    public static void SetPayPalSettings(
        this PaymentSettings settings,
        PayPalSettings PayPalSettings)
    {
        settings.CustomSettings[typeof(PayPalSettings)] = PayPalSettings;
    }
}
