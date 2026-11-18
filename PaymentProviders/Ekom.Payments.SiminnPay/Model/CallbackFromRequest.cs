using Microsoft.AspNetCore.Mvc;

namespace Ekom.Payments.SiminnPay.Model;

/// <summary>
/// Not used by Siminn Pay: Siminn Pay has no custom payment form callback. The class is kept so
/// code that references it still compiles; Siminn Pay's notifications are read as <see cref="SiminnPayOrderStatus"/>.
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
