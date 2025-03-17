namespace Ekom.Payments.Straumur;

public static class PaymentSettingsExtensions
{
    public static void SetStraumurSettings(
        this PaymentSettings settings, 
        StraumurSettings straumurSettings)
    {
        settings.CustomSettings[typeof(StraumurSettings)] = straumurSettings;
    }
}
