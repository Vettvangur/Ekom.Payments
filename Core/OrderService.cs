using LinqToDB;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;

namespace Ekom.Payments;

public interface IOrderService
{
    Task<OrderStatus?> GetAsync(Guid id);
    Task<OrderStatus?> GetByCustomAsync(string id);
    Task<OrderStatus> InsertAsync(decimal total,
        PaymentSettings paymentSettings,
        object paymentProviderSettings,
        string? netPaymentData,
        HttpContext httpContext);
    Task UpdateAsync(OrderStatus orderStatus);
}

/// <summary>
/// Utility functions for handling <see cref="OrderStatus"/> objects
/// </summary>
class OrderService : IOrderService
{
    readonly PaymentsConfiguration _settings;
    readonly IDatabaseFactory _dbFac;

    /// <summary>
    /// ctor
    /// </summary>
    public OrderService(
        PaymentsConfiguration settings,
        IDatabaseFactory dbFac
    )
    {
        _settings = settings;
        _dbFac = dbFac;
    }

    /// <summary>
    /// Get order with the given unique id
    /// </summary>
    /// <param name="id">Order id</param>
    public async Task<OrderStatus?> GetAsync(Guid id)
    {
        await using var db = _dbFac.GetDatabase();

        return await db.OrderStatus
            .Where(x => x.UniqueId == id)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Get order with the given custom id
    /// </summary>
    /// <param name="id">Id</param>
    public async Task<OrderStatus?> GetByCustomAsync(string id)
    {
        await using var db = _dbFac.GetDatabase();

        return await db.OrderStatus
            .Where(x => x.CustomData == id)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Persist in database and retrieve unique order id
    /// </summary>
    /// <returns>Order Id</returns>
    public async Task<OrderStatus> InsertAsync(
        decimal total,
        PaymentSettings paymentSettings,
        object paymentProviderSettings,
        string? netPaymentData,
        HttpContext httpContext
    )
    {
        var orderid = Guid.NewGuid();

        var orderStatus = new OrderStatus
        {
            OrderName = paymentSettings.OrderName,
            UniqueId = orderid,
            Member = paymentSettings.Member,
            Amount = total,
            Date = DateTime.Now,
            IPAddress = httpContext.Connection.RemoteIpAddress?.ToString() ?? "",
            UserAgent = httpContext.Request.Headers["User-Agent"]
                .ToString()
                .Substring(0, Math.Min(httpContext.Request.Headers["User-Agent"].Count, 4000)),
            EkomPaymentSettings = paymentSettings,
            EkomPaymentProviderData = JsonConvert.SerializeObject(paymentProviderSettings),
            CustomData = netPaymentData
        };

        await using var db = _dbFac.GetDatabase();
        // Return order id
        await db.InsertAsync(orderStatus).ConfigureAwait(false);

        return orderStatus;
    }

    public async Task UpdateAsync(OrderStatus orderStatus)
    {
        await using var db = _dbFac.GetDatabase();

        await db.UpdateAsync(orderStatus).ConfigureAwait(false);
    }
}
