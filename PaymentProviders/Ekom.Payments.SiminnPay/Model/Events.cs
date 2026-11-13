namespace Ekom.Payments.SiminnPay.Model;

/// <summary>
/// Siminn Pay records its payments on the Ekom order. Subscribe to
/// <c>Ekom.Events.OrderEvents.PaymentRecordedAsync</c> to follow them.
/// </summary>
public static class Events
{
    /// <summary>No longer raised.</summary>
    [Obsolete("Siminn Pay payments are recorded on the Ekom order. Use Ekom.Events.OrderEvents.PaymentRecordedAsync.")]
#pragma warning disable CS0067 // Kept so existing subscriptions compile; it is never raised.
    public static event EventHandler<SuccessEventArgs>? Success;

    /// <summary>No longer raised.</summary>
    [Obsolete("Siminn Pay payments are recorded on the Ekom order. Use Ekom.Events.OrderEvents.PaymentRecordedAsync.")]
    public static event EventHandler<ErrorEventArgs>? Error;
#pragma warning restore CS0067
}
