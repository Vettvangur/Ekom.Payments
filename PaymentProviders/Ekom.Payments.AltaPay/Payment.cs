using Ekom.Payments.AltaPay.Model;
using Ekom.Payments.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Ekom.Payments.AltaPay;

/// <summary>s
/// Initiate a payment request with Alta
/// </summary>
public class Payment : IPaymentProvider
{
    internal const string _ppNodeName = "altaPay";
    /// <summary>
    /// Ekom.Payments ResponseController
    /// </summary>
    const string reportPath = "/ekom/payments/altaresponse";

    readonly ILogger<Payment> _logger;
    readonly PaymentsConfiguration _settings;
    readonly IUmbracoService _uService;
    readonly IOrderService _orderService;
    readonly HttpContext _httpCtx;
    readonly IHttpClientFactory _httpClientFactory;
    readonly IConfiguration _configuration;
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
        IConfiguration configuration)
    {
        _logger = logger;
        _settings = settings;
        _uService = uService;
        _orderService = orderService;
        _httpCtx = httpContext.HttpContext ?? throw new NotSupportedException("Payment requests require an httpcontext");
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
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

        try
        {
            _logger.LogInformation("Alta Payment Request - Start");

            var altaSettings = paymentSettings.CustomSettings.ContainsKey(typeof(AltaSettings))
                ? paymentSettings.CustomSettings[typeof(AltaSettings)] as AltaSettings
                : new AltaSettings();

            _uService.PopulatePaymentProviderProperties(
                paymentSettings,
                _ppNodeName,
                altaSettings,
                AltaSettings.Properties);

            var total = paymentSettings.Orders.Sum(x => x.GrandTotal);

            var errorUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.ErrorUrl, _httpCtx.Request);

            // Persist in database and retrieve unique order id
            var orderStatus = await _orderService.InsertAsync(
                total,
                paymentSettings,
                altaSettings,
                paymentSettings.OrderUniqueId.ToString(),
                _httpCtx
            ).ConfigureAwait(false);

            // Localhost is not valid for callbacks, override host if needed
            if (altaSettings.HostOverride != null)
            {
                _httpCtx.Request.Host = new HostString(altaSettings.HostOverride);
            }

            var okUrl = PaymentsUriHelper.EnsureFullUri(new Uri(reportPath, UriKind.Relative), _httpCtx.Request);
            var failUrl = PaymentsUriHelper.EnsureFullUri(new Uri(reportPath + "/fail", UriKind.Relative), _httpCtx.Request);
            var reportUrl = paymentSettings.ReportUrl == null ? PaymentsUriHelper.EnsureFullUri(new Uri(reportPath, UriKind.Relative), _httpCtx.Request) : paymentSettings.ReportUrl;
            var formUrl = altaSettings.PaymentFormUrl == null ? null : PaymentsUriHelper.EnsureFullUri(new Uri(altaSettings.PaymentFormUrl, UriKind.Relative), _httpCtx.Request);

            _logger.LogInformation($"Alta Payment Request - Amount: {total} OrderId: {paymentSettings.OrderUniqueId}");

            var client = _httpClientFactory.CreateClient();
            client.BaseAddress = altaSettings.BaseAddress;
            var byteArray = Encoding.ASCII.GetBytes($"{altaSettings.ApiUserName}:{altaSettings.ApiPassword}");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));

            var form = new Dictionary<string, string>
            {
                { "terminal", altaSettings.Terminal }, // AltaPay terminal name
                { "shop_orderid", paymentSettings.OrderUniqueId.ToString() },
                { "amount", total.ToString("0.00", CultureInfo.InvariantCulture) },
                { "currency", paymentSettings.Currency },
                // Return URLs
                { "config[callback_ok]", okUrl.ToString().Replace("//ekom","/ekom", StringComparison.InvariantCultureIgnoreCase) },
                { "config[callback_fail]", failUrl.ToString().Replace("//ekom","/ekom", StringComparison.InvariantCultureIgnoreCase) },
                { "config[callback_notification]", reportUrl.ToString().Replace("//ekom","/ekom", StringComparison.InvariantCultureIgnoreCase) },
                // Optional parameters
                { "language", ParseSupportedLanguages(paymentSettings.Language) },
                { "payment_source", "eCommerce" },
                { "type", "paymentAndCapture" },
                { "transaction_info[0]", paymentSettings.Store }
            };

            if (!string.IsNullOrWhiteSpace(formUrl.ToString()))
            {
                form["config[callback_form]"] = formUrl.ToString().Replace("//ekom", "/ekom", StringComparison.InvariantCultureIgnoreCase);
            }

            foreach (var (index, line) in paymentSettings.Orders.Select((order, idx) => new KeyValuePair<int,OrderItem>(idx, order)))
            {
                form[$"orderLines[{index}][description]"] = line.Title;
                form[$"orderLines[{index}][itemId]"] = (index + 1).ToString();
                form[$"orderLines[{index}][quantity]"] = line.Quantity.ToString();
                form[$"orderLines[{index}][unitPrice]"] = line.Price.ToString("0.00", CultureInfo.InvariantCulture);
            }

            using var content = new FormUrlEncodedContent(form);
            var response = await client.PostAsync("createPaymentRequest", content);
            response.EnsureSuccessStatusCode();
            var contentString = await response.Content.ReadAsStringAsync();
            var xml = XDocument.Parse(contentString);
            // Extract the payment URL (redirect the customer here)
            var url = xml.Descendants("Url").FirstOrDefault()?.Value;

            var errorMessage = xml.Descendants("ErrorMessage").FirstOrDefault()?.Value;

            if (string.IsNullOrEmpty(url))
            {
                url = errorUrl.ToString() + "?errorStatus=paymenterror&errorMessage=We could no process your payment. Try again or contact Nespresso.";
                _logger.LogError($"Alta Payment Request - Error creating Payment - Request: {JsonSerializer.Serialize(form)} - Response: {contentString} OrderId: {paymentSettings.OrderUniqueId} Message: {errorMessage}");
            }

            return FormHelper.Redirect(url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Alta Payment Request - Payment Request Failed");
            await Events.OnErrorAsync(this, new ErrorEventArgs
            {
                Exception = ex,
            });
            throw;
        }
    }

    private static string ParseSupportedLanguages(string language)
    {
        return CultureInfo.GetCultureInfo(language).TwoLetterISOLanguageName.ToUpper() switch
        {
            "IS" => "is",
            "EN" => "en",
            _ => "is",
        };
    }
}
