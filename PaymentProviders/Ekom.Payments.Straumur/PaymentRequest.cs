using System.Text.Json.Serialization;

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
    /// The number of hours to wait before capturing the payment.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int CaptureHoursDelay { get; set; } = 0;

    /// <summary>
    /// The terminal identifier to uniquely identify the terminal.
    /// You can find your Terminal Identifier in the Merchant Portal.
    /// Open Section "Terminals" > Select Terminal to open Details panel > Copy Terminal Identifier.
    /// </summary>
    public string TerminalIdentifier { get; set; }

    public string Culture { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string RecurringProcessingModel { get; set; }

    /// <summary>
    /// The UTC date and time after which the hosted checkout can no longer be completed.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ExpiresAt { get; set; }

    public List<Item> Items { get; set; }
}

public class Item
{
    public string Name { get; set; }

    public int Amount { get; set; }

    public decimal Quantity { get; set; }

    public int UnitPrice { get; set; }
}
