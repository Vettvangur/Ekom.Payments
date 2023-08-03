using System.Globalization;

namespace Ekom.Payments.PayPal;

public class PayPalSettings : PaymentSettingsBase<PayPalSettings>
{
    /// <summary>
    /// Dev https://www.sandbox.paypal.com/cgi-bin/webscr
    /// Prod https://www.paypal.com/cgi-bin/webscr
    /// </summary>
    public Uri PaymentPageUrl { get; set; }

    public string? ImageUrl { get; set; }

    public CultureInfo CultureInfo { get; set; } = CultureInfo.InvariantCulture;

    /// <summary>
    /// GM: 
    /// PayPal ID or an email address associated with the store's PayPal account
    /// </summary>
    public string PayPalAccount { get; set; }
}
