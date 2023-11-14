using Ekom.Payments.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace Ekom.Payments.Borgun;

/// <summary>
/// Initiate a payment request with Borgun
/// </summary>
class Payment : IPaymentProvider
{
    internal const string _ppNodeName = "borgun";
    /// <summary>
    /// Ekom.Payments ResponseController
    /// </summary>
    const string reportPath = "/ekom/payments/BorgunResponse";

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
    /// Initiate a payment request with Borgun.
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
            _logger.LogInformation("Borgun Payment Request - Start");

            var borgunSettings = paymentSettings.CustomSettings.ContainsKey(typeof(BorgunSettings))
                ? (paymentSettings.CustomSettings[typeof(BorgunSettings)] as BorgunSettings)!
                : new BorgunSettings();

            _uService.PopulatePaymentProviderProperties(
                paymentSettings,
                _ppNodeName,
                borgunSettings,
                BorgunSettings.Properties);

            var total = paymentSettings.Orders.Sum(x => x.GrandTotal);

            paymentSettings.SuccessUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.SuccessUrl, _httpCtx.Request);
            paymentSettings.CancelUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.CancelUrl, _httpCtx.Request);
            paymentSettings.ErrorUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.ErrorUrl, _httpCtx.Request);

            var reportUrl = PaymentsUriHelper.EnsureFullUri(new Uri(reportPath, UriKind.Relative), _httpCtx.Request);
            var currencyFormat = new CultureInfo(paymentSettings.Currency, false).NumberFormat;


            // Begin populating form values to be submitted
            var formValues = new Dictionary<string, string>
            {
                { "merchantid", borgunSettings.MerchantId },
                { "paymentgatewayid", borgunSettings.PaymentGatewayId.ToString() },
                { "returnurlsuccess", paymentSettings.SuccessUrl.ToString() },
                { "returnurlcancel", paymentSettings.CancelUrl.ToString() + "?paymentError=cancelPayment" },
                { "returnurlerror", paymentSettings.ErrorUrl.ToString() + "?paymentError=errorPayment" },
                { "returnurlsuccessserver", reportUrl.ToString() },

                { "amount", total.ToString() + ",00" },
                { "currency", paymentSettings.Currency },
                { "language", paymentSettings.Language.ToUpper() == "IS-IS" ? "IS" : paymentSettings.Language.ToUpper() }
            };

            for (int lineNumber = 0, length = paymentSettings.Orders.Count(); lineNumber < length; lineNumber++)
            {
                var order = paymentSettings.Orders.ElementAt(lineNumber);

                formValues.Add("itemdescription_" + lineNumber, order.Title);
                formValues.Add("itemcount_" + lineNumber, order.Quantity.ToString());
                formValues.Add("itemunitamount_" + lineNumber, order.Price.ToString() + ",00");
                formValues.Add("itemamount_" + lineNumber, order.GrandTotal.ToString() + ",00");
            }

            //if (borgunSettings.SkipReceipt)
            //{
                formValues.Add("skipreceiptpage", "1");
            //}

            if (!string.IsNullOrEmpty(borgunSettings.MerchantEmail))
            {
                formValues.Add("merchantemail", borgunSettings.MerchantEmail);
            }

            if (borgunSettings.MerchantLogo != null)
            {
                formValues.Add("merchantlogo", borgunSettings.MerchantLogo.ToString());
            }

            // Borgun portal page type.
            // If set as 1 then cardholder is required to insert email, 
            // mobile number and home address. 
            // Merchant email parameter must be set since cardholder information is returned through email to merchant.

            // By default Customer is not asked for email, mobile number and home address

            // If == 1 Customer is presented with inputs for email, mobile number and home address
            if (borgunSettings.RequireCustomerInformation
            && !string.IsNullOrEmpty(borgunSettings.MerchantEmail))
            {
                formValues.Add("pagetype", "1");
            }

            // Persist in database and retrieve unique order id
            var orderStatus = await _orderService.InsertAsync(
                total,
                paymentSettings,
                borgunSettings,
                null,
                _httpCtx
            ).ConfigureAwait(false);

            // Borgun only supports specific types of order id's
            var borgunOrderId = orderStatus.UniqueId.ToString().Split('-').Last();

            //CheckHash
            var checkHash = CryptoHelpers.GetHMACSHA256(borgunSettings.SecretCode,
                new CheckHashMessage(
                    borgunSettings.MerchantId,
                    paymentSettings.SuccessUrl.ToString(),
                    reportUrl.ToString(),
                    borgunOrderId,
                    total.ToString() + ",00",
                    paymentSettings.Currency
                ).Message
            );

            formValues.Add("checkhash", checkHash);
            formValues.Add("reference", orderStatus.UniqueId.ToString());
            formValues.Add("orderid", borgunOrderId);

            _logger.LogInformation("Borgun Payment Request - Amount: {Total} OrderId: {OrderUniqueId}", total, orderStatus.UniqueId);

            return FormHelper.CreateRequest(formValues, borgunSettings.PaymentPageUrl.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Borgun Payment Request - Payment Request Failed");
            Events.OnError(this, new ErrorEventArgs
            {
                Exception = ex,
            });
            throw;
        }
    }
}
