namespace Ekom.Payments.TeyaConsumerloans;

/// <summary>
/// Callbacks to run only for this provider on success/error.
/// Supplied by library consumer.
/// </summary>
public static class Events
{
    /// <summary>
    /// Raises the success event on successful loan application creation.
    /// </summary>
    internal static async Task OnSuccessAsync(object sender, SuccessEventArgs successEventArgs)
    {
        Success?.Invoke(sender, successEventArgs);
        await Payments.Events.OnSuccessAsync(sender, successEventArgs);
    }

    /// <summary>
    /// Raises the error event on failed loan applications.
    /// </summary>
    internal static async Task OnErrorAsync(object sender, ErrorEventArgs errorEventArgs)
    {
        Error?.Invoke(sender, errorEventArgs);
        await Payments.Events.OnErrorAsync(sender, errorEventArgs);
    }

    /// <summary>
    /// Event fired when the loan application is successfully initialized.
    /// </summary>
    public static event EventHandler<SuccessEventArgs>? Success;

    /// <summary>
    /// Event fired on loan application errors.
    /// </summary>
    public static event EventHandler<ErrorEventArgs>? Error;
}
