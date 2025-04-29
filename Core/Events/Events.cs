namespace Ekom.Payments;
public static class Events
{
    public static event Func<object, SuccessEventArgs, Task>? SuccessAsync;

    public static event Func<object, ErrorEventArgs, Task>? ErrorAsync;

    /// <summary>
    /// Raises the success event asynchronously
    /// </summary>
    internal static async Task OnSuccessAsync(object sender, SuccessEventArgs successEventArgs)
    {
        if (SuccessAsync != null)
        {
            foreach (var handler in SuccessAsync.GetInvocationList().Cast<Func<object, SuccessEventArgs, Task>>())
            {
                await handler(sender, successEventArgs).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Raises the error event asynchronously
    /// </summary>
    internal static async Task OnErrorAsync(object sender, ErrorEventArgs errorEventArgs)
    {
        if (ErrorAsync != null)
        {
            foreach (var handler in ErrorAsync.GetInvocationList().Cast<Func<object, ErrorEventArgs, Task>>())
            {
                await handler(sender, errorEventArgs).ConfigureAwait(false);
            }
        }
    }
}
