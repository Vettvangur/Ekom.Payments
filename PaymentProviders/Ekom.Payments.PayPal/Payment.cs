using Ekom.Models.Payments;
using Ekom.Payments.Helpers;
using Ekom.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ekom.Payments.PayPal;

/// <summary>
/// Initiate a payment request with PayPal
/// </summary>
public class Payment : IPaymentProvider
{
    internal const string _ppNodeName = "payPal";

    /// <summary>
    /// Ekom.Payments ResponseController: where PayPal sends the customer after approving the payment.
    /// </summary>
    internal const string ReturnPath = "/ekom/payments/paypalresponse";

    readonly ILogger<Payment> _logger;
    readonly IUmbracoService _uService;
    readonly IOrderPayments _payments;
    readonly HttpContext _httpCtx;
    readonly IHttpClientFactory _httpClientFactory;
    readonly IConfiguration _config;

    /// <summary>
    /// ctor for Unit Tests
    /// </summary>
    public Payment(
        ILogger<Payment> logger,
        IUmbracoService uService,
        IOrderPayments payments,
        IHttpContextAccessor httpContext,
        IHttpClientFactory httpClientFactory,
        IConfiguration config)
    {
        _logger = logger;
        _uService = uService;
        _payments = payments;
        _httpCtx = httpContext.HttpContext ?? throw new NotSupportedException("Payment requests require an httpcontext");
        _httpClientFactory = httpClientFactory;
        _config = config;
    }

    /// <summary>
    /// Initiate a payment request with PayPal: creates a PayPal order and returns the markup
    /// that sends the customer to PayPal to approve it.
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
            _logger.LogInformation("PayPal Payment Request - Start. OrderNumber: {OrderNumber} OrderId: {OrderUniqueId}",
                paymentSettings.OrderNumber, paymentSettings.OrderUniqueId);

            var payPalSettings = (paymentSettings.CustomSettings.TryGetValue(typeof(PayPalSettings), out var custom)
                ? custom as PayPalSettings
                : null) ?? new PayPalSettings();

            _uService.PopulatePaymentProviderProperties(
                paymentSettings,
                _ppNodeName,
                payPalSettings,
                PayPalSettings.Properties);

            ArgumentException.ThrowIfNullOrEmpty(paymentSettings.Currency);
            ArgumentNullException.ThrowIfNull(paymentSettings.SuccessUrl);

            var client = new PayPalClient(_httpClientFactory, payPalSettings);

            var currency = paymentSettings.Currency.Trim().ToUpperInvariant();
            var total = PayPalAmount.Round(paymentSettings.Orders.Sum(x => x.GrandTotal), currency);

            // Store the payment on the Ekom order. PayPal keeps the payment id on its order and payments.
            var payment = await _payments.CreateAsync(
                paymentSettings,
                _ppNodeName,
                total,
                payPalSettings,
                httpContext: _httpCtx
            ).ConfigureAwait(false);

            // PayPal sends the customer to the response controller, which takes the payment and sends them on.
            var returnUrl = PaymentsUriHelper.EnsureFullUri(new Uri(ReturnPath, UriKind.Relative), _httpCtx.Request);
            returnUrl = PaymentsUriHelper.AddQueryString(returnUrl, "?paymentId=" + payment.PaymentId);

            var cancelUrl = PaymentsUriHelper.EnsureFullUri(
                paymentSettings.CancelUrl ?? paymentSettings.ErrorUrl ?? paymentSettings.SuccessUrl,
                _httpCtx.Request);

            var order = BuildOrder(paymentSettings, payPalSettings, payment, total, currency, returnUrl, cancelUrl);

            _logger.LogInformation("PayPal Payment Request - " + PaymentLog.Template + " Amount: {Total}",
                payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId, total);

            var response = await client.CreateOrderAsync(order, payment.PaymentId.ToString()).ConfigureAwait(false);

