namespace Ekom.Payments.Netgiro;

public class NetgiroSettings : PaymentSettingsBase<NetgiroSettings>
{
    [EkomProperty(PropertyEditorType.Store)]
    public Guid ApplicationId { get; set; }

    [EkomProperty(PropertyEditorType.Store)]
    public string Secret { get; set; }

    //[EkomProperty(PropertyEditorType.Store)]
    //public bool? iFrame { get; set; }

    /// <summary>
    /// Dev https://test.netgiro.is/securepay/
    /// Prod https://securepay.netgiro.is/v1/
    /// </summary>
    public Uri PaymentPageUrl { get; set; }

    /// <summary>
    /// Refuse a callback that does not carry <c>NetgiroSignature</c>. Off by default.
    /// <para>
    /// Netgiro signs its callbacks two ways. <c>NetgiroSignature</c> covers the secret, the reference number,
    /// the transaction id, the invoice number, the amount and the status. The older <c>Signature</c> covers
    /// only the secret, the reference number, the confirmation code and the invoice number: it does not say
    /// how much was paid or whether the payment went through. When this is off, a callback with only the
    /// older signature is still recorded as a capture of the payment's amount (with a warning in the log),
    /// so sites whose Netgiro account sends only the older signature keep working.
    /// </para>
    /// <para>
    /// Turn it on (provider node property or <c>Ekom:Payments:netgiro:requireNetgiroSignature</c>) once the
    /// site's Netgiro callbacks carry <c>NetgiroSignature</c>; the log warning above shows when they do not.
    /// The value is stored with each payment when it is requested, so it applies to payments requested
    /// after it was changed.
    /// </para>
    /// </summary>
    public bool RequireNetgiroSignature { get; set; }
}
