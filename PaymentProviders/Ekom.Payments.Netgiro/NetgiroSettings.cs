namespace Ekom.Payments.Netgiro;

public class NetgiroSettings : PaymentSettingsBase<NetgiroSettings>
{
    public Guid ApplicationId { get; set; }

    [EkomProperty(PropertyEditorType.Store)]
    public string Secret { get; set; }

    [EkomProperty(PropertyEditorType.Store)]
    public bool? iFrame { get; set; }

    /// <summary>
    /// Dev https://test.netgiro.is/securepay/
    /// Prod https://securepay.netgiro.is/v1/
    /// </summary>
    public Uri PaymentPageUrl { get; set; }
}
