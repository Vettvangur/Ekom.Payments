using Microsoft.Extensions.Logging;

namespace Ekom.Payments.TeyaConsumerloans;

/// <summary>Business-facing activity for an individual payment attempt.</summary>
public sealed class LoanActivityEventArgs : EventArgs
{
    public required Guid PaymentId { get; init; }
    public required PaymentSettings PaymentSettings { get; init; }
    public required string Message { get; init; }
    public LogLevel Level { get; init; } = LogLevel.Information;
    public bool IsSuccess { get; init; }
}
