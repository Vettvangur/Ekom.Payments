namespace Ekom.Payments.TeyaConsumerloans.Models;

public class PollingRequest
{
    /// <summary>The payment whose loan is being monitored.</summary>
    public required Guid PaymentId { get; set; }
    public required string Token { get; set; }
    public required Uri RedirectUrl { get; set; }
    public required TeyaConsumerloansSettings Settings { get; set; }

    internal DateTimeOffset? RecoveryDeadlineUtc { get; set; }
}
