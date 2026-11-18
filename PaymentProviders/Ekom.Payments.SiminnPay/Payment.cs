using Ekom.Payments.Helpers;
using Ekom.Payments.SiminnPay.Exceptions;
using Ekom.Payments.SiminnPay.Model;
using Ekom.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Ekom.Payments.SiminnPay;

/// <summary>
/// Initiate a payment request with Síminn Pay
/// </summary>
public class Payment : IPaymentProvider
{
    internal const string _ppNodeName = "siminnPay";
    /// <summary>
    /// Ekom.Payments ResponseController
    /// </summary>
    const string reportPath = "/ekom/payments/siminnpayresponse";

    readonly ILogger<Payment> _logger;
    readonly IUmbracoService _uService;
    readonly IOrderPayments _payments;
    readonly HttpContext _httpCtx;

    /// <summary>
    /// ctor for Unit Tests
    /// </summary>
    public Payment(
        ILogger<Payment> logger,
        IUmbracoService uService,
        IOrderPayments payments,
        IHttpContextAccessor httpContext)
    {
        _logger = logger;
        _uService = uService;
        _payments = payments;
        _httpCtx = httpContext.HttpContext ?? throw new NotSupportedException("Payment requests require an httpcontext");
    }

    /// <summary>
    /// Initiate a payment request with Síminn Pay.
    /// When calling RequestAsync, always await the result.
    /// </summary>
    /// <param name="paymentSettings">Configuration object for PaymentProviders</param>
    public async Task<string> RequestAsync(PaymentSettings paymentSettings)
    {
        ArgumentNullException.ThrowIfNull(paymentSettings);
        ArgumentNullException.ThrowIfNull(paymentSettings.Orders);
        ArgumentException.ThrowIfNullOrEmpty(paymentSettings.Language);
        ArgumentException.ThrowIfNullOrEmpty(paymentSettings.CustomerInfo?.PhoneNumber);

        try
        {
            _logger.LogInformation("Síminn Pay Payment Request - Start");

            var siminnPaySettings = paymentSettings.CustomSettings.ContainsKey(typeof(SiminnPaySettings))
                ? paymentSettings.CustomSettings[typeof(SiminnPaySettings)] as SiminnPaySettings
                : new SiminnPaySettings();
            siminnPaySettings ??= new SiminnPaySettings();

            _uService.PopulatePaymentProviderProperties(
                paymentSettings,
                _ppNodeName,
                siminnPaySettings,
                SiminnPaySettings.Properties);
            siminnPaySettings.ApplyDefaults();

            var total = paymentSettings.Orders.Sum(x => x.GrandTotal);

            var payOrder = new CreateSiminnPayOrder
            {
                Description = paymentSettings.Orders.First().Title,
                PhoneNumber = paymentSettings.CustomerInfo.PhoneNumber,
                Amount = total,
                ReferenceId = paymentSettings.OrderUniqueId.ToString()
            };

            paymentSettings.ReportUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.ReportUrl ?? new Uri(reportPath, UriKind.Relative), _httpCtx.Request);
            paymentSettings.SuccessUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.SuccessUrl, _httpCtx.Request);

            var svc = new SiminnPayService(siminnPaySettings, _logger);
            var order = await svc.CreatePaymentOrder(payOrder, paymentSettings.ReportUrl).ConfigureAwait(false)
                ?? throw new SiminnPayApiResponseException("Síminn Pay returned no payment order.");

            _logger.LogInformation("Síminn Pay Payment Request - Created payment order with order key: {OrderKey}", order.OrderKey);

            // Store the payment on the Ekom order. Síminn Pay identifies it by its own order key in callbacks.
            var payment = await _payments.CreateAsync(
                paymentSettings,
                _ppNodeName,
                total,
                providerReference: order.OrderKey.ToString(),
                httpContext: _httpCtx
            ).ConfigureAwait(false);

            // Only an order waiting for the customer can be paid. Anything else fails the payment request,
            // so checkout shows the customer an error instead of an empty page.
            if (order.Status != SiminnPayStatus.WaitingForCustomer)
            {
                throw new SiminnPayApiResponseException(
                    $"Síminn Pay did not accept the payment request: the new payment order has status {order.Status} ({(int)order.Status}), expected {SiminnPayStatus.WaitingForCustomer}.");
            }

            _logger.LogInformation("Síminn Pay Payment Request - " + PaymentLog.Template + " Amount: {Total} OrderKey: {OrderKey}",
                payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId, total, order.OrderKey);

            var redirectUri = PaymentsUriHelper.AddQueryString(paymentSettings.SuccessUrl, $"?siminnPayOrderKey={order.OrderKey}");
            _httpCtx.Response.Redirect(redirectUri.ToString());
            return string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Síminn Pay Payment Request - Failed. OrderNumber: {OrderNumber} OrderId: {OrderUniqueId}",
                paymentSettings.OrderNumber, paymentSettings.OrderUniqueId);
            throw;
        }
    }
}
