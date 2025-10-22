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

    /// <summary>
    /// Set to true to add order number to the payment reference.
    /// </summary>
    public string AddOrderToReference { get; set; }

    /// <summary>
    /// Select the recurring processing model to be used if recurring payments are needed. See https://skjolun.straumur.is/hosted-checkout/optional-parameters/recurring-processing-model
    /// </summary>
    public RecurringProccessingModel? RecurringProcessingModel { get; set; }
}


public enum RecurringProccessingModel
{
    CardOnFile,
    Subscription,
    UnscheduledCardOnFile
}
