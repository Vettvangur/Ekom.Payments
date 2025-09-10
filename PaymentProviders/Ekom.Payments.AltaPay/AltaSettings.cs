namespace Ekom.Payments.AltaPay;

public class AltaSettings : PaymentSettingsBase<AltaSettings>
{
    public string TerminalIdenitifer { get; set; }

    public string ApiUserName { get; set; }
    public string ApiPassword { get; set; }

    public string HmacKey { get; set; }
    /// <summary>
    /// Dev https://checkout-api.staging.straumur.is/api/v1/hostedcheckout
    /// Prod https://checkout-api.straumur.is/api/v1/hostedcheckout
    /// </summary>
    public Uri PaymentPageUrl { get; set; }
    public Uri AuthenticationUrl { get; set; }
    public Uri SessionUrl { get; set; }

    public string AddOrderToReference { get; set; }

}
