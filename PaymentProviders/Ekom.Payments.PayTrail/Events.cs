namespace Ekom.Payments.PayTrail;

/// <summary>
/// Callbacks to run only for this provider on success/error.
/// Supplied by library consumer.
/// </summary>
public static class Events
{
    /// <summary>
    /// Raises the success event on successful payment verification
    /// </summary>
    internal static async Task OnSuccessAsync(object sender, SuccessEventArgs successEventArgs)
    {
        Success?.Invoke(sender, successEventArgs);
        await global::Ekom.Payments.Events.OnSuccessAsync(sender, successEventArgs);
    }

    /// <summary>
    /// Raises the error event on failed payments
    /// </summary>
    internal static async Task OnErrorAsync(object sender, ErrorEventArgs errorEventArgs)
    {
        Error?.Invoke(sender, errorEventArgs);
        await global::Ekom.Payments.Events.OnErrorAsync(sender, errorEventArgs);
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
