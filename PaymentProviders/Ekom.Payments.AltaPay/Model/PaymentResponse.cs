namespace Ekom.Payments.AltaPay.Model;

public class PaymentResponse
{
    public required string PaymentId { get; set; }
    public required string ShopOrderId { get; set; }
    public string ExternalPaymentId { get; set; }
    public string Status { get; set; }
    public string Type { get; set; }
    public string Url { get; set; }
}