            if (!response.IsSuccess)
            {
                _logger.LogError(
                    "PayPal Payment Request returned {StatusCode}. " + PaymentLog.Template + " Error: {PayPalError}",
                    (int)response.Status, payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId, response.Error);

                throw new HttpRequestException($"PayPal did not create the order: {response.Error}", null, response.Status);
            }

            var payPalOrder = PayPalOrder.From(response.Body);
            if (string.IsNullOrEmpty(payPalOrder.ApproveUrl))
            {
                throw new InvalidOperationException("PayPal returned an order without a link for the customer to approve it.");
            }

            _logger.LogInformation("PayPal Payment Request - PayPal order {PayPalOrder} created. " + PaymentLog.Template,
                payPalOrder.Id, payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);

            return Redirect(payPalOrder.ApproveUrl, CspHelper.GetCspNonce(_httpCtx, _config));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PayPal Payment Request - Failed. OrderNumber: {OrderNumber} OrderId: {OrderUniqueId}",
                paymentSettings.OrderNumber, paymentSettings.OrderUniqueId);
            throw;
        }
    }

    /// <summary>
    /// The PayPal order that takes the payment.
    /// See https://developer.paypal.com/docs/api/orders/v2/#orders_create
    /// </summary>
    internal static string BuildOrder(
        PaymentSettings paymentSettings,
        PayPalSettings payPalSettings,
        OrderPayment payment,
        decimal total,
        string currency,
        Uri returnUrl,
        Uri cancelUrl)
    {
        const int maxDescriptionLength = 127;

        var description = string.IsNullOrWhiteSpace(paymentSettings.OrderName)
            ? paymentSettings.OrderNumber
            : paymentSettings.OrderName;

        var unit = new JsonObject
        {
            // PayPal copies the custom id to the order's captures, authorizations and refunds.
            ["custom_id"] = payment.PaymentId.ToString(),
            ["amount"] = new JsonObject
            {
                ["currency_code"] = currency,
                ["value"] = PayPalAmount.Format(total, currency),
            },
        };
        if (!string.IsNullOrWhiteSpace(description))
        {
            unit["description"] = description.Length > maxDescriptionLength ? description[..maxDescriptionLength] : description;
        }

        var experience = new JsonObject
        {
            ["return_url"] = returnUrl.ToString(),
            ["cancel_url"] = cancelUrl.ToString(),
            // The customer confirms the payment at PayPal, there is no review step back in the shop.
            ["user_action"] = "PAY_NOW",
            // Ekom has the delivery address. PayPal does not ask for another one.
            ["shipping_preference"] = "NO_SHIPPING",
        };
        if (!string.IsNullOrWhiteSpace(payPalSettings.BrandName))
        {
            experience["brand_name"] = payPalSettings.BrandName.Trim();
        }
        if (!string.IsNullOrWhiteSpace(payPalSettings.Locale))
        {
            experience["locale"] = payPalSettings.Locale.Trim();
        }

        var order = new JsonObject
        {
            ["intent"] = payPalSettings.UseDelayedCapture ? "AUTHORIZE" : "CAPTURE",
            ["purchase_units"] = new JsonArray(unit),
            ["payment_source"] = new JsonObject
            {
                ["paypal"] = new JsonObject { ["experience_context"] = experience },
            },
        };

        return order.ToJsonString();
    }

    /// <summary>Markup that sends the customer to PayPal.</summary>
    internal static string Redirect(string url, string? cspNonce)
    {
        var nonceAttribute = string.IsNullOrWhiteSpace(cspNonce)
            ? string.Empty
            : $" nonce=\"{WebUtility.HtmlEncode(cspNonce)}\"";

        return $"<script{nonceAttribute}>(function(){{ window.location.href = {JsonSerializer.Serialize(url)}; }}())</script>"
            + $"<noscript><a href=\"{WebUtility.HtmlEncode(url)}\">Continue to PayPal</a></noscript>";
    }
}
