namespace Ekom.Payments.TeyaConsumerloans;

public class TeyaConsumerloansSettings : PaymentSettingsBase<TeyaConsumerloansSettings>
{
    /// <summary>
    /// Base API url for Consumer Loans API v3.
    /// Example: https://api.borgun.is/consumerloans/v3/
    /// </summary>
    public Uri ApiBaseUrl { get; set; } = new("https://api.borgun.is/consumerloans/v3/");

    /// <summary>
    /// Merchant or partner user name supplied by Teya/Borgun.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Merchant or partner password supplied by Teya/Borgun.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Optional API key if the merchant agreement uses an API-key based integration.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Base URL for the Teya/Borgun self-service loan portal.
    /// The token returned from /online/token/web is appended as the configured token query parameter.
    /// </summary>
    public Uri LoanPortalUrl { get; set; } = new("https://mitt.borgun.is/consumerloans/");

    /// <summary>
    /// Query-string parameter name used when redirecting customers to the loan portal.
    /// </summary>
    public string LoanPortalTokenQueryParameter { get; set; } = "token";

    /// <summary>
    /// Optional store or merchant identifier expected by the Consumer Loans API.
    /// </summary>
    public string? MerchantId { get; set; }

    /// <summary>
    /// Optional store location identifier expected by the Consumer Loans API.
    /// </summary>
    public string? StoreId { get; set; }

    /// <summary>
    /// Optional campaign or product code for the financing product.
    /// </summary>
    public string? ProductCode { get; set; }
}
