namespace Ekom.Payments.PayTrail.Models;

public class PayTrailCallback
{
    public string Account { get; set; } = string.Empty;

    public string Algorithm { get; set; } = string.Empty;

    public int Amount { get; set; }

    public string Stamp { get; set; } = string.Empty;

    public string Reference { get; set; } = string.Empty;

    public string TransactionId { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;

    public string Signature { get; set; } = string.Empty;
}
