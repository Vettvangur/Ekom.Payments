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
    internal static async Task OnSuccessAsync(object sender, SuccessEventArgs successEventArgs)
    {
        Success?.Invoke(sender, successEventArgs);
        await Payments.Events.OnSuccessAsync(sender, successEventArgs);
    }

    /// <summary>
    /// Raises the error event on failed payments
    /// </summary>
    internal static async Task OnErrorAsync(object sender, ErrorEventArgs errorEventArgs)
    {
        Error?.Invoke(sender, errorEventArgs);
        await Payments.Events.OnErrorAsync(sender, errorEventArgs);
    }

    /// <summary>
    /// Event fired on successful payment verification
    /// </summary>
    public static event EventHandler<SuccessEventArgs>? Success;

    /// <summary>
    /// Event fired on payment verification error
    /// </summary>
    public static event EventHandler<ErrorEventArgs>? Error;


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
