namespace Ekom.Payments.PayTrail;

public class PayTrailSettings : PaymentSettingsBase<PayTrailSettings>
{
    public string AccountId { get; set; } = null!;

    public string SecretKey { get; set; } = null!;

    /// <summary>
    /// Dev/prod https://services.paytrail.com
    /// </summary>
    public Uri ApiBaseUrl { get; set; } = null!;

    public string Algorithm { get; set; } = null!;

    public string PlatformName { get; set; } = null!;
}
