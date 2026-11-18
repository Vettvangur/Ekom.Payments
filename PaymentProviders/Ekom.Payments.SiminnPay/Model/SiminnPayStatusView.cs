using Ekom.Payments.SiminnPay.apimodels;

namespace Ekom.Payments.SiminnPay.Model;

public class SiminnPayStatusView
{
    public SiminnPayStatus PayStatus { get; set; }
    public decimal Amount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime Expires { get; set; }
    public Guid NetPaymentOrderId { get; set; }

    /// <summary>
    /// Expires one minute after the payment was created, the default time to live.
    /// </summary>
    public SiminnPayStatusView(Ekom.Models.Payments.OrderPayment payment, GetPaymentOrderStatusResponse response)
        : this(payment, response, SiminnPaySettings.DefaultTimeToLive)
    {
    }

    /// <param name="payment">The payment</param>
    /// <param name="response">Siminn Pay's status of the payment order</param>
    /// <param name="timeToLive">
    /// The time to live the payment order was requested with, in seconds. The default is used when it is not positive.
    /// </param>
    public SiminnPayStatusView(Ekom.Models.Payments.OrderPayment payment, GetPaymentOrderStatusResponse response, int timeToLive)
    {
        PayStatus = response.Status;
        CreatedAt = payment.CreatedUtc;
        Expires = payment.CreatedUtc.AddSeconds(timeToLive > 0 ? timeToLive : SiminnPaySettings.DefaultTimeToLive);
        Amount = payment.Amount;
        NetPaymentOrderId = payment.PaymentId;
    }
}
