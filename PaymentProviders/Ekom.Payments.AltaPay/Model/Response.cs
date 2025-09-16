namespace Ekom.Payments.AltaPay.Model;

/// <summary>
/// Represents the details of a payment transaction.
/// </summary>
public class PaymentCallback
{
    public bool RequiresCapture { get; set; }
    public string Status { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public PaymentStatus PaymentStatus => Enum.TryParse<PaymentStatus>(Status, out var status) ? status : PaymentStatus.UNKNOWN;
    public string CustomerErrorMessage { get; set; }
    public string MerchantErrorMessage { get; set; }
    public string MerchantErrorCode { get; set; }
    public bool CustomerMessageMustBeShown { get; set; }
    public Guid PaymentId { get; set; }
    public Guid SessionId { get; set; }
    public PaymentCallbackOrder Order { get; set; }
    public PaymentCallbackCardInformation CardInformation { get; set; }
}

public enum PaymentStatus
{
    NEW,
    SUCCEEDED,
    ERROR,
    FAILED,
    PENDING,
    CANCELLED,
    DECLINED,
    UNKNOWN
}

public class PaymentCallbackOrder
{
    public string OrderId { get; set; }
    public double Amount { get; set; }
    public string Currency { get; set; }
}

public class PaymentCallbackCardInformation
{
    public string LastFourDigits { get; set; }
}

public class AuthenticationResponse
{
    public string Token { get; set; }
}

