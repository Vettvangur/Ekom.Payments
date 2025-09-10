namespace Ekom.Payments.AltaPay;

public static class PaymentSettingsExtensions
{
    public static void SetStraumurSettings(
        this PaymentSettings settings, 
        AltaSettings straumurSettings)
    {
        settings.CustomSettings[typeof(AltaSettings)] = straumurSettings;
    }
}
