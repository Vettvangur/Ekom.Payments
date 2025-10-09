namespace Ekom.Payments.AltaPay.Model;

/// <summary>
/// Event to run on Callback Url
/// </summary>
public class CallbackUrlEventArgs : EventArgs
{
    public CallbackFromRequest? Request { get; set; }
    public string? Template { get; set; }
}
