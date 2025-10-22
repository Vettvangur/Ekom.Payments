namespace Ekom.Payments.Straumur;

/// <summary>
/// PaymentRequest data for Straumur
/// </summary>
public class PaymentRequest
{
    /// <summary>
    /// The amount to be charged in minor units. Must end in 00 for ISK..
    /// </summary>
    public int Amount { get; set; }

    /// <summary>
    /// The three-character ISO currency code..
    /// </summary>
    public string Currency { get; set; }

    /// <summary>
    /// The URL to return to when a redirect payment is completed. Must begin with http:// or https://..
    /// </summary>
    public string ReturnUrl { get; set; }

    /// <summary>
    /// The reference to uniquely identify a payment.
    /// </summary>
    public string Reference { get; set; }

    /// <summary>
    /// The terminal identifier to uniquely identify the terminal.
    /// You can find your Terminal Identifier in the Merchant Portal.
    /// Open Section "Terminals" > Select Terminal to open Details panel > Copy Terminal Identifier.
    /// </summary>
    public string TerminalIdentifier { get; set; }

    public string Culture { get; set; }

    public string RecurringProcessingModel { get; set; }

    public List<Item> Items { get; set; }
}

public class Item
{
    public string Name { get; set; }

    public int Amount { get; set; }

    public int Quantity { get; set; }

    public int UnitPrice { get; set; }
}
