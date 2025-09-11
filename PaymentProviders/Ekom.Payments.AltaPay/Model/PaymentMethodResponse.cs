namespace Ekom.Payments.AltaPay.Model;

public class PaymentMethodResponse
{
    public List<PaymentMethod> Methods { get; set; }
}

public class PaymentMethod
{
    public required string Id { get; set; }
    public required string Type { get; set; }
    public required string Description { get; set; }
    public required string LogoUrl { get; set; }
    public required string Display { get; set; }
    public required InitiatePayment OnInitiatePayment { get; set; }
    public required string Name { get; set; }
}

public class InitiatePayment
{
    public string Type { get; set; }
    public string Value { get; set; }
}
