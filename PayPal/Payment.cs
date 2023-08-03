using Ekom.Payments.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Ekom.Payments.PayPal;

/// <summary>
/// Initiate a payment request with PayPal
/// </summary>
class Payment : IPaymentProvider
{
    internal const string _ppNodeName = "paypal";
    /// <summary>
    /// Ekom.Payments ResponseController
    /// </summary>
    const string reportPath = "/ekom/payments/PayPalResponse";

    readonly ILogger<Payment> _logger;
    readonly PaymentsConfiguration _settings;
    readonly IUmbracoService _uService;
    readonly IOrderService _orderService;
    readonly HttpContext _httpCtx;

    /// <summary>
    /// ctor for Unit Tests
    /// </summary>
    public Payment(
        ILogger<Payment> logger,
        PaymentsConfiguration settings,
        IUmbracoService uService,
        IOrderService orderService,
        IHttpContextAccessor httpContext)
    {
        _logger = logger;
        _settings = settings;
        _uService = uService;
        _orderService = orderService;
        _httpCtx = httpContext.HttpContext ?? throw new NotSupportedException("Payment requests require an httpcontext");
    }

    /// <summary>
    /// Initiate a payment request with PayPal.
    /// When calling RequestAsync, always await the result.
    /// </summary>
    /// <param name="paymentSettings">Configuration object for PaymentProviders</param>
    public async Task<string> RequestAsync(PaymentSettings paymentSettings)
    {
        if (paymentSettings == null)
            throw new ArgumentNullException(nameof(paymentSettings));
        if (paymentSettings.Orders == null)
            throw new ArgumentNullException(nameof(paymentSettings.Orders));
        if (string.IsNullOrWhiteSpace(paymentSettings.Language))
            throw new ArgumentException(nameof(paymentSettings.Language));

        paymentSettings.Currency ??= "ISK";

        try
        {
            _logger.LogInformation("PayPal Payment Request - Start");

            var paypalSettings = paymentSettings.CustomSettings.ContainsKey(typeof(PayPalSettings))
                ? (paymentSettings.CustomSettings[typeof(PayPalSettings)] as PayPalSettings)!
                : new PayPalSettings();

            _uService.PopulatePaymentProviderProperties(
                paymentSettings,
                _ppNodeName,
                paypalSettings,
                PayPalSettings.Properties);

            var total = paymentSettings.Orders.Sum(x => x.GrandTotal);

            // Persist in database and retrieve unique order id
            var orderStatus = await _orderService.InsertAsync(
                total,
                paymentSettings,
                paypalSettings,
                null,
                _httpCtx
            ).ConfigureAwait(false);

            // Adding the reference querystring allows for easy retrieval of order using the order helper on the success/receipt page
            paymentSettings.SuccessUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.SuccessUrl, _httpCtx.Request);
            paymentSettings.SuccessUrl = PaymentsUriHelper.AddQueryString(paymentSettings.SuccessUrl, "?reference=" + orderStatus.UniqueId);

            paymentSettings.CancelUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.CancelUrl, _httpCtx.Request);
            paymentSettings.CancelUrl = PaymentsUriHelper.AddQueryString(paymentSettings.CancelUrl, "?reference=" + orderStatus.UniqueId);

            var reportUrl = PaymentsUriHelper.EnsureFullUri(new Uri(reportPath, UriKind.Relative), _httpCtx.Request);

            _logger.LogInformation("PayPal Payment Request - Amount: {Total} OrderId: {OrderUniqueId}", total, orderStatus.UniqueId);

            var baseUrl = PaymentsUriHelper.EnsureFullUri(paypalSettings.PaymentPageUrl, _httpCtx.Request);

            var formValues = new Dictionary<string, string?>
            {
                { "upload", "1" },
                { "cmd", "_cart" },
                { "business", paypalSettings.PayPalAccount },

                { "return", paymentSettings.SuccessUrl.ToString() },
                { "shopping_url", baseUrl.ToString() },
                { "notify_url", reportUrl.ToString() },

                { "currency_code", paymentSettings.Currency },
                { "lc", paymentSettings.Language },

                { "invoice", orderStatus.UniqueId.ToString() },
                { "custom", orderStatus.UniqueId.ToString() },
            };

            if (!string.IsNullOrWhiteSpace(paypalSettings.ImageUrl))
            {
                formValues.Add("image_url", paypalSettings.ImageUrl);
            }

            for (int i = 0; i < paymentSettings.Orders.Count(); i++)
            {
                var order = paymentSettings.Orders.ElementAt(i);

                var lineNumber = i + 1;

                formValues.Add($"item_name_{lineNumber}", order.Title);
                formValues.Add($"quantity_{lineNumber}", order.Quantity.ToString());
                formValues.Add($"amount_{lineNumber}", order.Price.ToString("F0"));
                formValues.Add($"discount_amount_{lineNumber}", order.Discount.ToString("F0"));
            }

            _logger.LogDebug(JsonConvert.SerializeObject(formValues));

            return FormHelper.CreateRequest(formValues, paypalSettings.PaymentPageUrl.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PayPal Payment Request - Payment Request Failed");
            Events.OnError(this, new ErrorEventArgs
            {
                Exception = ex,
            });
            throw;
        }
    }
}
