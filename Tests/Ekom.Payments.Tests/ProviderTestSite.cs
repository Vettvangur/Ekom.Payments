using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Net;
using System.Net.Mail;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Ekom.Payments.Tests;

/// <summary>
/// The shop a payment provider runs in during a test: one order, Ekom's order payments held in memory,
/// and the legacy payment rows providers used before they recorded payments on the order.
/// </summary>
sealed class ProviderTestSite : IUmbracoService, IHttpContextAccessor, IMailService
{
    public PaymentsConfiguration Configuration { get; } = new() { SendEmailAlerts = false };
    public Guid OrderId { get; } = Guid.NewGuid();
    public InMemoryOrderPayments Payments { get; } = new();
    public LegacyOrderRows LegacyOrders { get; } = new();
    public HttpContext? HttpContext { get; set; } = CreateHttpContext();
    public string From { get; set; } = "";
    public string To { get; set; } = "";

    public PaymentSettings Settings(decimal total, string orderNumber = "IS0001")
    {
        var settings = new PaymentSettings
        {
            OrderUniqueId = OrderId, OrderNumber = orderNumber, OrderName = "Order",
            Orders = [new OrderItem { Title = "Item", Price = total, GrandTotal = total, Quantity = 1 }],
            Currency = "ISK", Store = "test", Language = "is-IS",
            SuccessUrl = new Uri($"https://shop.example/receipt?orderId={OrderId}"),
            CancelUrl = new Uri("https://shop.example/cancel"),
            ErrorUrl = new Uri("https://shop.example/error"),
        };
        settings.OrderCustomData["ekomOrderUniqueId"] = OrderId.ToString();
        return settings;
    }

    /// <summary>A payment row as a provider stored it before payments were recorded on the Ekom order.</summary>
    public OrderStatus AddLegacyPayment(decimal total, object providerSettings, string? provider)
    {
        var stored = Newtonsoft.Json.Linq.JObject.FromObject(providerSettings);
        if (provider != null) stored["ekomPaymentProvider"] = provider;
        var row = new OrderStatus
        {
            UniqueId = Guid.NewGuid(),
            Amount = total,
            EkomPaymentSettings = Settings(total),
            EkomPaymentProviderData = stored.ToString(Newtonsoft.Json.Formatting.None),
            CustomData = OrderId.ToString(),
        };
        LegacyOrders.Rows.Add(row);
        return row;
    }

    /// <summary>The hidden fields of the form a provider sends the customer to its gateway with.</summary>
    public static Dictionary<string, string> ParseForm(string html)
        => Regex.Matches(html, "<input type=\"hidden\" name=\"([^\"]*)\" value=\"([^\"]*)\">")
            .ToDictionary(x => WebUtility.HtmlDecode(x.Groups[1].Value), x => WebUtility.HtmlDecode(x.Groups[2].Value));

    public static T WithRequest<T>(T controller) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = CreateHttpContext() };
        return controller;
    }

    static HttpContext CreateHttpContext()
    {
        var http = new DefaultHttpContext();
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("shop.example");
        return http;
    }

    /// <summary>Fills in a provider's settings the way its Umbraco payment provider node would.</summary>
    public Action<object?>? ProviderNode { get; set; }

    public void PopulatePaymentProviderProperties(
        PaymentSettings settings, string ppNodeName, object? customProperties, IEnumerable<PropertyInfo>? customPropertyList)
    {
        settings.PaymentProviderName = "Payment provider node";
        ProviderNode?.Invoke(customProperties);
    }

    public Task SendAsync(MailMessage mailMessage) => Task.CompletedTask;
}

/// <summary>The payment rows providers used before they recorded payments on the Ekom order.</summary>
sealed class LegacyOrderRows : IOrderService
{
    public List<OrderStatus> Rows { get; } = [];

    public Task<OrderStatus?> GetAsync(Guid id)
        => Task.FromResult(Rows.FirstOrDefault(x => x.UniqueId == id || x.CustomData == id.ToString()));
    public Task<OrderStatus?> GetByCustomAsync(string id) => Task.FromResult(Rows.FirstOrDefault(x => x.CustomData == id));
    public Task<OrderStatus> InsertAsync(decimal total, PaymentSettings paymentSettings, object paymentProviderSettings,
        string? netPaymentData, HttpContext httpContext) => throw new NotSupportedException();
    public Task UpdateAsync(OrderStatus orderStatus) => throw new NotSupportedException();
}
