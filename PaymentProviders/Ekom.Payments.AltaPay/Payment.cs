using Ekom.Payments.AltaPay.Model;
using Ekom.Payments.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
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

    /// <summary>
    /// Marks the address AltaPay returns the customer's browser to (callback_ok),
    /// so the response controller can tell it from the server notification.
    /// </summary>
    internal const string BrowserQueryKey = "browser";

    readonly ILogger<Payment> _logger;
    readonly PaymentsConfiguration _settings;
    readonly IUmbracoService _uService;
    readonly Ekom.Services.IOrderPayments _payments;
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
        Ekom.Services.IOrderPayments payments,
        IHttpContextAccessor httpContext,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
    {
        _logger = logger;
        _settings = settings;
        _uService = uService;
        _payments = payments;
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
            altaSettings ??= new AltaSettings();

            _uService.PopulatePaymentProviderProperties(
                paymentSettings,
                _ppNodeName,
                altaSettings,
                AltaSettings.Properties);

            // Checked before the payment is created: without these AltaPay cannot be asked for a payment page,
            // and without the shared secret its answer cannot be verified.
            EnsureRequiredSettings(altaSettings);

            var total = paymentSettings.Orders.Sum(x => x.GrandTotal);

            var errorUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.ErrorUrl, _httpCtx.Request);

            // Store the payment on the Ekom order. AltaPay identifies it by the order id (shop_orderid) in its responses.
            var payment = await _payments.CreateAsync(
                paymentSettings,
                _ppNodeName,
                total,
                altaSettings,
                providerReference: paymentSettings.OrderUniqueId.ToString(),
                httpContext: _httpCtx
            ).ConfigureAwait(false);

            // Localhost is not valid for callbacks, override host if needed
            if (altaSettings.HostOverride != null)
            {
                _httpCtx.Request.Host = new HostString(altaSettings.HostOverride);
            }

            var okUrl = PaymentsUriHelper.EnsureFullUri(new Uri(reportPath + "?" + BrowserQueryKey + "=true", UriKind.Relative), _httpCtx.Request);
            var failUrl = PaymentsUriHelper.EnsureFullUri(new Uri(reportPath + "/fail", UriKind.Relative), _httpCtx.Request);
            var reportUrl = paymentSettings.ReportUrl == null ? PaymentsUriHelper.EnsureFullUri(new Uri(reportPath, UriKind.Relative), _httpCtx.Request) : paymentSettings.ReportUrl;
            // Optional: a path on this site or an absolute URL
            var formUrl = string.IsNullOrWhiteSpace(altaSettings.PaymentFormUrl) ? null : PaymentsUriHelper.EnsureFullUri(altaSettings.PaymentFormUrl.Trim(), _httpCtx.Request);

            _logger.LogInformation("Alta Payment Request - " + PaymentLog.Template + " Amount: {Total}",
                payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId, total);

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
                { "config[callback_ok]", WithoutDoubleSlashes(okUrl) },
                { "config[callback_fail]", WithoutDoubleSlashes(failUrl) },
                { "config[callback_notification]", WithoutDoubleSlashes(reportUrl) },
                // Optional parameters
                { "language", ParseSupportedLanguages(paymentSettings.Language) },
                { "payment_source", "eCommerce" },
                { "type", "paymentAndCapture" },
                { "transaction_info[0]", paymentSettings.Store }
            };

            if (formUrl != null)
            {
                form["config[callback_form]"] = WithoutDoubleSlashes(formUrl);
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
                url = QueryHelpers.AddQueryString(errorUrl.ToString(), new Dictionary<string, string?>
                {
                    ["errorStatus"] = "paymenterror",
                    ["errorMessage"] = "We could not process your payment. Please try again.",
                });
                _logger.LogError("Alta Payment Request - Error creating payment. " + PaymentLog.Template + " Message: {ErrorMessage} Response: {Response}",
                    payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId, errorMessage, contentString);
            }

            var cspNonce = CspHelper.GetCspNonce(_httpCtx, _configuration);

            return FormHelper.Redirect(url, cspNonce: cspNonce);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Alta Payment Request - Failed. OrderNumber: {OrderNumber} OrderId: {OrderUniqueId}",
                paymentSettings.OrderNumber, paymentSettings.OrderUniqueId);
            throw;
        }
    }

    static void EnsureRequiredSettings(AltaSettings settings)
    {
        static InvalidOperationException Missing(string name)
            => new($"AltaPay setting {name} is not configured. Set it on the {_ppNodeName} payment provider node, under Ekom:Payments:{_ppNodeName} or with SetAltaPaySettings.");

        if (string.IsNullOrWhiteSpace(settings.Terminal)) throw Missing(nameof(AltaSettings.Terminal));
        if (string.IsNullOrWhiteSpace(settings.ApiUserName)) throw Missing(nameof(AltaSettings.ApiUserName));
        if (string.IsNullOrWhiteSpace(settings.ApiPassword)) throw Missing(nameof(AltaSettings.ApiPassword));
        if (settings.BaseAddress == null || !settings.BaseAddress.IsAbsoluteUri) throw Missing(nameof(AltaSettings.BaseAddress) + " (an absolute URL)");
        if (string.IsNullOrWhiteSpace(settings.CustomerInformationSharedSecret)) throw Missing(nameof(AltaSettings.CustomerInformationSharedSecret));

        if (!string.IsNullOrWhiteSpace(settings.PaymentFormUrl)
            && !Uri.IsWellFormedUriString(settings.PaymentFormUrl.Trim(), UriKind.RelativeOrAbsolute))
        {
            throw new InvalidOperationException(
                $"AltaPay setting {nameof(AltaSettings.PaymentFormUrl)} \"{settings.PaymentFormUrl}\" is not a valid path or absolute URL.");
        }
    }

    /// <summary>
    /// Removes empty segments from the path, f.x. when HostOverride or ReportUrl end with a slash.
    /// The scheme and host are left alone.
    /// </summary>
    internal static string WithoutDoubleSlashes(Uri uri)
    {
        if (!uri.IsAbsoluteUri || !uri.AbsolutePath.Contains("//", StringComparison.Ordinal))
        {
            return uri.ToString();
        }

        var builder = new UriBuilder(uri);
        var path = builder.Path;
        while (path.Contains("//", StringComparison.Ordinal))
        {
            path = path.Replace("//", "/", StringComparison.Ordinal);
        }
        builder.Path = path;

        return builder.Uri.ToString();
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
