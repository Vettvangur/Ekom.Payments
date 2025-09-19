namespace Ekom.Payments.AltaPay.Model;

public class AltaSettings : PaymentSettingsBase<AltaSettings>
{
    public string ApiUserName { get; set; }

    public string ApiPassword { get; set; }

    public string Terminal { get; set; }

    public Uri BaseAddress { get; set; }
    public Uri? PaymentFormUrl { get; set; }
    public string HostOverride { get; set; }
}
