using Ekom.Payments.SiminnPay.apimodels;

namespace Ekom.Payments.SiminnPay.Model;

public class SiminnPayStatusView
{
    public SiminnPayStatus PayStatus { get; set; }
    public decimal Amount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime Expires { get; set; }
    public Guid NetPaymentOrderId { get; set; }

    public SiminnPayStatusView(OrderStatus order, GetPaymentOrderStatusResponse response)
    {
        PayStatus = response.Status;
        CreatedAt = order.Date;
        Expires = order.Date.AddMinutes(1);
        Amount = order.Amount;
        NetPaymentOrderId = order.UniqueId;
    }
}
