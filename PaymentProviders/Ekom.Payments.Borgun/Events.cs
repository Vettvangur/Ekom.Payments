namespace Ekom.Payments.Borgun;

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
}
