namespace Ekom.Payments.TeyaConsumerloans;

public class TeyaConsumerloansSettings : PaymentSettingsBase<TeyaConsumerloansSettings>
{
    /// <summary>
    /// Base API url for Consumer Loans API v3.
    /// Example: https://api.borgun.is/consumerloans/v3/
    /// </summary>
    public Uri? ApiBaseUrl { get; set; }

    /// <summary>
    /// Merchant or partner user name supplied by Teya/Borgun.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Merchant or partner password supplied by Teya/Borgun.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Base URL for the Teya/Borgun self-service loan portal.
    /// The token returned from /online/token/web is appended as the configured token query parameter.
    /// </summary>
    public Uri? LoanPortalUrl { get; set; }

    /// <summary>
    /// Optional store or merchant identifier expected by the Consumer Loans API.
    /// </summary>
    public string? MerchantId { get; set; }

    /// <summary>
    /// Number of payments for the loan.
    /// </summary>
    public int? NumberOfPayments { get; set; }

    public bool? FlexibleNumberOfPayments { get; set; }

    public int? LoanTypeId { get; set; }

    public int? ProgressValidMinutes { get; set; }

    public int? TokenValidMinutes { get; set; }
}
