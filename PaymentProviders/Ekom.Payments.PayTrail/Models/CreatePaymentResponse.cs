using System.Text.Json.Serialization;

namespace Ekom.Payments.PayTrail.Models;

public class CreatePaymentResponse
{
    [JsonPropertyName("transactionId")]
    public string TransactionId { get; set; } = string.Empty;

    [JsonPropertyName("href")]
    public Uri? Href { get; set; }

    [JsonPropertyName("reference")]
    public string Reference { get; set; } = string.Empty;
}
