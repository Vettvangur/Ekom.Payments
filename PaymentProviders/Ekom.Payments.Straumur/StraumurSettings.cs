namespace Ekom.Payments.Straumur;

public class StraumurSettings : PaymentSettingsBase<StraumurSettings>
{
    public string ReportUrl { get; set; }
    
    [EkomProperty(PropertyEditorType.Store)]
    public string TerminalIdenitifer { get; set; }

    [EkomProperty(PropertyEditorType.Store)]
    public string ApiKey { get; set; }

    [EkomProperty(PropertyEditorType.Store)]
    public string HmacKey { get; set; }
    /// <summary>
    /// Dev https://checkout-api.staging.straumur.is/api/v1/hostedcheckout
    /// Prod https://greidslusida.valitor.is
    /// </summary>
    public Uri PaymentPageUrl { get; set; }

}
