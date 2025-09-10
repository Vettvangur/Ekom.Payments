using Azure.Core;
using Ekom.Payments.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

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

            var accessToken = await GetAuthenticationTokenAsync(altaSettings);
            var session = await CreateSession(altaSettings, accessToken);

            var total = paymentSettings.Orders.Sum(x => x.GrandTotal);

            ArgumentNullException.ThrowIfNull(altaSettings.PaymentPageUrl);

            if (string.IsNullOrEmpty(altaSettings.TerminalIdenitifer))
            {
                throw new ArgumentNullException(nameof(altaSettings.TerminalIdenitifer));
            }

            // Persist in database and retrieve unique order id
            var orderStatus = await _orderService.InsertAsync(
                total,
                paymentSettings,
                altaSettings,
                paymentSettings.OrderUniqueId.ToString(),
                _httpCtx
            ).ConfigureAwait(false);

            session.Order = new SessionOrder
            {
                OrderId = orderStatus.UniqueId.ToString(),
                Amount = new SessionOrderAmount
                {
                    Value = (int)total * 100, // Price is in ISK, Straumur requires two decimal places
                    Currency = paymentSettings.Currency
                },
                OrderLines = paymentSettings.Orders.Select((lineItem, index) => new SessionOrderLine
                {
                    ItemId = (index + 1).ToString(),
                    Description = lineItem.Title,
                    Quantity = lineItem.Quantity,
                    UnitPrice = (int)lineItem.Price,
                }).ToList(),
            };

            paymentSettings.SuccessUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.SuccessUrl, _httpCtx.Request);
            paymentSettings.SuccessUrl = PaymentsUriHelper.AddQueryString(paymentSettings.SuccessUrl, "?reference=" + orderStatus.UniqueId);

            var cancelUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.CancelUrl, _httpCtx.Request);
            var reportUrl = paymentSettings.ReportUrl == null ? PaymentsUriHelper.EnsureFullUri(new Uri(reportPath, UriKind.Relative), _httpCtx.Request) : paymentSettings.ReportUrl;

            //var items = new List<Item>();

            //foreach (var lineItem in paymentSettings.Orders)
            //{
            //    // TODO: þetta þarf að vera skoða meira
            //    var item = new Item()
            //    {
            //        Name = lineItem.Title,
            //        Quantity = lineItem.Quantity,
            //        UnitPrice = (int)lineItem.Price * 100, // Price is in ISK, Straumur requires two decimal places
            //        Amount = (int)lineItem.GrandTotal * 100, // Price is in ISK, Straumur requires two decimal places
            //    };

            //    items.Add(item);
            //}

            //var reference = orderStatus.UniqueId.ToString();

            //if (altaSettings.AddOrderToReference == "true")
            //{
            //    var referenceParts = new List<string>();
            //    referenceParts.AddRange(items.Select(i => i.Name));
            //    referenceParts.Add(reference);
            //    reference = string.Join(";", referenceParts);
            //}

            var request = new PaymentRequest
            {
                TerminalIdentifier = altaSettings.TerminalIdenitifer,
                Reference = reference,
                Currency = paymentSettings.Currency,
                Amount = (int)total * 100, // Price is in ISK, Straumur requires two decimal places
                ReturnUrl = paymentSettings.SuccessUrl.ToString(),
                Culture = ParseSupportedLanguages(paymentSettings.Language),
                Items = items
            };

            _logger.LogInformation($"Alta Payment Request - Amount: {total} OrderId: {orderStatus.UniqueId}");

            var httpClient = _httpClientFactory.CreateClient();

            httpClient.DefaultRequestHeaders.Add("X-API-key", altaSettings.ApiKey);

            var responseMessage = await httpClient.PostAsJsonAsync(altaSettings.PaymentPageUrl, request);

            var responseContent = await responseMessage.Content.ReadAsStringAsync();

            try
            {
                responseMessage.EnsureSuccessStatusCode();
            }
            catch(Exception ex)
            {
                _logger.LogError(ex, "Alta Payment Request Error Content", responseContent);

                throw;
            }

            var response = JsonSerializer.Deserialize<PaymentRequestResponse>(responseContent);

            return FormHelper.CreateRequest(new Dictionary<string, string?>(), response.Url, "GET");
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

    private async Task<string> GetAuthenticationTokenAsync(AltaSettings altaSettings)
    {
        var httpClient = _httpClientFactory.CreateClient();

        var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{altaSettings.ApiUserName}:{altaSettings.ApiPassword}"));
        var authorization = new AuthenticationHeaderValue("Basic", credentials);

        httpClient.DefaultRequestHeaders.Authorization = authorization;

        var responseMessage = await httpClient.PostAsync(altaSettings.AuthenticationUrl, null);
        var responseContent = await responseMessage.Content.ReadFromJsonAsync<AuthenticationResponse>();
        return responseContent?.Token ?? throw new InvalidOperationException("Authentication token was null");
    }

    private async Task<SessionResponse> CreateSession(AltaSettings altaSettings, string accessToken)
    {
        var httpClient = _httpClientFactory.CreateClient();

        var authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        httpClient.DefaultRequestHeaders.Authorization = authorization;

        var responseMessage = await httpClient.PostAsync(altaSettings.SessionUrl, null);
        var responseContent = await responseMessage.Content.ReadFromJsonAsync<SessionResponse>();
        return responseContent ?? throw new InvalidOperationException("Session response was null");
    }

    private static string ParseSupportedLanguages(string language)
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
