using Ekom.Payments.Helpers;
using Ekom.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Ekom.Payments.AsynchronousExample;

/// <summary>
/// Initiate a payment request with AsynchronousExample
/// </summary>
class Payment : IPaymentProvider
{
    internal const string _ppNodeName = "AsynchronousExample";
    /// <summary>
    /// Ekom.Payments ResponseController
    /// </summary>
    const string reportPath = "/ekom/payments/AsynchronousExampleresponse";

    readonly ILogger<Payment> _logger;
    readonly PaymentsConfiguration _settings;
    readonly IUmbracoService _uService;
    readonly IOrderPayments _payments;
    readonly HttpContext _httpCtx;
    readonly IConfiguration _config;

    /// <summary>
    /// ctor for Unit Tests
    /// </summary>
    public Payment(
        ILogger<Payment> logger,
        PaymentsConfiguration settings,
        IUmbracoService uService,
        IOrderPayments payments,
        IHttpContextAccessor httpContext,
        IConfiguration config)
    {
        _logger = logger;
        _settings = settings;
        _uService = uService;
        _payments = payments;
        _httpCtx = httpContext.HttpContext ?? throw new NotSupportedException("Payment requests require an httpcontext");
        _config = config;
    }

    /// <summary>
    /// Initiate a payment request with AsynchronousExample.
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

        try
        {
            _logger.LogInformation("AsynchronousExample Payment Request - Start");

            var asynchronousExampleSettings = paymentSettings.CustomSettings.ContainsKey(typeof(AsynchronousExampleSettings))
                ? (paymentSettings.CustomSettings[typeof(AsynchronousExampleSettings)] as AsynchronousExampleSettings)!
                : new AsynchronousExampleSettings();

            _uService.PopulatePaymentProviderProperties(
                paymentSettings,
                _ppNodeName,
                asynchronousExampleSettings,
                AsynchronousExampleSettings.Properties);

            var total = paymentSettings.Orders.Sum(x => x.GrandTotal);

            // Store the payment on the Ekom order. The payment id is what the gateway echoes back in its callback.
            var payment = await _payments.CreateAsync(
                paymentSettings,
                _ppNodeName,
                total,
                asynchronousExampleSettings,
                httpContext: _httpCtx
            ).ConfigureAwait(false);

            // This example shows how to construct a success url / receipt page destination
            // adding the reference querystring allows the receipt page to find the payment
            paymentSettings.SuccessUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.SuccessUrl, _httpCtx.Request);
            paymentSettings.SuccessUrl = PaymentsUriHelper.AddQueryString(paymentSettings.SuccessUrl, "?reference=" + payment.PaymentId);

            var cancelUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.CancelUrl, _httpCtx.Request);
            var callbackUrl = PaymentsUriHelper.EnsureFullUri(new Uri(reportPath, UriKind.Relative), _httpCtx.Request);


            _logger.LogInformation(
                "AsynchronousExample Payment Request - OrderNumber: {OrderNumber} OrderId: {OrderUniqueId} PaymentId: {PaymentId} Amount: {Total}",
                paymentSettings.OrderNumber, paymentSettings.OrderUniqueId, payment.PaymentId, total);

            // If your payment provider expects a client initiated post request to send the user to it's payment portal
            // the following form helper is useful.
            var formValues = new Dictionary<string, string?>
            {

            };

            var cspNonce = CspHelper.GetCspNonce(_httpCtx, _config);

            return FormHelper.CreateRequest(formValues, asynchronousExampleSettings.PaymentPageUrl.ToString(), cspNonce: cspNonce);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "AsynchronousExample Payment Request - Failed. OrderNumber: {OrderNumber} OrderId: {OrderUniqueId}",
                paymentSettings.OrderNumber, paymentSettings.OrderUniqueId);
            throw;
        }
    }
}
