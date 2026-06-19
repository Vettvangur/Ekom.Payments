using System.Text.Json.Serialization;

namespace Ekom.Payments.TeyaConsumerloans.Models;

public class TokenRequest
{
    [JsonPropertyName("SocialSecurityNumber")]
    public string? SocialSecurityNumber { get; set; }

    [JsonPropertyName("Email")]
    public string? Email { get; set; }

    [JsonPropertyName("PhoneNumber")]
    public string? PhoneNumber { get; set; }

    [JsonPropertyName("ProgressValidMinutes")]
    public required int ProgressValidMinutes { get; set; }

    [JsonPropertyName("TokenValidMinutes")]
    public required int TokenValidMinutes { get; set; }

    [JsonPropertyName("LoanInformation")]
    public required OnlineLoan LoanInformation { get; set; }
}

public class OnlineLoan
{
    [JsonPropertyName("MerchantNumber")]
    public required string MerchantNumber { get; set; }

    [JsonPropertyName("LoanTypeId")]
    public required int LoanTypeId { get; set; }

    [JsonPropertyName("Amount")]
    public required decimal Amount { get; set; }

    [JsonPropertyName("Description")]
    public required string Description { get; set; }

    [JsonPropertyName("NumberOfPayments")]
    public required int NumberOfPayments { get; set; }

    [JsonPropertyName("FlexibleNumberOfPayments")]
    public required bool FlexibleNumberOfPayments { get; set; }

    [JsonPropertyName("SuccessUrl")]
    public required string SuccessUrl { get; set; }

    [JsonPropertyName("CancelUrl")]
    public required string CancelUrl { get; set; }
}

public class ValidateRequest
{
    [JsonPropertyName("Token")]
    public required string Token { get; set; }

    [JsonPropertyName("RedirectUrl")]
    public required string RedirectUrl { get; set; }

    [JsonPropertyName("MerchantNumber")]
    public required string MerchantNumber { get; set; }
}

public class ContractInfoCompact
{
    [JsonPropertyName("contractNumber")]
    public string? ContractNumber { get; set; }

    [JsonPropertyName("authorizationNumber")]
    public string? AuthorizationNumber { get; set; }

    [JsonPropertyName("socialSecurityNumber")]
    public string? SocialSecurityNumber { get; set; }
}
