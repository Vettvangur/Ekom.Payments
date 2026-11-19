using Ekom.Payments.Helpers;
using Ekom.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text;
using System.Web;

namespace Ekom.Payments.Rapyd;

/// <summary>
/// Initiate a payment request with Rapyd
/// </summary>
public class Payment : IPaymentProvider
{
    /// <summary>
    /// Still the Valitor name: it is the payment provider node name on existing sites
    /// and the provider type stored on their payments.
    /// </summary>
    internal const string _ppNodeName = "valitor";
    /// <summary>
    /// Ekom.Payments ResponseController. Keeps the Valitor url so payments already in flight complete.
    /// </summary>
    const string reportPath = "/ekom/payments/valitorresponse";

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

    /// <summary>
    /// Initiate a payment request with Rapyd.
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
            _logger.LogInformation("Rapyd Payment Request - Start");

            var rapydSettings = paymentSettings.CustomSettings.ContainsKey(typeof(RapydSettings))
                ? paymentSettings.CustomSettings[typeof(RapydSettings)] as RapydSettings
                : new RapydSettings();

            _uService.PopulatePaymentProviderProperties(
                paymentSettings,
                _ppNodeName,
                rapydSettings,
                RapydSettings.Properties);

            var total = paymentSettings.Orders.Sum(x => x.GrandTotal);
            if (rapydSettings.LoanType != 0 && total < 30000)
            {
                throw new RapydAHKException("Requested loan amount will likely not meet icelandic ÁHK requirements");
            }

            if (rapydSettings.LoanType != 0 && string.IsNullOrEmpty(rapydSettings.MerchantName))
            {
                throw new NotSupportedException(
                    "Rapyd Loans require MerchantName parameter");
            }

            ArgumentNullException.ThrowIfNull(rapydSettings.PaymentPageUrl);
            
            if (string.IsNullOrEmpty(rapydSettings.MerchantId))
            {
                throw new ArgumentNullException(nameof(rapydSettings.MerchantId));
            }

            var sb = new StringBuilder(rapydSettings.VerificationCode);
            sb.Append("0");

            // Store the payment on the Ekom order. Rapyd echoes the payment id back as its reference number.
            var payment = await _payments.CreateAsync(
                paymentSettings,
                _ppNodeName,
                total,
                rapydSettings,
                httpContext: _httpCtx
            ).ConfigureAwait(false);

            paymentSettings.SuccessUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.SuccessUrl, _httpCtx.Request);
            paymentSettings.SuccessUrl = PaymentsUriHelper.AddQueryString(paymentSettings.SuccessUrl, "?reference=" + payment.PaymentId);

            var cancelUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.CancelUrl, _httpCtx.Request);
            var reportUrl = paymentSettings.ReportUrl == null ? PaymentsUriHelper.EnsureFullUri(new Uri(reportPath, UriKind.Relative), _httpCtx.Request) : paymentSettings.ReportUrl;

            // Begin populating form values to be submitted
            var formValues = new Dictionary<string, string?>
            {
                { "MerchantID", rapydSettings.MerchantId },
                { "AuthorizationOnly", "0" },

                { "ReferenceNumber", payment.PaymentId.ToString() },

                { "Currency", paymentSettings.Currency },
                { "Language", ParseSupportedLanguages(paymentSettings.Language) },

                { "PaymentSuccessfulURL", paymentSettings.SuccessUrl.ToString() },
                { "PaymentSuccessfulURLText", /*rapydSettings.SkipReceipt*/ true
                    ? "-" :
                    rapydSettings.PaymentSuccessfulURLText
                },
                { "PaymentSuccessfulAutomaticRedirect", /*rapydSettings.SkipReceipt*/ true
                    ? "1"
                    : "0"
                },
                { "PaymentCancelledURL", cancelUrl.ToString() },

                { "PaymentSuccessfulServerSideURL", reportUrl.ToString() },
            };

            if (rapydSettings.SessionExpiredTimeoutInSeconds != 0
            && rapydSettings.SessionExpiredRedirectURL != null)
            {
                formValues.Add("SessionExpiredTimeoutInSeconds", rapydSettings.SessionExpiredTimeoutInSeconds.ToString());
                formValues.Add("SessionExpiredRedirectURL", rapydSettings.SessionExpiredRedirectURL.ToString());
            }
            else if (rapydSettings.SessionExpiredTimeoutInSeconds != 0)
            {
                _logger.LogError("Requested session expired timeout but could not find redirect url, please configure payment provider with 'timeoutRedirectURL' property");
            }

            for (int x = 0, length = paymentSettings.Orders.Count(); x < length; x++)
            {
                var order = paymentSettings.Orders.ElementAt(x);

                var lineNumber = x + 1;

                formValues.Add($"Product_{lineNumber}_Description",
                    HttpUtility.UrlEncode(order.Title, Encoding.GetEncoding("ISO-8859-1")));
                // The same strings are sent and signed, formatted the same on every server.
                var quantity = order.Quantity.ToString(CultureInfo.InvariantCulture);
                var price = FormatPrice(order.Price);
                var discount = order.Discount.ToString(CultureInfo.InvariantCulture);

                formValues.Add($"Product_{lineNumber}_Quantity", quantity);
                formValues.Add($"Product_{lineNumber}_Price", price);
                formValues.Add($"Product_{lineNumber}_Discount", discount);

                sb.Append(quantity);
                sb.Append(price);
                sb.Append(discount);
            }

            sb.Append(rapydSettings.MerchantId);
            sb.Append(payment.PaymentId);
            sb.Append(paymentSettings.SuccessUrl);
            sb.Append(reportUrl);
            sb.Append(paymentSettings.Currency);

            if (rapydSettings.LoanType != LoanType.Disabled)
            {
                formValues.Add("IsCardLoan", "1");
                formValues.Add("MerchantName", rapydSettings.MerchantName);

                if (rapydSettings.LoanType == LoanType.IsLoan)
                {
                    formValues.Add("IsInterestFree", "0");
                    sb.Append(0);
                }
                else if (rapydSettings.LoanType == LoanType.IsInterestFreeLoan)
                {
                    formValues.Add("IsInterestFree", "1");
                    sb.Append(1);
                }
            }

            formValues.Add("DigitalSignature", CryptoHelpers.GetSHA256HexStringSum(sb.ToString()));

            _logger.LogInformation("Rapyd Payment Request - " + PaymentLog.Template + " Amount: {Total}",
                payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId, total);

            var cspNonce = CspHelper.GetCspNonce(_httpCtx, _config);

            return FormHelper.CreateRequest(formValues, rapydSettings.PaymentPageUrl.ToString(), cspNonce: cspNonce);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rapyd Payment Request - Failed. OrderNumber: {OrderNumber} OrderId: {OrderUniqueId}",
                paymentSettings.OrderNumber, paymentSettings.OrderUniqueId);
            throw;
        }
    }

    /// <summary>
    /// The unit price as sent to Rapyd: whole currency units, rounded half away from zero.
    /// The payment page has only ever been sent whole units by this provider, decimals are not known to be accepted.
    /// </summary>
    internal static string FormatPrice(decimal price)
        => Math.Round(price, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);

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
