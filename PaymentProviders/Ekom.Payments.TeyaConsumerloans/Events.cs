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
    /// <summary>No longer raised.</summary>
    [Obsolete("Teya Consumer Loans payments are recorded on the Ekom order. Use Ekom.Events.OrderEvents.PaymentRecordedAsync.")]
#pragma warning disable CS0067 // Kept so existing subscriptions compile; it is never raised.
    public static event EventHandler<SuccessEventArgs>? Success;

    /// <summary>No longer raised.</summary>
    [Obsolete("Teya Consumer Loans payments are recorded on the Ekom order. Use Ekom.Events.OrderEvents.PaymentRecordedAsync.")]
    public static event EventHandler<ErrorEventArgs>? Error;
#pragma warning restore CS0067
}
