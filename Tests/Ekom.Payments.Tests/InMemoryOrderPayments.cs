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

    /// <summary>Makes completing the order's checkout fail after a capture was stored.</summary>
    public bool FailCheckoutCompletion { get; set; }

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
            CreatedUtc = payment.CreatedUtc ?? DateTime.UtcNow,
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
        // Like Ekom: the transaction is stored first, completing the order's checkout comes after.
        if (recorded && FailCheckoutCompletion && transaction.Type == PaymentTransactionType.Captured)
            throw new InvalidOperationException("Simulated checkout failure");
        return Task.FromResult(new PaymentRecordResult(recorded, PaymentStatus.Paid, null));
    }

    public Task<OrderPayment?> GetAsync(Guid paymentId, CancellationToken ct = default)
        => Task.FromResult(Payments.FirstOrDefault(x => x.PaymentId == paymentId));

    public Task<OrderPayment?> GetByProviderReferenceAsync(string providerType, string providerReference, CancellationToken ct = default)
        => Task.FromResult(Payments.LastOrDefault(x => x.ProviderReference == providerReference
            && (string.IsNullOrEmpty(x.ProviderType) || x.ProviderType == providerType)));

    public Task<IReadOnlyList<OrderPayment>> GetOpenPaymentsAsync(string providerType, DateTime createdAfterUtc, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<OrderPayment>>(Payments
            .Where(x => x.CreatedUtc > createdAfterUtc
                && (string.IsNullOrEmpty(x.ProviderType) || x.ProviderType == providerType)
                && Recorded.All(t => t.PaymentId != x.PaymentId))
            .ToList());

    public Task<OrderPaymentsView> GetForOrderAsync(Guid orderId, CancellationToken ct = default)
    {
        var attempts = Payments.Where(x => x.OrderUniqueId == orderId).Select(x => new OrderPaymentAttempt
        {
            PaymentId = x.PaymentId,
            Status = Captures(x.PaymentId).Any() ? "Paid" : "Pending",
            Amount = x.Amount,
            CreatedUtc = x.CreatedUtc,
            CapturedUtc = Captures(x.PaymentId).Any() ? DateTime.UtcNow : null,
        }).ToList();
        return Task.FromResult(new OrderPaymentsView(attempts.FirstOrDefault(x => x.CapturedUtc != null), attempts, []));
    }

    public async Task<IReadOnlyDictionary<Guid, OrderPaymentAttempt>> GetCurrentPaymentsAsync(IReadOnlyCollection<Guid> orderIds, CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, OrderPaymentAttempt>();
        foreach (var orderId in orderIds)
        {
            if ((await GetForOrderAsync(orderId, ct)).Current is { } current) result[orderId] = current;
        }
        return result;
    }

    public Task<Guid?> FindOrderAsync(string paymentIdOrTransactionId, CancellationToken ct = default)
        => Task.FromResult<Guid?>(null);
}
