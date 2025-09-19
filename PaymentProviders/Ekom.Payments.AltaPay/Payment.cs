using Azure.Core;
using Ekom.Payments.AltaPay.Model;
using Ekom.Payments.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Text;
using System.Xml.Linq;
using System;
using System.Reflection;

namespace Ekom.Payments.AltaPay;

/// <summary>
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

    /// <summary>
    /// ctor for Unit Tests
    /// </summary>
    public Payment(
        ILogger<Payment> logger,
        PaymentsConfiguration settings,
        IUmbracoService uService,
        IOrderService orderService,
        IHttpContextAccessor httpContext,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _settings = settings;
        _uService = uService;
        _orderService = orderService;
        _httpCtx = httpContext.HttpContext ?? throw new NotSupportedException("Payment requests require an httpcontext");
        _httpClientFactory=httpClientFactory;
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
            altaSettings.PaymentFormUrl = altaSettings.PaymentFormUrl == null ? null : PaymentsUriHelper.EnsureFullUri(altaSettings.PaymentFormUrl, _httpCtx.Request);

            _logger.LogInformation($"Alta Payment Request - Amount: {total} OrderId: {orderStatus.UniqueId}");

            var client = _httpClientFactory.CreateClient();
            client.BaseAddress = altaSettings.BaseAddress;
            var byteArray = Encoding.ASCII.GetBytes($"{altaSettings.ApiUserName}:{altaSettings.ApiPassword}");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));

            var form = new Dictionary<string, string>
            {
                { "terminal", altaSettings.Terminal }, // AltaPay terminal name
                { "shop_orderid", orderStatus.UniqueId.ToString() },
                { "amount", total.ToString("0.00", CultureInfo.InvariantCulture) },
                { "currency", paymentSettings.Currency },
                // Return URLs
                { "config[callback_ok]", okUrl.ToString() },
                { "config[callback_fail]", failUrl.ToString() },
                { "config[callback_notification]", reportUrl.ToString() },
                // Optional parameters
                { "language", ParseSupportedLanguages(paymentSettings.Language) },
            };
            if (altaSettings.PaymentFormUrl != null)
            {
                form["config[callback_form]"] = altaSettings.PaymentFormUrl.ToString();
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
            var xml = XDocument.Parse(await response.Content.ReadAsStringAsync());
            // Extract the payment URL (redirect the customer here)
            var url = xml.Descendants("Url").FirstOrDefault()?.Value;

            return FormHelper.CreateRequest([], url, "GET");
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
