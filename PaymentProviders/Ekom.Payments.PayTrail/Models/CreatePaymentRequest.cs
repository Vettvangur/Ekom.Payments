using System.Text.Json.Serialization;

namespace Ekom.Payments.PayTrail.Models;

public class CreatePaymentRequest
{
    [JsonPropertyName("stamp")]
    public string Stamp { get; set; } = string.Empty;

    [JsonPropertyName("reference")]
    public string Reference { get; set; } = string.Empty;

    [JsonPropertyName("amount")]
    public int Amount { get; set; }

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = string.Empty;

    [JsonPropertyName("language")]
    public string Language { get; set; } = string.Empty;

    [JsonPropertyName("items")]
    public IEnumerable<PaymentItem> Items { get; set; } = [];

    [JsonPropertyName("customer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PaymentCustomer? Customer { get; set; }

    [JsonPropertyName("redirectUrls")]
    public PaymentUrls RedirectUrls { get; set; } = new();

    [JsonPropertyName("callbackUrls")]
    public PaymentUrls CallbackUrls { get; set; } = new();
}

public class PaymentItem
{
    [JsonPropertyName("unitPrice")]
    public int UnitPrice { get; set; }

    [JsonPropertyName("units")]
    public int Units { get; set; }

    [JsonPropertyName("vatPercentage")]
    public decimal VatPercentage { get; set; }

    [JsonPropertyName("productCode")]
    public string ProductCode { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
}

public class PaymentCustomer
{
    [JsonPropertyName("email")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Email { get; set; }

    [JsonPropertyName("firstName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FirstName { get; set; }

    [JsonPropertyName("lastName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastName { get; set; }

    [JsonPropertyName("phone")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Phone { get; set; }
}

public class PaymentUrls
{
    [JsonPropertyName("success")]
    public string Success { get; set; } = string.Empty;

    [JsonPropertyName("cancel")]
    public string Cancel { get; set; } = string.Empty;
}
