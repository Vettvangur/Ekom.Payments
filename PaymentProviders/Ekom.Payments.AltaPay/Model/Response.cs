using Microsoft.AspNetCore.Mvc;
using System.Xml;
using System.Xml.Serialization;

namespace Ekom.Payments.AltaPay.Model;

public class PaymentResponse
{
    [FromForm(Name = "shop_orderid")]
    public Guid ShopOrderId { get; set; }
    public int Currency { get; set; }
    public string Type { get; set; }

    [FromForm(Name = "embedded_window")]
    public int EmbeddedWindow { get; set; }
    public decimal Amount { get; set; }

    [FromForm(Name = "transaction_id")]
    public long TransactionId { get; set; }

    [FromForm(Name = "payment_id")]
    public Guid PaymentId { get; set; }
    public string Nature { get; set; }

    [FromForm(Name = "require_capture")]
    public bool RequireCapture { get; set; }

    [FromForm(Name = "payment_status")]
    public string PaymentStatus { get; set; }

    [FromForm(Name = "masked_credit_card")]
    public string MaskedCreditCard { get; set; }

    [FromForm(Name = "blacklist_token")]
    public string BlacklistToken { get; set; }

    [FromForm(Name = "credit_card_token")]
    public string CreditCardToken { get; set; }
    public string Status { get; set; }
    public string Xml { get; set; }
    public string Checksum { get; set; }

    public APIResponse? GetApiResponse()
    {
        var serializer = new XmlSerializer(typeof(APIResponse));
        try
        {
            using var stringReader = new StringReader(Xml);
            using var xmlReader = XmlReader.Create(stringReader);
            return (APIResponse)serializer.Deserialize(xmlReader);
        }
        catch (Exception ex)
        {
            return null;
        }
    }
}

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
