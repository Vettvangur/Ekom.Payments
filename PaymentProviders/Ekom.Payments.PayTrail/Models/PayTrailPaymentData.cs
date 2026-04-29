namespace Ekom.Payments.PayTrail.Models;

public class PayTrailPaymentData
{
    public string TransactionId { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public Dictionary<string, string> Parameters { get; set; } = new();
}
