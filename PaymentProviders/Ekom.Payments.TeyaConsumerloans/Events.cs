namespace Ekom.Payments.TeyaConsumerloans;

/// <summary>
/// Callbacks to run only for this provider on success/error.
/// Supplied by library consumer.
/// </summary>
public static class Events
{
    /// <summary>Notifications that consumers can forward to their order activity log.</summary>
    public static event Func<object, LoanActivityEventArgs, Task>? ActivityAsync;

    internal static async Task OnActivityAsync(object sender, LoanActivityEventArgs args)
    {
        var handlers = ActivityAsync;
        if (handlers != null)
        {
            foreach (var handler in handlers.GetInvocationList().Cast<Func<object, LoanActivityEventArgs, Task>>())
            {
                await handler(sender, args).ConfigureAwait(false);
            }
        }
    }

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
