using Microsoft.AspNetCore.Mvc;

namespace Ekom.Payments.AltaPay.Model;

/// <summary>
/// Callback data from AltaPay request to retrieve a custom payment form.
/// Documentation: https://documentation.altapay.com/Content/Ecom/Payment%20Pages/Payment%20Page%20Form.htm
/// </summary>
public class CallbackFromRequest
{
    [FromForm(Name = "shop_orderid")]
    [FromQuery(Name = "shop_orderid")]
    public string OrderId { get; set; } = "";

    public decimal Amount { get; set; }

    public string Currency { get; set; } = "";

    public string Language { get; set; } = "";

    [FromForm(Name = "transaction_info")]
    [FromQuery(Name = "transaction_info")]
    public string[] TransactionInfo { get; set; } = [];
}
