using Ekom.Payments.Helpers;
using Ekom.Payments.SiminnPay.Model;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Ekom.Payments.SiminnPay;

/// <summary>s
/// Initiate a payment request with Alta
/// </summary>
public class Payment : IPaymentProvider
{
    internal const string _ppNodeName = "siminnPay";
    /// <summary>
    /// Ekom.Payments ResponseController
    /// </summary>
    const string reportPath = "/ekom/payments/siminnpayresponse";

    readonly ILogger<Payment> _logger;
    readonly PaymentsConfiguration _settings;
    readonly IUmbracoService _uService;
    readonly IOrderService _orderService;
    readonly HttpContext _httpCtx;
    readonly IHttpClientFactory _httpClientFactory;
    readonly IDatabaseFactory _dbFac;

    /// <summary>
    /// ctor for Unit Tests
    /// </summary>
    public Payment(
        ILogger<Payment> logger,
        PaymentsConfiguration settings,
        IUmbracoService uService,
        IOrderService orderService,
        IHttpContextAccessor httpContext,
        IHttpClientFactory httpClientFactory,
        IDatabaseFactory dbFac)
    {
        _logger = logger;
        _settings = settings;
        _uService = uService;
        _orderService = orderService;
        _httpCtx = httpContext.HttpContext ?? throw new NotSupportedException("Payment requests require an httpcontext");
        _httpClientFactory = httpClientFactory;
        _dbFac = dbFac;
    }

    /// <summary>
    /// Initiate a payment request with alta.
    /// When calling RequestAsync, always await the result.
    /// </summary>
    /// <param name="paymentSettings">Configuration object for PaymentProviders</param>
    public async Task<string> RequestAsync(PaymentSettings paymentSettings)
    {
        if (paymentSettings == null)
            throw new ArgumentNullException(nameof(paymentSettings));
        if (paymentSettings.Orders == null)
            throw new ArgumentNullException(nameof(paymentSettings.Orders));
        if (string.IsNullOrEmpty(paymentSettings.Language))
            throw new ArgumentException(nameof(paymentSettings.Language));
        if (string.IsNullOrEmpty(paymentSettings.CustomerInfo?.PhoneNumber))
            throw new ArgumentException(nameof(paymentSettings.CustomerInfo.PhoneNumber));

        try
        {
            _logger.LogInformation("Síminn Pay Payment Request - Start");

            var siminnPaySettings = paymentSettings.CustomSettings.ContainsKey(typeof(SiminnPaySettings))
                ? paymentSettings.CustomSettings[typeof(SiminnPaySettings)] as SiminnPaySettings
                : new SiminnPaySettings();

            _uService.PopulatePaymentProviderProperties(
                paymentSettings,
                _ppNodeName,
                siminnPaySettings,
                SiminnPaySettings.Properties);

            var total = paymentSettings.Orders.Sum(x => x.GrandTotal);

            var payOrder = new SiminnPayOrder
            {
                Description = paymentSettings.Orders.First().Title,
                PhoneNumber = paymentSettings.CustomerInfo.PhoneNumber,
                Status = SiminnPayStatus.WaitingForCustomer,
                Amount = total,
                OriginalAmount = total,
                ReferenceId = paymentSettings.OrderUniqueId.ToString(),
                Created = DateTime.UtcNow,
            };

            paymentSettings.ReportUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.ReportUrl ?? new Uri(reportPath, UriKind.Relative), _httpCtx.Request);
            paymentSettings.SuccessUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.SuccessUrl, _httpCtx.Request);

            var svc = new SiminnPayService(siminnPaySettings!.ApiKey, siminnPaySettings.ApiUrl, _logger);
            var order = await svc.CreatePaymentOrder(payOrder,
                                                     paymentSettings.ReportUrl.ToString(),
                                                     siminnPaySettings.Currency,
                                                     siminnPaySettings.RestrictToLoan).ConfigureAwait(false);

            payOrder.OrderKey = order.OrderKey;
            payOrder.Expires = DateTime.UtcNow.AddMinutes(1);
            payOrder.Status = order.Status;
            payOrder.PaymentProvider = _ppNodeName;

            _logger.LogInformation("Síminn Pay Payment Request - Created payment order with order key: {OrderKey}", order.OrderKey);

            // Persist in database and retrieve unique order id
            var orderStatus = await _orderService.InsertAsync(
                total,
                paymentSettings,
                siminnPaySettings,
                order.OrderKey.ToString(),
                _httpCtx
            ).ConfigureAwait(false);

            payOrder.NetPaymentOrderId = orderStatus.UniqueId;

            using (var db = _dbFac.GetDatabase())
            {
                await db.InsertAsync(payOrder).ConfigureAwait(false);
            }

            if (payOrder.Status == SiminnPayStatus.WaitingForNewAmount)
            {
                throw new NotImplementedException("SiminnPayStatus.WaitingForNewAmount");
            }
            else if (payOrder.Status != SiminnPayStatus.WaitingForCustomer)
            {
                _logger.LogWarning("Síminn Pay Payment Request - Received erronous status: {Status}", payOrder.Status);
                return null;
            }

            _logger.LogInformation("Síminn Pay Payment Request - Amount: {total} OrderId: {UniqueId}", total, orderStatus.UniqueId);

            var redirectUri = PaymentsUriHelper.AddQueryString(paymentSettings.SuccessUrl, $"?siminnPayOrderKey={payOrder.OrderKey.ToString()}");
            _httpCtx.Response.Redirect(redirectUri.ToString());
            return string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Síminn Pay Payment Request - Payment Request Failed");
            await Events.OnErrorAsync(this, new ErrorEventArgs
            {
                Exception = ex,
            });
            throw;
        }
    }
}
