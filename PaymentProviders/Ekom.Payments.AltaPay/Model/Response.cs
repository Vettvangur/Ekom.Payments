using System.Xml;
using System.Xml.Serialization;

namespace Ekom.Payments.AltaPay.Model;

[XmlRoot("APIResponse")]
public class APIResponse
{
    [XmlElement("Header")]
    public Header Header { get; set; }

    [XmlElement("Body")]
    public Body Body { get; set; }
}

public class Header
{
    [XmlElement("Date")]
    public string Date { get; set; }

    [XmlElement("Path")]
    public string Path { get; set; }

    [XmlElement("ErrorCode")]
    public string ErrorCode { get; set; }

    [XmlElement("ErrorMessage")]
    public string ErrorMessage { get; set; }
}

public class Body
{
    [XmlElement("Result")]
    public string Result { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public PaymentStatus PaymentStatus => Enum.TryParse<PaymentStatus>(Result, out var status) ? status : PaymentStatus.Error;

    [XmlArray("Transactions")]
    [XmlArrayItem("Transaction")]
    public List<Transaction> Transactions { get; set; }
}

public class Transaction
{
    [XmlElement("TransactionId")]
    public string TransactionId { get; set; }

    [XmlElement("PaymentId")]
    public string PaymentId { get; set; }

    [XmlElement("AuthType")]
    public string AuthType { get; set; }

    [XmlElement("CardStatus")]
    public string CardStatus { get; set; }

    [XmlElement("ShopOrderId")]
    public string ShopOrderId { get; set; }

    [XmlElement("ReservedAmount")]
    public decimal ReservedAmount { get; set; }

    [XmlElement("CapturedAmount")]
    public decimal CapturedAmount { get; set; }

    [XmlElement("RefundedAmount")]
    public decimal RefundedAmount { get; set; }

    [XmlElement("MerchantCurrencyAlpha")]
    public string MerchantCurrencyAlpha { get; set; }

    [XmlElement("CreditCardMaskedPan")]
    public string CreditCardMaskedPan { get; set; }
}

public enum PaymentStatus
{
    Success,
    Fail,
    Open,
    Redirect,
    PartialSuccess,
    Error
}
