
namespace Ekom.Payments.PayPal;

/// <summary>
/// Response data from PayPal Server
/// See https://developer.paypal.com/api/nvp-soap/ipn/IPNandPDTVariables/#link-ipnandpdtvariables
/// <remarks>
/// Note that we ignore some elements from the response that will be multiples when the payment is for multiple items.
/// These elements are: item_numberx, item_namex, mc_gross_x, quantityx (where x is a number).
/// </remarks>
/// </summary>
public class Response
{
    public string Address_City { get; set; } = string.Empty;
    public string Address_Country { get; set; } = string.Empty;
    public string Address_Country_Code { get; set; } = string.Empty;
    public string Address_Name { get; set; } = string.Empty;
    public string Address_State { get; set; } = string.Empty;
    public string Address_Status { get; set; } = string.Empty;
    public string Address_Street { get; set; } = string.Empty;
    public string Address_Zip { get; set; } = string.Empty;
    public string Business { get; set; } = string.Empty;
    public string Charset { get; set; } = string.Empty;
    public string Custom { get; set; } = string.Empty;
    public string Discount { get; set; } = string.Empty;
    public string First_Name { get; set; } = string.Empty;
    public string Insurance_Amount { get; set; } = string.Empty;
    public string Invoice { get; set; } = string.Empty;
    public string Ipn_Track_Id { get; set; } = string.Empty;
    public string Last_Name { get; set; } = string.Empty;
    public string Mc_Currency { get; set; } = string.Empty;
    public string Mc_Fee { get; set; } = string.Empty;
    public string Mc_Gross { get; set; } = string.Empty;
    public string Notify_Version { get; set; } = string.Empty;
    public string Num_Cart_Items { get; set; } = string.Empty;
    public string Payer_Business_Name { get; set; } = string.Empty;
    public string Payer_Email { get; set; } = string.Empty;
    public string Payer_Id { get; set; } = string.Empty;
    public string Payer_Status { get; set; } = string.Empty;
    public string Payment_Date { get; set; } = string.Empty;
    public string Payment_Fee { get; set; } = string.Empty;
    public string Payment_Gross { get; set; } = string.Empty;
    public string Payment_Status { get; set; } = string.Empty;
    public string Payment_Type { get; set; } = string.Empty;
    public string Protection_Eligibility { get; set; } = string.Empty;
    public string Receiver_Email { get; set; } = string.Empty;
    public string Receiver_Id { get; set; } = string.Empty;
    public string Residence_Country { get; set; } = string.Empty;
    public string Shipping_Discount { get; set; } = string.Empty;
    public string Shipping_Method { get; set; } = string.Empty;
    public string Test_Ipn { get; set; } = string.Empty;
    public string Transaction_Subject { get; set; } = string.Empty;
    public string Txn_Id { get; set; } = string.Empty;
    public string Txn_Type { get; set; } = string.Empty;
    public string Verify_Sign { get; set; } = string.Empty;
}
