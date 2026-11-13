using Ekom.Payments.SiminnPay.apimodels;

namespace Ekom.Payments.SiminnPay.Model;

public class SiminnPayStatusView
{
    public SiminnPayStatus PayStatus { get; set; }
    public decimal Amount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime Expires { get; set; }
    public Guid NetPaymentOrderId { get; set; }

    public SiminnPayStatusView(Ekom.Models.Payments.OrderPayment payment, GetPaymentOrderStatusResponse response)
    {
        PayStatus = response.Status;
        CreatedAt = payment.CreatedUtc;
        Expires = payment.CreatedUtc.AddMinutes(1);
        Amount = payment.Amount;
        NetPaymentOrderId = payment.PaymentId;
    }
}
