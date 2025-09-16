using Azure.Core;
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

            // TODO: fjarlægja
            altaSettings.ApiUserName = "thorvardurb@vettvangur.is";
            altaSettings.ApiPassword = "testP@ssword123";
            altaSettings.Terminal = "Nespresso Test CC"; // AltaPay terminal name
            altaSettings.BaseAddress = new Uri("https://testgateway.altapaysecure.com/merchant/API/");
            altaSettings.AuthenticationUrl = new Uri("https://testgateway.altapaysecure.com/checkout/v1/api/authenticate");
            altaSettings.SessionUrl = new Uri("https://testgateway.altapaysecure.com/checkout/v1/api/session");

            _uService.PopulatePaymentProviderProperties(
                paymentSettings,
                _ppNodeName,
                altaSettings,
                AltaSettings.Properties);

            var total = paymentSettings.Orders.Sum(x => x.GrandTotal);

            var service = new AltaPaymentService(_httpClientFactory, _logger, altaSettings.PaymentConfig);
            //await service.AuthenticateAsync();
            //var session = await service.CreateSession();
            //altaSettings.SessionId = session.SessionId;

            // Persist in database and retrieve unique order id
            var orderStatus = await _orderService.InsertAsync(
                total,
                paymentSettings,
                altaSettings,
                paymentSettings.OrderUniqueId.ToString(),
                _httpCtx
            ).ConfigureAwait(false);

            paymentSettings.SuccessUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.SuccessUrl, _httpCtx.Request);
            paymentSettings.SuccessUrl = PaymentsUriHelper.AddQueryString(paymentSettings.SuccessUrl, "?reference=" + orderStatus.UniqueId);

            var cancelUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.CancelUrl, _httpCtx.Request);
            var reportUrl = paymentSettings.ReportUrl == null ? PaymentsUriHelper.EnsureFullUri(new Uri(reportPath, UriKind.Relative), _httpCtx.Request) : paymentSettings.ReportUrl;

            //session.Order = new SessionOrder
            //{
            //    OrderId = orderStatus.UniqueId.ToString(),
            //    Amount = new SessionOrderAmount
            //    {
            //        Value = (double)total,
            //        Currency = paymentSettings.Currency
            //    },
            //    OrderLines = paymentSettings.Orders.Select((lineItem, index) => new SessionOrderLine
            //    {
            //        ItemId = (index + 1).ToString(),
            //        Description = lineItem.Title,
            //        Quantity = lineItem.Quantity,
            //        UnitPrice = (int)lineItem.Price,
            //    }).ToList(),
            //};
            //session.Callbacks = new SessionCallbacks
            //{
            //    Success = new SessionCallback
            //    {
            //        Value = paymentSettings.SuccessUrl.ToString(),
            //    },
            //    Failure = new SessionCallback
            //    {
            //        Value = cancelUrl.ToString(),
            //    },
            //    Notification = reportUrl.ToString(),
            //};
            //session.Configuration = new SessionConfiguration
            //{
            //    Language = ParseSupportedLanguages(paymentSettings.Language)
            //};

            //await service.UpdateSession(session);

            _logger.LogInformation($"Alta Payment Request - Amount: {total} OrderId: {orderStatus.UniqueId}");

            var redirectUrl = await service.CreatePaymentRequestAsync(new CreateMerchantPaymentRequest
            {
                OrderId = orderStatus.UniqueId.ToString(), // Your internal order id
                Amount = total,
                Currency = paymentSettings.Currency,
                // Return URLs
                CallbackOk = "https://mysite.com/payment/success", // paymentSettings.SuccessUrl.ToString(),
                CallbackFail = "https://mysite.com/payment/fail", // cancelUrl.ToString(),
                CallbackNotification = "https://mysite.com/api/payment/altapay-notify" // reportUrl.ToString()
            });
            return FormHelper.CreateRequest([], redirectUrl, "GET");

            //var paymentResponse = await service.PaymentAsync(session.SessionId);

            //return FormHelper.CreateRequest([], paymentResponse.Url, "GET");
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
