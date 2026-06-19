namespace Ekom.Payments.TeyaConsumerloans.Models;

public class PollingRequest
{
    public required Guid OrderId { get; set; }
    public required string Token { get; set; }
    public required Uri RedirectUrl { get; set; }
    public required TeyaConsumerloansSettings Settings { get; set; }
}
