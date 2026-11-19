namespace Ekom.Payments.Teya;

/// <summary>
/// Helper that facilitates easy addition of a prepopulated instance of your payment providers custom settings
/// prior to calling RequestAsync on f.x. Ekom.Payments.Teya.Payment
/// </summary>
public static class PaymentSettingsExtensions
{
    public static void SetTeyaSettings(
        this PaymentSettings settings, 
        TeyaSettings TeyaSettings)
    {
        settings.CustomSettings[typeof(TeyaSettings)] = TeyaSettings;
    }
}
