namespace Ekom.Payments.TeyaConsumerloans;

public static class PaymentSettingsExtensions
{
    public static void SetTeyaConsumerloansSettings(
        this PaymentSettings settings,
        TeyaConsumerloansSettings teyaConsumerloansSettings)
    {
        settings.CustomSettings[typeof(TeyaConsumerloansSettings)] = teyaConsumerloansSettings;
    }
}
