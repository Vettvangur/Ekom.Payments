namespace Ekom.Payments.PayTrail;

public static class PaymentSettingsExtensions
{
    public static void SetPayTrailSettings(
        this PaymentSettings settings,
        PayTrailSettings payTrailSettings)
    {
        settings.CustomSettings[typeof(PayTrailSettings)] = payTrailSettings;
    }
}
