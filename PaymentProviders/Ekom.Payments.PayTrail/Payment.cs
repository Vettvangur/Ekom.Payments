using Ekom.Payments.Helpers;
using Ekom.Payments.PayTrail.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace Ekom.Payments.PayTrail;

/// <summary>
/// Initiate a payment request with PayTrail
/// </summary>
public class Payment : IPaymentProvider
{
    internal const string _ppNodeName = "payTrail";
    const string reportPath = "/ekom/payments/paytrailresponse";

    readonly ILogger<Payment> _logger;
    readonly IUmbracoService _uService;
    readonly IOrderService _orderService;
    readonly HttpContext _httpCtx;
    readonly IHttpClientFactory _httpClientFactory;
    readonly IConfiguration _config;

    public Payment(
        ILogger<Payment> logger,
        PaymentsConfiguration settings,
        IUmbracoService uService,
        IOrderService orderService,
        IHttpContextAccessor httpContext,
        IHttpClientFactory httpClientFactory,
        IConfiguration config)
    {
        _logger = logger;
        _uService = uService;
        _orderService = orderService;
        _httpCtx = httpContext.HttpContext ?? throw new NotSupportedException("Payment requests require an httpcontext");
        _httpClientFactory = httpClientFactory;
        _config = config;
    }

    public async Task<string> RequestAsync(PaymentSettings paymentSettings)
    {
        ArgumentNullException.ThrowIfNull(paymentSettings);
        ArgumentNullException.ThrowIfNull(paymentSettings.Orders);
        ArgumentException.ThrowIfNullOrEmpty(paymentSettings.Language);

        try
        {
            _logger.LogInformation("PayTrail Payment Request - Start");

            var payTrailSettings = paymentSettings.CustomSettings.ContainsKey(typeof(PayTrailSettings))
                ? paymentSettings.CustomSettings[typeof(PayTrailSettings)] as PayTrailSettings
                : new PayTrailSettings();
            payTrailSettings ??= new PayTrailSettings();

            _uService.PopulatePaymentProviderProperties(
                paymentSettings,
                _ppNodeName,
                payTrailSettings,
                PayTrailSettings.Properties);

            ArgumentException.ThrowIfNullOrEmpty(payTrailSettings.AccountId);
            ArgumentException.ThrowIfNullOrEmpty(payTrailSettings.SecretKey);
            ArgumentNullException.ThrowIfNull(payTrailSettings.ApiBaseUrl);

            var total = paymentSettings.Orders.Sum(x => x.GrandTotal);

            var orderStatus = await _orderService.InsertAsync(
                total,
                paymentSettings,
                payTrailSettings,
                null,
                _httpCtx
            ).ConfigureAwait(false);

            paymentSettings.SuccessUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.SuccessUrl, _httpCtx.Request);
            paymentSettings.SuccessUrl = PaymentsUriHelper.AddQueryString(paymentSettings.SuccessUrl, "?reference=" + orderStatus.UniqueId);

            var cancelUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.CancelUrl, _httpCtx.Request);
            var reportUrl = paymentSettings.ReportUrl == null
                ? PaymentsUriHelper.EnsureFullUri(new Uri(reportPath, UriKind.Relative), _httpCtx.Request)
                : PaymentsUriHelper.EnsureFullUri(paymentSettings.ReportUrl, _httpCtx.Request);
            var callbackUrl = PaymentsUriHelper.AddQueryString(reportUrl, "?callback=true");

            var createPaymentRequest = new CreatePaymentRequest
            {
                Stamp = orderStatus.UniqueId.ToString(),
                Reference = !string.IsNullOrEmpty(paymentSettings.OrderNumber) ? paymentSettings.OrderNumber : orderStatus.ReferenceId.ToString(CultureInfo.InvariantCulture),
                Amount = ToMinorUnits(total, paymentSettings.Currency),
                Currency = paymentSettings.Currency,
                Language = ParseSupportedLanguage(paymentSettings.Language),
                Items = paymentSettings.Orders.Select((lineItem, index) => new PaymentItem
                {
                    UnitPrice = ToMinorUnits(lineItem.Price, paymentSettings.Currency),
                    Units = lineItem.Quantity,
                    VatPercentage = 0,
                    ProductCode = index.ToString(CultureInfo.InvariantCulture),
                    Description = lineItem.Title,
                }).ToList(),
                Customer = CreateCustomer(paymentSettings.CustomerInfo),
                RedirectUrls = new PaymentUrls
                {
                    Success = reportUrl.ToString(),
                    Cancel = reportUrl.ToString(),
                },
                CallbackUrls = new PaymentUrls
                {
                    Success = callbackUrl.ToString(),
                    Cancel = callbackUrl.ToString(),
                },
            };

            var svc = new PayTrailService(_httpClientFactory, _logger);
            var response = await svc.CreatePaymentAsync(payTrailSettings, createPaymentRequest).ConfigureAwait(false);

            orderStatus.CustomData = response.TransactionId;
            await _orderService.UpdateAsync(orderStatus).ConfigureAwait(false);

            _logger.LogInformation("PayTrail Payment Request - Amount: {Total} OrderId: {OrderId} TransactionId: {TransactionId}", total, orderStatus.UniqueId, response.TransactionId);

            var cspNonce = CspHelper.GetCspNonce(_httpCtx, _config);
            return FormHelper.CreateRequest(new Dictionary<string, string?>(), response.Href!.ToString(), "GET", cspNonce: cspNonce);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PayTrail Payment Request - Payment Request Failed. OrderId: {OrderId} OrderNumber: {OrderNumber}", paymentSettings.OrderUniqueId, paymentSettings.OrderNumber);
            await Events.OnErrorAsync(this, new ErrorEventArgs
            {
                Exception = ex,
            });
            throw;
        }
    }

    internal static int ToMinorUnits(decimal amount, string currency)
    {
        var multiplier = IsZeroDecimalCurrency(currency) ? 1 : 100;
        return Convert.ToInt32(Math.Round(amount * multiplier, 0, MidpointRounding.AwayFromZero));
    }

    internal static bool IsZeroDecimalCurrency(string currency)
    {
        return currency.Equals("ISK", StringComparison.InvariantCultureIgnoreCase)
            || currency.Equals("JPY", StringComparison.InvariantCultureIgnoreCase)
            || currency.Equals("KRW", StringComparison.InvariantCultureIgnoreCase);
    }

    static PaymentCustomer? CreateCustomer(CustomerInfo customerInfo)
    {
        if (customerInfo == null)
        {
            return null;
        }

        var names = (customerInfo.Name ?? string.Empty).Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);

        return new PaymentCustomer
        {
            Email = customerInfo.Email,
            FirstName = names.FirstOrDefault(),
            LastName = names.Length > 1 ? names[1] : null,
            Phone = customerInfo.PhoneNumber,
        };
    }

    static string ParseSupportedLanguage(string language)
    {
        var parsed = CultureInfo.GetCultureInfo(language).TwoLetterISOLanguageName.ToUpperInvariant();

        return parsed switch
        {
            "FI" => "FI",
            "SV" => "SV",
            "EN" => "EN",
            _ => "EN",
        };
    }
}
