namespace Ekom.Payments.Borgun;

public class BorgunSettings : PaymentSettingsBase<BorgunSettings>
{
    public string ReportUrl { get; set; }

    [EkomProperty(PropertyEditorType.Store)]
    public string MerchantId { get; set; }

    [EkomProperty(PropertyEditorType.Store)]
    public string SecretCode { get; set; }

    [EkomProperty(PropertyEditorType.Store)]
    public int PaymentGatewayId { get; set; }

    /// <summary>
    /// Dev https://test.borgun.is/SecurePay/default.aspx
    /// Prod https://securepay.borgun.is/SecurePay/default.aspx
    /// </summary>
    public Uri PaymentPageUrl { get; set; }

    [EkomProperty(PropertyEditorType.Store)]
    public bool SkipReceipt { get; set; }

    [EkomProperty(PropertyEditorType.Store)]
    public string MerchantEmail { get; set; }

    [EkomProperty(PropertyEditorType.Store)]
    public Uri? MerchantLogo { get; set; }

    [EkomProperty(PropertyEditorType.Store)]
    public bool RequireCustomerInformation { get; set; }
    public string Language { get; set; }
}
