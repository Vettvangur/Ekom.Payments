using Ekom.Payments;
using Ekom.Payments.Helpers;
using Ekom.Payments.Netgiro;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web;

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

        _logger.LogInformation("Netgiro Payment Request - Start");

        var netgiroSettings = paymentSettings.CustomSettings.ContainsKey(typeof(NetgiroSettings))
            ? (paymentSettings.CustomSettings[typeof(NetgiroSettings)] as NetgiroSettings)!
            : new NetgiroSettings();

        _uService.PopulatePaymentProviderProperties(
            paymentSettings,
            _ppNodeName,
            netgiroSettings,
            NetgiroSettings.Properties);

        var total = paymentSettings.Orders.Sum(x => x.GrandTotal);

        // Persist in database and retrieve unique order id
        var orderStatus = await _orderService.InsertAsync(
            total,
            paymentSettings,
            netgiroSettings,
            null,
            _httpCtx
        ).ConfigureAwait(false);

        // This example shows how to construct a success url / receipt page destination
        // adding the reference querystring allows for easy retrieval of order using the order helper on the success/receipt page
        paymentSettings.SuccessUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.SuccessUrl, _httpCtx.Request);
        paymentSettings.SuccessUrl = PaymentsUriHelper.AddQueryString(paymentSettings.SuccessUrl, "?reference=" + orderStatus.UniqueId);

        paymentSettings.CancelUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.CancelUrl, _httpCtx.Request);
        paymentSettings.CancelUrl = PaymentsUriHelper.AddQueryString(paymentSettings.CancelUrl, "?reference=" + orderStatus.UniqueId);

        var reportUrl = PaymentsUriHelper.EnsureFullUri(new Uri(reportPath, UriKind.Relative), _httpCtx.Request);

#pragma warning disable CA1305 // Specify IFormatProvider
        var totalAmount = Math.Ceiling(total).ToString("F0");
#pragma warning restore CA1305 // Specify IFormatProvider
        // Begin populating form values to be submitted
        var formValues = new Dictionary<string, string?>
            {
                { "ApplicationID", netgiroSettings.ApplicationId.ToString() },
                { "ConfirmationType", ((int)ConfirmationType.ServerCallback).ToString() },

                { "PaymentSuccessfulURL", paymentSettings.SuccessUrl.ToString() },
                { "PaymentCancelledURL", paymentSettings.CancelUrl.ToString() },
                { "PaymentConfirmedURL", reportUrl.ToString() },

                { "TotalAmount",  totalAmount},
            };

        if (netgiroSettings.iFrame.HasValue)
        {
            formValues.Add("iframe", netgiroSettings.iFrame.Value.ToString());
        }

        NumberFormatInfo currencyFormat = new CultureInfo("is-IS", false).NumberFormat;

        for (int lineNumber = 0, length = paymentSettings.Orders.Count(); lineNumber < length; lineNumber++)
        {
            var order = paymentSettings.Orders.ElementAt(lineNumber);

            formValues.Add($"Items[{lineNumber}].Name", order.Title);
            formValues.Add($"Items[{lineNumber}].Quantity", order.Quantity.ToString("F0"));
            formValues.Add($"Items[{lineNumber}].UnitPrice", order.Price.ToString("F0", currencyFormat));
            formValues.Add($"Items[{lineNumber}].Amount", order.GrandTotal.ToString("F0", currencyFormat));
        }

        // Netgiro only supports specific types of order id's
        var borgunOrderId = orderStatus.UniqueId.ToString().Split('-').Last();

        var sig = CryptoHelpers.GetSHA256HexStringSum(
            CombineSignature(
                netgiroSettings.Secret,
                orderStatus.UniqueId.ToString(),
                totalAmount,
                netgiroSettings.ApplicationId.ToString()));
        formValues.Add("Signature", sig);
        formValues.Add("ReferenceNumber", orderStatus.UniqueId.ToString());

        _logger.LogInformation(
            "Netgiro Payment Request - Amount: {Total} OrderId: {OrderUniqueId}",
            total,
            orderStatus.UniqueId);

        return FormHelper.CreateRequest(formValues, netgiroSettings.PaymentPageUrl.ToString());
    }

    internal static string CombineSignature(params string[] args)
    {
        var sb = new StringBuilder();
        Array.ForEach(args, x => sb.Append(x));
        return sb.ToString();
    }
}
