namespace Ekom.Payments.SiminnPay.Model
{
    public class SiminnPayStatusView
    {
        public SiminnPayStatus PayStatus { get; set; }
        public decimal OriginalAmount { get; set; }
        public decimal Amount { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime Expires { get; set; }
        public Guid NetPaymentOrderId { get; set; }

        public SiminnPayStatusView(SiminnPayOrder siminnPayOrder)
        {
            PayStatus = siminnPayOrder.Status;
            CreatedAt = siminnPayOrder.Created;
            Expires = siminnPayOrder.Expires.Value;
            Amount = siminnPayOrder.Amount;
            OriginalAmount = siminnPayOrder.OriginalAmount;
            NetPaymentOrderId = siminnPayOrder.NetPaymentOrderId;
        }
    }
}
