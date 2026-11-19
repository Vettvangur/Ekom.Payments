using Ekom.Payments.Helpers;
using Ekom.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace Ekom.Payments.Teya;

/// <summary>
/// Initiate a payment request with Teya
/// </summary>
class Payment : IPaymentProvider
{
    /// <summary>
    /// Still the Borgun name: it is the payment provider node name on existing sites
    /// and the provider type stored on their payments.
    /// </summary>
    internal const string _ppNodeName = "borgun";
    /// <summary>
    /// Ekom.Payments ResponseController. Keeps the Borgun url so payments already in flight complete.
    /// </summary>
    const string reportPath = "/ekom/payments/BorgunResponse";

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

    internal static string FormatPrice(decimal price, TeyaSettings teyaSettings)
    {
        if (teyaSettings.PreserveDecimalAmounts)
        {
            var decimalAmount = Math.Round(price, 2, MidpointRounding.AwayFromZero);
            return decimalAmount.ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',');
        }

        var rounded = Math.Round(price, 0, MidpointRounding.AwayFromZero);
        return $"{rounded},00";
    }

    /// <summary>
    /// The language sent to Teya: the two-letter language code in upper case, "IS" for "is-IS" and "EN" for "en-US".
    /// </summary>
    internal static string FormatLanguage(string language)
    {
        var separator = language.IndexOfAny(['-', '_']);
        var code = separator > 0 ? language[..separator] : language;

        return code.ToUpperInvariant();
    }

    /// <summary>
    /// The order id sent to Teya for a payment, Teya returns it with the payment confirmation.
    /// </summary>
    internal static string GetTeyaOrderId(PaymentSettings paymentSettings, Guid uniqueId)
        => string.IsNullOrEmpty(paymentSettings.OrderNumber)
            ? uniqueId.ToString().Split('-').Last()
            : paymentSettings.OrderNumber;

    /// <summary>
    /// Initiate a payment request with Teya.
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

        paymentSettings.Currency ??= "ISK";

        using var scope = PaymentLog.BeginScope(_logger, paymentSettings, _ppNodeName);

        try
        {
            _logger.LogInformation("Teya Payment Request - Start. OrderNumber: {OrderNumber} OrderId: {OrderUniqueId}",
                paymentSettings.OrderNumber, paymentSettings.OrderUniqueId);

            var teyaSettings = paymentSettings.CustomSettings.ContainsKey(typeof(TeyaSettings))
                ? (paymentSettings.CustomSettings[typeof(TeyaSettings)] as TeyaSettings)!
                : new TeyaSettings();

            _uService.PopulatePaymentProviderProperties(
                paymentSettings,
                _ppNodeName,
                teyaSettings,
                TeyaSettings.Properties);

            var total = paymentSettings.Orders.Sum(x => x.GrandTotal);

            // Checked before the payment is stored, so a misconfigured provider stores nothing.
            ArgumentNullException.ThrowIfNull(teyaSettings.PaymentPageUrl);

            if (string.IsNullOrEmpty(teyaSettings.MerchantId))
            {
                throw new ArgumentNullException(nameof(teyaSettings.MerchantId));
            }

            if (string.IsNullOrEmpty(teyaSettings.SecretCode))
            {
                throw new ArgumentNullException(nameof(teyaSettings.SecretCode));
            }

            paymentSettings.SuccessUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.SuccessUrl, _httpCtx.Request);
            paymentSettings.CancelUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.CancelUrl, _httpCtx.Request);
            paymentSettings.ErrorUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.ErrorUrl, _httpCtx.Request);

            var reportUrl = paymentSettings.ReportUrl == null ? PaymentsUriHelper.EnsureFullUri(new Uri(reportPath, UriKind.Relative), _httpCtx.Request) : paymentSettings.ReportUrl;
            var currencyFormat = new CultureInfo(paymentSettings.Currency, false).NumberFormat;


            // Begin populating form values to be submitted
            var formValues = new Dictionary<string, string>
            {
                { "merchantid", teyaSettings.MerchantId },
                { "paymentgatewayid", teyaSettings.PaymentGatewayId.ToString() },
                { "returnurlsuccess", paymentSettings.SuccessUrl.ToString() },
                { "returnurlcancel", paymentSettings.CancelUrl.ToString().AddQueryParam("paymentError","cancelPayment" ) },
                { "returnurlerror", paymentSettings.ErrorUrl.ToString().AddQueryParam("paymentError","errorPayment" ) },
                { "returnurlsuccessserver", reportUrl.ToString() },
                { "amount", FormatPrice(total, teyaSettings) },
                { "currency", paymentSettings.Currency },
                { "language", FormatLanguage(paymentSettings.Language) }
            };

            for (int lineNumber = 0, length = paymentSettings.Orders.Count(); lineNumber < length; lineNumber++)
            {
                var order = paymentSettings.Orders.ElementAt(lineNumber);

                formValues.Add("itemdescription_" + lineNumber, order.Title);
                formValues.Add("itemcount_" + lineNumber, order.Quantity.ToString());
                formValues.Add("itemunitamount_" + lineNumber, FormatPrice(order.Price, teyaSettings));
                formValues.Add("itemamount_" + lineNumber, FormatPrice(order.GrandTotal, teyaSettings));
            }

            //if (teyaSettings.SkipReceipt)
            //{
                formValues.Add("skipreceiptpage", "1");
            //}

            if (!string.IsNullOrEmpty(teyaSettings.MerchantEmail))
            {
                formValues.Add("merchantemail", teyaSettings.MerchantEmail);
            }

            if (teyaSettings.MerchantLogo != null)
            {
                formValues.Add("merchantlogo", teyaSettings.MerchantLogo.ToString());
            }

            // Teya portal page type.
            // If set as 1 then cardholder is required to insert email, 
            // mobile number and home address. 
            // Merchant email parameter must be set since cardholder information is returned through email to merchant.

            // By default Customer is not asked for email, mobile number and home address

            // If == 1 Customer is presented with inputs for email, mobile number and home address
            if (teyaSettings.RequireCustomerInformation
            && !string.IsNullOrEmpty(teyaSettings.MerchantEmail))
            {
                formValues.Add("pagetype", "1");
            }

            // Store the payment on the Ekom order. Teya echoes the payment id back as "reference".
            var payment = await _payments.CreateAsync(
                paymentSettings,
                _ppNodeName,
                total,
                teyaSettings,
                httpContext: _httpCtx
            ).ConfigureAwait(false);

            // Teya only supports specific types of order id's
            var teyaOrderId = GetTeyaOrderId(paymentSettings, payment.PaymentId);

            //CheckHash
            var checkHash = CryptoHelpers.GetHMACSHA256(teyaSettings.SecretCode,
                new CheckHashMessage(
                    teyaSettings.MerchantId,
                    paymentSettings.SuccessUrl.ToString(),
                    reportUrl.ToString(),
                    teyaOrderId,
                    FormatPrice(total, teyaSettings),
                    paymentSettings.Currency
                ).Message
            );

            formValues.Add("checkhash", checkHash);
            formValues.Add("reference", payment.PaymentId.ToString());
            formValues.Add("orderid", teyaOrderId);

            _logger.LogInformation("Teya Payment Request - " + PaymentLog.Template + " Amount: {Total}",
                payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId, total);

            var cspNonce = CspHelper.GetCspNonce(_httpCtx, _config);

            return FormHelper.CreateRequest(formValues, teyaSettings.PaymentPageUrl.ToString(), cspNonce: cspNonce);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Teya Payment Request - Failed. OrderNumber: {OrderNumber} OrderId: {OrderUniqueId}",
                paymentSettings.OrderNumber, paymentSettings.OrderUniqueId);
            throw;
        }
    }
}
