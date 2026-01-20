namespace Ekom.Payments.SiminnPay.Model;

public class SiminnPaySettings : PaymentSettingsBase<SiminnPaySettings>
{
    public string ApiKey { get; set; }
    public string ApiUrl { get; set; }
    public string Terminal { get; set; }
    public Uri BaseAddress { get; set; }
    public string? HostOverride { get; set; }
    public string? PaymentFormUrl { get; set; }
    public string? CustomerInformationSharedSecret { get; set; }
    public bool RestrictToLoan { get; set; }
    public string ReferenceId { get; set; }
    public string Currency { get; set; } = "ISK";
    public string Secret { get; set; }
}
