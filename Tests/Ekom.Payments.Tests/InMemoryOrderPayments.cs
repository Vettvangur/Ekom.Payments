using Ekom.Models.Orders;
using Ekom.Models.Payments;
using Ekom.Services;

namespace Ekom.Payments.Tests;

/// <summary>
/// Stands in for Ekom's order payments in provider tests. Like the real service it stores a
/// transaction once per idempotency key and refuses transactions of unknown payments.
/// </summary>
sealed class InMemoryOrderPayments : IOrderPayments
{
    readonly HashSet<string> _keys = new(StringComparer.Ordinal);

    public List<OrderPayment> Payments { get; } = [];

    /// <summary>The transactions that were stored, without repeated deliveries.</summary>
    public List<PaymentTransaction> Recorded { get; } = [];

    public IEnumerable<PaymentTransaction> Captures(Guid paymentId)
        => Recorded.Where(x => x.PaymentId == paymentId && x.Type == PaymentTransactionType.Captured);

    public Task<OrderPayment> CreateAsync(NewOrderPayment payment, CancellationToken ct = default)
    {
        var row = new OrderPayment
        {
            Sequence = Payments.Count + 1,
            PaymentId = payment.PaymentId == Guid.Empty ? Guid.NewGuid() : payment.PaymentId,
            OrderUniqueId = payment.OrderUniqueId,
            OrderNumber = payment.OrderNumber,
            AttemptId = payment.AttemptId,
            StoreAlias = payment.StoreAlias,
            ProviderName = payment.ProviderName,
            ProviderType = payment.ProviderType,
            Amount = payment.Amount,
            Currency = payment.Currency,
            ProviderReference = payment.ProviderReference,
            Settings = payment.Settings,
            ProviderData = payment.ProviderData,
        };
        Payments.Add(row);
        return Task.FromResult(row);
    }

    public Task<PaymentRecordResult> RecordAsync(PaymentTransaction transaction, CancellationToken ct = default)
    {
        if (Payments.All(x => x.PaymentId != transaction.PaymentId)) throw new InvalidOperationException("Unknown payment.");
        var key = string.IsNullOrWhiteSpace(transaction.IdempotencyKey)
            ? $"{transaction.PaymentId:N}:{transaction.Type}:{transaction.TransactionId}"
            : transaction.IdempotencyKey;
        var recorded = _keys.Add(key);
        if (recorded) Recorded.Add(transaction);
        return Task.FromResult(new PaymentRecordResult(recorded, PaymentStatus.Paid, null));
    }

    public Task<OrderPayment?> GetAsync(Guid paymentId, CancellationToken ct = default)
        => Task.FromResult(Payments.FirstOrDefault(x => x.PaymentId == paymentId));

    public Task<OrderPayment?> GetByProviderReferenceAsync(string providerName, string providerReference, CancellationToken ct = default)
        => Task.FromResult(Payments.LastOrDefault(x => x.ProviderReference == providerReference && x.ProviderName == providerName));

    public Task<OrderPaymentsView> GetForOrderAsync(Guid orderId, CancellationToken ct = default)
        => Task.FromResult(new OrderPaymentsView(null, [], []));

    public Task<Guid?> FindOrderAsync(string paymentIdOrTransactionId, CancellationToken ct = default)
        => Task.FromResult<Guid?>(null);
}
