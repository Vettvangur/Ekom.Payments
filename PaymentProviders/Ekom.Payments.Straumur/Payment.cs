using Ekom.Payments.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Web;

namespace Ekom.Payments.Straumur;

/// <summary>
/// Initiate a payment request with Straumur
/// </summary>
public class Payment : IPaymentProvider
{
    internal const string _ppNodeName = "straumur";
    /// <summary>
    /// Ekom.Payments ResponseController
    /// </summary>
    const string reportPath = "/ekom/payments/straumurresponse";

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
    /// Initiate a payment request with Straumur.
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
            _logger.LogInformation("Straumur Payment Request - Start");

            var straumurSettings = paymentSettings.CustomSettings.ContainsKey(typeof(StraumurSettings))
                ? paymentSettings.CustomSettings[typeof(StraumurSettings)] as StraumurSettings
                : new StraumurSettings();

            _uService.PopulatePaymentProviderProperties(
                paymentSettings,
                _ppNodeName,
                straumurSettings,
                StraumurSettings.Properties);

            var total = paymentSettings.Orders.Sum(x => x.GrandTotal);

            ArgumentNullException.ThrowIfNull(straumurSettings.PaymentPageUrl);
            
            if (string.IsNullOrEmpty(straumurSettings.TerminalIdenitifer))
            {
                throw new ArgumentNullException(nameof(straumurSettings.TerminalIdenitifer));
            }

            // Persist in database and retrieve unique order id
            var orderStatus = await _orderService.InsertAsync(
                total,
                paymentSettings,
                straumurSettings,
                paymentSettings.OrderUniqueId.ToString(),
                _httpCtx
            ).ConfigureAwait(false);

            paymentSettings.SuccessUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.SuccessUrl, _httpCtx.Request);
            paymentSettings.SuccessUrl = PaymentsUriHelper.AddQueryString(paymentSettings.SuccessUrl, "?reference=" + orderStatus.UniqueId);

            var cancelUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.CancelUrl, _httpCtx.Request);
            var reportUrl = paymentSettings.ReportUrl == null ? PaymentsUriHelper.EnsureFullUri(new Uri(reportPath, UriKind.Relative), _httpCtx.Request) : paymentSettings.ReportUrl;

            var items = new List<Item>();

            foreach (var lineItem in paymentSettings.Orders)
            {
                var item = new Item()
                {
                    Name = lineItem.Title,
                    Quantity = lineItem.Quantity,
                    UnitPrice = (int)lineItem.Price * 100, // Price is in ISK, Straumur requires two decimal places
                    Amount = (int)lineItem.GrandTotal * 100, // Price is in ISK, Straumur requires two decimal places
                };

                items.Add(item);
            }

            var reference = orderStatus.UniqueId.ToString();

            if (straumurSettings.AddOrderToReference)
            {
                var referenceParts = new List<string>();
                referenceParts.AddRange(items.Select(i => i.Name));
                referenceParts.Add(reference);
                reference = string.Join(";", referenceParts);
            }

            var request = new PaymentRequest
            {
                TerminalIdentifier = straumurSettings.TerminalIdenitifer,
                Reference = orderStatus.UniqueId.ToString(),
                Currency = paymentSettings.Currency,
                Amount = (int)total * 100, // Price is in ISK, Straumur requires two decimal places
                ReturnUrl = paymentSettings.SuccessUrl.ToString(),
                Culture = ParseSupportedLanguages(paymentSettings.Language),
                Items = items
            };

            _logger.LogInformation($"Straumur Payment Request - Amount: {total} OrderId: {orderStatus.UniqueId}");

            var httpClient = _httpClientFactory.CreateClient("straumur");

            var responseMessage = await httpClient.PostAsJsonAsync(straumurSettings.PaymentPageUrl, request);

            var responseContent = await responseMessage.Content.ReadAsStringAsync();

            try
            {
                responseMessage.EnsureSuccessStatusCode();
            }
            catch(Exception ex)
            {
                var error = JsonSerializer.Deserialize<StraumurError>(responseContent);

                _logger.LogError(ex, "Straumur Payment Request Error" , error);

                throw;
            }

            var response = JsonSerializer.Deserialize<PaymentRequestResponse>(responseContent);

            return FormHelper.CreateRequest(new Dictionary<string, string?>(), response.Url, "GET");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Straumur Payment Request - Payment Request Failed");
            Events.OnError(this, new ErrorEventArgs
            {
                Exception = ex,
            });
            throw;
        }
    }

    public static string ParseSupportedLanguages(string language)
    {
        var parsed
            = CultureInfo.GetCultureInfo(language).TwoLetterISOLanguageName.ToUpper();

        switch (parsed)
        {
            case "IS":
                return "is";
            case "EN":
                return "en";
            default:
                return "is";
        }
    }
}
