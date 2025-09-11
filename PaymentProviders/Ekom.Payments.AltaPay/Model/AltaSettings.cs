namespace Ekom.Payments.AltaPay.Model;

public class AltaSettings : PaymentSettingsBase<AltaSettings>
{
    public string ApiUserName { get; set; }
    public string ApiPassword { get; set; }

    public string HmacKey { get; set; }

    public Uri AuthenticationUrl { get; set; }
    public Uri SessionUrl { get; set; }
}
