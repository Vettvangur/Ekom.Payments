using System.Text.Json.Serialization;

namespace Ekom.Payments.TeyaConsumerloans.Models;

/// <summary>
/// Documentation: https://docs.borgun.is/consumerloans/clapiv3/#tokenrequest
/// </summary>
public class TokenRequest
{
    public string? SocialSecurityNumber { get; set; }
    public string? Email { get; set; }
    public string? PhoneNumber { get; set; }
    public required int ProgressValidMinutes { get; set; }
    public required int TokenValidMinutes { get; set; }
    public required OnlineLoan LoanInformation { get; set; }
}

public class OnlineLoan
{
    public required string MerchantNumber { get; set; }
    public required int LoanTypeId { get; set; }
    public required decimal Amount { get; set; }
    public required string Description { get; set; }
    public required int NumberOfPayments { get; set; }
    public required bool FlexibleNumberOfPayments { get; set; }
    public required string SuccessUrl { get; set; }
    public required string CancelUrl { get; set; }
} 

public class LoanCustomer
{
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("email")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Email { get; set; }

    [JsonPropertyName("phoneNumber")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PhoneNumber { get; set; }

    [JsonPropertyName("nationalRegistryId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NationalRegistryId { get; set; }

    [JsonPropertyName("address")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Address { get; set; }

    [JsonPropertyName("city")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? City { get; set; }

    [JsonPropertyName("postalCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PostalCode { get; set; }
}

public class LoanApplicationItem
{
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("quantity")]
    public int Quantity { get; set; }

    [JsonPropertyName("unitPrice")]
    public decimal UnitPrice { get; set; }

    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }
}

public class LoanTokenResponse
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;
}
