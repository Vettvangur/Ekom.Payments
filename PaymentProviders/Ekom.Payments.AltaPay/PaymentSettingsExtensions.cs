using Ekom.Payments.AltaPay.Model;

namespace Ekom.Payments.AltaPay;

public static class PaymentSettingsExtensions
{
    public static void SetAltaPaySettings(
        this PaymentSettings settings, 
        AltaSettings altaPaySettings)
    {
        settings.CustomSettings[typeof(AltaSettings)] = altaPaySettings;
    }
}
