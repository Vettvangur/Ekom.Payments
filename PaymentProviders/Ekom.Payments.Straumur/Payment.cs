using Ekom.Payments.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text;
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
            
            if (string.IsNullOrEmpty(straumurSettings.MerchantId))
            {
                throw new ArgumentNullException(nameof(straumurSettings.MerchantId));
            }

            var sb = new StringBuilder(straumurSettings.VerificationCode);
            sb.Append("0");

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

            // Begin populating form values to be submitted
            var formValues = new Dictionary<string, string?>
            {
                { "MerchantID", straumurSettings.MerchantId },
                { "AuthorizationOnly", "0" },

                { "ReferenceNumber", orderStatus.UniqueId.ToString() },

                { "Currency", paymentSettings.Currency },
                { "Language", ParseSupportedLanguages(paymentSettings.Language) },

                { "PaymentSuccessfulURL", paymentSettings.SuccessUrl.ToString() },
                { "PaymentSuccessfulURLText", /*straumurSettings.SkipReceipt*/ true
                    ? "-" :
                    straumurSettings.PaymentSuccessfulURLText
                },
                { "PaymentSuccessfulAutomaticRedirect", /*straumurSettings.SkipReceipt*/ true
                    ? "1"
                    : "0"
                },
                { "PaymentCancelledURL", cancelUrl.ToString() },

                { "PaymentSuccessfulServerSideURL", reportUrl.ToString() },
            };

            if (straumurSettings.SessionExpiredTimeoutInSeconds != 0
            && straumurSettings.SessionExpiredRedirectURL != null)
            {
                formValues.Add("SessionExpiredTimeoutInSeconds", straumurSettings.SessionExpiredTimeoutInSeconds.ToString());
                formValues.Add("SessionExpiredRedirectURL", straumurSettings.SessionExpiredRedirectURL.ToString());
            }
            else if (straumurSettings.SessionExpiredTimeoutInSeconds != 0)
            {
                _logger.LogError("Requested session expired timeout but could not find redirect url, please configure payment provider with 'timeoutRedirectURL' property");
            }

            for (int x = 0, length = paymentSettings.Orders.Count(); x < length; x++)
            {
                var order = paymentSettings.Orders.ElementAt(x);

                var lineNumber = x + 1;

                formValues.Add($"Product_{lineNumber}_Description",
                    HttpUtility.UrlEncode(order.Title, Encoding.GetEncoding("ISO-8859-1")));
                formValues.Add($"Product_{lineNumber}_Quantity", order.Quantity.ToString());
                formValues.Add($"Product_{lineNumber}_Price", ((int)order.Price).ToString());
                formValues.Add($"Product_{lineNumber}_Discount", order.Discount.ToString());

                sb.Append(order.Quantity.ToString());
                sb.Append(((int)order.Price).ToString());
                sb.Append(order.Discount.ToString());
            }

            sb.Append(straumurSettings.MerchantId);
            sb.Append(orderStatus.UniqueId);
            sb.Append(paymentSettings.SuccessUrl);
            sb.Append(reportUrl);
            sb.Append(paymentSettings.Currency);

            formValues.Add("DigitalSignature", CryptoHelpers.GetSHA256HexStringSum(sb.ToString()));

            _logger.LogInformation("Valitor Payment Request - Amount: " + total + " OrderId: " + orderStatus.UniqueId);

            return FormHelper.CreateRequest(formValues, straumurSettings.PaymentPageUrl.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AsynchronousExample Payment Request - Payment Request Failed");
            Events.OnError(this, new ErrorEventArgs
            {
                Exception = ex,
            });
            throw;
        }
    }

    public string ParseSupportedLanguages(string language)
    {
        var parsed
            = CultureInfo.GetCultureInfo(language).TwoLetterISOLanguageName.ToUpper();

        switch (parsed)
        {
            case "IS":
            case "EN":
            case "DA":
            case "DE":
                return parsed;

            default:
                return "IS";
        }
    }
}
