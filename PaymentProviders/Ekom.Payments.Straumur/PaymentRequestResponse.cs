namespace Ekom.Payments.Straumur;

/// <summary>
/// Response data from Straumur Server
/// </summary>
public class PaymentRequestResponse
{
    /// <summary>
    /// The URL to redirect the user to the hosted checkout page.	
    /// </summary>
    public string Url { get; set; }

    /// <summary>
    /// The reference to uniquely identify the hosted checkout session.	
    /// </summary>
    public string CheckoutReference { get; set; }

    /// <summary>
    /// The date and time when the response was generated.	
    /// </summary>
    public string ResponseDateTime { get; set; }

    /// <summary>
    /// The unique identifier for the response.	
    /// </summary>
    public string ResponseIdentifier { get; set; }
}
