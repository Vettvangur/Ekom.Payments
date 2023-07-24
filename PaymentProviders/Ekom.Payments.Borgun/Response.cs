using System;
using System.ComponentModel.DataAnnotations;

namespace Ekom.Payments.Borgun;

/// <summary>
/// Response data from Borgun Server
/// </summary>
public class Response
{
    /// <summary>
    /// Contains „Ok“
    /// </summary>
    [RegularExpression("[a-zA-Z]+")]
    public string Status { get; set; }

    /// <summary>
    /// Signature that is created by joining together the following parameters with | as separator and using HMAC SHA256 with the merchant secret key to create the checkhash. 
    /// orderid|amount|Currency
    /// <para></para>
    /// (Secret key is issued by Borgun and known only to merchant and Borgun).
    /// <para></para>
    /// See HMAC value creation appendix https://docs.borgun.is/hostedpayments/securepay/#hmac-value-creation
    /// </summary>
    [RegularExpression("[0-9a-zA-Z]+")]
    public string OrderHash { get; set; }

    private string _orderid;
    /// <summary>
    /// Order number created by webshop and sent to payment page during payment initiation
    /// </summary>
    public string OrderId
    {
        get
        {
            return _orderid;
        }
        set
        {
            _orderid = System.Net.WebUtility.HtmlEncode(value);
        }
    }

    /// <summary>
    /// Payment authorization from Borgun
    /// </summary>
    [RegularExpression("[0-9a-zA-Z]+")]
    public string AuthorizationCode { get; set; }

    /// <summary>
    /// Masked creditcard number (558740******2037)
    /// </summary>
    [RegularExpression("[0-9*]+")]
    public string CreditCardNumber { get; set; }

    /// <summary>
    /// Success message is sent on two occations from Borgun to the webshop. 
    /// <para></para>First time is after buyer has successfully paid and is being shown a receipt by Borgun. Note that this url request comes from the Borgun server and is not redirected thorugh the buyer browser, it is thus not in the same active session. 
    /// <para></para>Second time is when the buyer pushes the optional „Back to shop“ button. 
    /// <para></para>
    /// <para></para>The steps are identified by the following values. 
    /// <para></para>„Payment“: Payment has been completed. See section C for more info. 
    /// <para></para>„Confirmation“: Buyer is sent from the payment page back to the webshop.
    /// <para></para>
    /// </summary>
    [RegularExpression("[a-zA-Z]+")]
    public string Step { get; set; }

    private string _reference;
    /// <summary>
    /// Reference can be any string, it is returned with the same value as is sent in. 
    /// It‘s main function is to simplify adaptation to merchant system by containing an external orderid number.
    /// </summary>
    public string Reference
    {
        get
        {
            return _reference;
        }
        set
        {
            _reference = System.Net.WebUtility.HtmlEncode(value);
        }
    }
}
