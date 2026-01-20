using Ekom.Payments.SiminnPay.Model;

namespace Ekom.Payments.SiminnPay;

public static class PaymentSettingsExtensions
{
    public static void SetSiminnPaySettings(this PaymentSettings settings, SiminnPaySettings siminnPaySettings)
    {
        settings.CustomSettings[typeof(SiminnPaySettings)] = siminnPaySettings;
    }
}
