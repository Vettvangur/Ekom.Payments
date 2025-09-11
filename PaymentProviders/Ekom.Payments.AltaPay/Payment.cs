using Ekom.Payments.AltaPay.Model;
using Ekom.Payments.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Globalization;

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

            var service = new AltaPaymentService(_httpClientFactory, _logger, altaSettings);

            var total = paymentSettings.Orders.Sum(x => x.GrandTotal);

            // Persist in database and retrieve unique order id
            var orderStatus = await _orderService.InsertAsync(
                total,
                paymentSettings,
                altaSettings,
                paymentSettings.OrderUniqueId.ToString(),
                _httpCtx
            ).ConfigureAwait(false);

            await service.AuthenticateAsync();
            var sessionRequest = new SessionRequest
            {
                OrderId = orderStatus.UniqueId.ToString(),
            };
            var session = await service.CreateSession();

            session.Order = new SessionOrder
            {
                OrderId = orderStatus.UniqueId.ToString(),
                Amount = new SessionOrderAmount
                {
                    Value = (double)total,
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

            session.Callbacks = new SessionCallbacks
            {
                Success = new SessionCallback
                {
                    Value = reportUrl.ToString(),
                },
                Failure = new SessionCallback
                {
                    Value = cancelUrl.ToString(),
                },
                Redirect = paymentSettings.SuccessUrl.ToString(),
            };
            session.Configuration = new SessionConfiguration
            {
                Language = ParseSupportedLanguages(paymentSettings.Language)
            };

            await service.UpdateSession(session);

            _logger.LogInformation($"Alta Payment Request - Amount: {total} OrderId: {orderStatus.UniqueId}");

            var paymentResponse = await service.PaymentAsync(session.SessionId);

            return FormHelper.CreateRequest([], paymentResponse.Url, "GET");
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
