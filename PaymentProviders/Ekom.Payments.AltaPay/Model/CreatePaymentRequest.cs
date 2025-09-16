namespace Ekom.Payments.AltaPay.Model;

public class CreatePaymentRequest
{
    public string PaymentMethodId { get; set; }
    public string SessionId { get; set; }
}

public class CreateMerchantPaymentRequest
{
    public string Terminal { get; set; }
    public string OrderId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "ISK";
    public string CallbackOk { get; set; }
    public string CallbackFail { get; set; }
    public string CallbackNotification { get; set; }
}
