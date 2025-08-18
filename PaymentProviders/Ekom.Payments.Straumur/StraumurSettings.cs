namespace Ekom.Payments.Straumur;

public class StraumurSettings : PaymentSettingsBase<StraumurSettings>
{
    public string TerminalIdenitifer { get; set; }

    public string ApiKey { get; set; }

    public string HmacKey { get; set; }
    /// <summary>
    /// Dev https://checkout-api.staging.straumur.is/api/v1/hostedcheckout
    /// Prod https://checkout-api.straumur.is/api/v1/hostedcheckout
    /// </summary>
    public Uri PaymentPageUrl { get; set; }

    public string AddOrderToReference { get; set; }

}
