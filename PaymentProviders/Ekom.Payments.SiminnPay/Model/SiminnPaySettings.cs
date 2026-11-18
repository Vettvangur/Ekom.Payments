namespace Ekom.Payments.SiminnPay.Model;

public class SiminnPaySettings : PaymentSettingsBase<SiminnPaySettings>
{
    internal const string DefaultPaymentType = "loan";
    internal const int DefaultTimeToLive = 60;
    internal const string DefaultCurrency = "ISK";

    public string ApiKey { get; set; }
    public string ApiUrl { get; set; }
    public string Terminal { get; set; }
    public Uri BaseAddress { get; set; }
    public string? HostOverride { get; set; }
    public string? PaymentFormUrl { get; set; }
    public string? CustomerInformationSharedSecret { get; set; }
    public bool RestrictToLoan { get; set; }

    /// <summary>
    /// "loan" when not set in code, on the payment provider node or in configuration.
    /// </summary>
    public string PaymentType { get; set; }

    /// <summary>
    /// How long the customer has to answer the payment request, sent to Siminn Pay as timeToLive.
    /// Read as seconds when the status endpoint reports when the request expires.
    /// 60 when not set (or not positive) in code, on the payment provider node or in configuration.
    /// </summary>
    public int TimeToLive { get; set; }
    public string ReferenceId { get; set; }

    /// <summary>
    /// "ISK" when not set in code, on the payment provider node or in configuration.
    /// </summary>
    public string Currency { get; set; }
    public string Secret { get; set; }

    /// <summary>
    /// Ekom fills a setting from the payment provider node or configuration only while it is empty,
    /// so the defaults are applied after that, never as property initializers.
    /// </summary>
    internal SiminnPaySettings ApplyDefaults()
    {
        if (string.IsNullOrWhiteSpace(PaymentType)) PaymentType = DefaultPaymentType;
        if (TimeToLive <= 0) TimeToLive = DefaultTimeToLive;
        if (string.IsNullOrWhiteSpace(Currency)) Currency = DefaultCurrency;
        return this;
    }
}
