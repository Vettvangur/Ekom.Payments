using Ekom.Payments.AltaPay.Model;

namespace Ekom.Payments.AltaPay;

/// <summary>
/// Callbacks to run only for this provider on success/error.
/// Supplied by library consumer.
/// Local in this context is in contrast with callbacks to be performed after a remote provider response, f.x.
/// </summary>
public static class Events
{
    /// <summary>
    /// Raises the success event on successful payment verification
    /// </summary>
    /// <summary>No longer raised.</summary>
    [Obsolete("AltaPay payments are recorded on the Ekom order. Use Ekom.Events.OrderEvents.PaymentRecordedAsync.")]
#pragma warning disable CS0067 // Kept so existing subscriptions compile; it is never raised.
    public static event EventHandler<SuccessEventArgs>? Success;

    /// <summary>No longer raised.</summary>
    [Obsolete("AltaPay payments are recorded on the Ekom order. Use Ekom.Events.OrderEvents.PaymentRecordedAsync.")]
    public static event EventHandler<ErrorEventArgs>? Error;
#pragma warning restore CS0067

    public delegate Task AsyncEventHandler<in TEventArgs>(object? sender, TEventArgs e)
    where TEventArgs : EventArgs;

    public static event AsyncEventHandler<CallbackUrlEventArgs>? CallbackUrl;

    internal static async Task OnCallbackUrlAsync(object? sender, CallbackUrlEventArgs e)
    {
        var handlers = CallbackUrl;
        if (handlers is null) return;

        // Call every subscriber and await completion (in parallel)
        var calls = handlers.GetInvocationList()
            .Cast<AsyncEventHandler<CallbackUrlEventArgs>>()
            .Select(h => h(sender, e));
        await Task.WhenAll(calls);
    }
}
