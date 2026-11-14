using Ekom.Payments.Helpers;
using Ekom.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text;

namespace Ekom.Payments.Netgiro;

/// <summary>
/// Initiate a payment request with Netgiro
/// </summary>
class Payment : IPaymentProvider
{
    internal const string _ppNodeName = "netgiro";
    /// <summary>
    /// Ekom.Payments ResponseController
    /// </summary>
    const string reportPath = "/ekom/payments/NetgiroResponse";


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
    /// Initiate a payment request with Netgiro.
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

        try
        {
            _logger.LogInformation("Netgiro Payment Request - Start. OrderNumber: {OrderNumber} OrderId: {OrderUniqueId}",
                paymentSettings.OrderNumber, paymentSettings.OrderUniqueId);

            var netgiroSettings = paymentSettings.CustomSettings.ContainsKey(typeof(NetgiroSettings))
                ? (paymentSettings.CustomSettings[typeof(NetgiroSettings)] as NetgiroSettings)!
                : new NetgiroSettings();

            _uService.PopulatePaymentProviderProperties(
                paymentSettings,
                _ppNodeName,
                netgiroSettings,
                NetgiroSettings.Properties);

            var total = paymentSettings.Orders.Sum(x => x.GrandTotal);

            // Store the payment on the Ekom order. Netgiro echoes the payment id back as its reference number.
            var payment = await _payments.CreateAsync(
                paymentSettings,
                _ppNodeName,
                total,
                netgiroSettings,
                httpContext: _httpCtx
            ).ConfigureAwait(false);

            // This example shows how to construct a success url / receipt page destination
            // adding the reference querystring allows for easy retrieval of order using the order helper on the success/receipt page
            paymentSettings.SuccessUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.SuccessUrl, _httpCtx.Request);
            paymentSettings.SuccessUrl = PaymentsUriHelper.AddQueryString(paymentSettings.SuccessUrl, "?reference=" + payment.PaymentId);

            paymentSettings.CancelUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.CancelUrl, _httpCtx.Request);
            paymentSettings.CancelUrl = PaymentsUriHelper.AddQueryString(paymentSettings.CancelUrl, "?reference=" + payment.PaymentId);

            var reportUrl = PaymentsUriHelper.EnsureFullUri(new Uri(reportPath, UriKind.Relative), _httpCtx.Request);

            // Begin populating form values to be submitted
            var formValues = new Dictionary<string, string?>
            {
                { "ApplicationID", netgiroSettings.ApplicationId.ToString() },
                { "ConfirmationType", ((int)ConfirmationType.ServerCallback).ToString() },
                { "PaymentSuccessfulURL", paymentSettings.SuccessUrl.ToString() },
                { "PaymentCancelledURL", paymentSettings.CancelUrl.ToString() },
                { "PaymentConfirmedURL", reportUrl.ToString() },
                { "TotalAmount",  FormatPrice(total)},
            };
            
            for (int lineNumber = 0, length = paymentSettings.Orders.Count(); lineNumber < length; lineNumber++)
            {
                var order = paymentSettings.Orders.ElementAt(lineNumber);

                formValues.Add($"Items[{lineNumber}].Name", order.Title);
                formValues.Add($"Items[{lineNumber}].Quantity", order.Quantity.ToString("F0"));
                formValues.Add($"Items[{lineNumber}].UnitPrice", FormatPrice(order.Price));
                formValues.Add($"Items[{lineNumber}].Amount", FormatPrice(order.GrandTotal));
            }

            var sig = RequestSignature(netgiroSettings, payment.PaymentId, total);
            formValues.Add("Signature", sig);
            formValues.Add("ReferenceNumber", payment.PaymentId.ToString());

            _logger.LogInformation("Netgiro Payment Request - " + PaymentLog.Template + " Amount: {Total}",
                payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId, total);

            var cspNonce = CspHelper.GetCspNonce(_httpCtx, _config);

            return FormHelper.CreateRequest(formValues, netgiroSettings.PaymentPageUrl.ToString(), cspNonce: cspNonce);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Netgiro Payment Request - Failed. OrderNumber: {OrderNumber} OrderId: {OrderUniqueId}",
                paymentSettings.OrderNumber, paymentSettings.OrderUniqueId);
            throw;
        }
    }

    internal static string CombineSignature(params string[] args)
    {
        var sb = new StringBuilder();
        Array.ForEach(args, x => sb.Append(x));
        return sb.ToString();
    }

    /// <summary>
    /// Signature of the payment request, SHA256(SecretKey + ReferenceNumber + TotalAmount + ApplicationId)
    /// </summary>
    internal static string RequestSignature(NetgiroSettings netgiroSettings, Guid referenceNumber, decimal total)
        => CryptoHelpers.GetSHA256HexStringSum(
            CombineSignature(
                netgiroSettings.Secret,
                referenceNumber.ToString(),
                FormatPrice(total),
                netgiroSettings.ApplicationId.ToString()));

    internal static string FormatPrice(decimal price)
    {
        var rounded = Math.Round(price, 0, MidpointRounding.AwayFromZero);
        return rounded.ToString("0", CultureInfo.InvariantCulture);
    }
}
