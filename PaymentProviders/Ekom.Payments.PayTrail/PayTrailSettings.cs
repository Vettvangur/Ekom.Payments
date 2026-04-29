namespace Ekom.Payments.PayTrail;

public class PayTrailSettings : PaymentSettingsBase<PayTrailSettings>
{
    public string AccountId { get; set; } = string.Empty;

    public string SecretKey { get; set; } = string.Empty;

    /// <summary>
    /// Dev/prod https://services.paytrail.com
    /// </summary>
    public Uri ApiBaseUrl { get; set; } = new("https://services.paytrail.com");

    public string Algorithm { get; set; } = "sha256";

    public string PlatformName { get; set; } = "ekom-vettvangur";
}
