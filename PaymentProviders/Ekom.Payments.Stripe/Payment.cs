using Ekom.Models.Payments;
using Ekom.Payments.Helpers;
using Ekom.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Ekom.Payments.Stripe;

/// <summary>
/// Initiate a payment request with Stripe Checkout
/// </summary>
public class Payment : IPaymentProvider
{
    internal const string _ppNodeName = "stripe";

    /// <summary>The metadata key that carries the Ekom payment id on Stripe's objects.</summary>
    internal const string PaymentIdMetadataKey = "ekomPaymentId";

    static readonly Uri _checkoutSessionsUrl = new("https://api.stripe.com/v1/checkout/sessions");

    // https://docs.stripe.com/api/checkout/sessions/create#create_checkout_session-locale
    static readonly HashSet<string> _checkoutLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "bg", "cs", "da", "de", "el", "en", "es", "et", "fi", "fil", "fr", "hr", "hu", "id", "it", "ja", "ko",
        "lt", "lv", "ms", "mt", "nb", "nl", "pl", "pt", "ro", "ru", "sk", "sl", "sv", "th", "tr", "vi", "zh",
    };

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
    /// Initiate a payment request with Stripe: creates a Checkout Session and returns the markup
    /// that sends the customer to it.
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
            _logger.LogInformation("Stripe Payment Request - Start. OrderNumber: {OrderNumber} OrderId: {OrderUniqueId}",
                paymentSettings.OrderNumber, paymentSettings.OrderUniqueId);

            var stripeSettings = (paymentSettings.CustomSettings.TryGetValue(typeof(StripeSettings), out var custom)
                ? custom as StripeSettings
                : null) ?? new StripeSettings();

            _uService.PopulatePaymentProviderProperties(
                paymentSettings,
                _ppNodeName,
                stripeSettings,
                StripeSettings.Properties);

            ArgumentException.ThrowIfNullOrEmpty(stripeSettings.SecretKey);
            ArgumentException.ThrowIfNullOrEmpty(paymentSettings.Currency);

            // The payment could never be confirmed: fail before the customer is sent to pay.
            if (string.IsNullOrWhiteSpace(stripeSettings.WebhookSecret))
            {
                throw new ArgumentException(
                    "A Stripe webhook signing secret is required to confirm payments.",
                    nameof(stripeSettings.WebhookSecret));
            }
            if (stripeSettings.CheckoutExpiresInMinutes is < 30 or > 24 * 60)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(stripeSettings.CheckoutExpiresInMinutes),
                    "Stripe keeps a Checkout page open for 30 minutes to 24 hours.");
            }

            var total = paymentSettings.Orders.Sum(x => x.GrandTotal);

            // Store the payment on the Ekom order. Stripe echoes the payment id back in its webhooks.
            var payment = await _payments.CreateAsync(
                paymentSettings,
                _ppNodeName,
                total,
                stripeSettings,
                httpContext: _httpCtx
            ).ConfigureAwait(false);

            var successUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.SuccessUrl, _httpCtx.Request);
            successUrl = PaymentsUriHelper.AddQueryString(successUrl, "?reference=" + payment.PaymentId);

            var cancelUrl = paymentSettings.CancelUrl == null
                ? null
                : PaymentsUriHelper.EnsureFullUri(paymentSettings.CancelUrl, _httpCtx.Request);

            var form = BuildCheckoutSession(
                paymentSettings, stripeSettings, payment, total, successUrl, cancelUrl, DateTimeOffset.UtcNow);

            _logger.LogInformation("Stripe Payment Request - " + PaymentLog.Template + " Amount: {Total}",
                payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId, total);

            using var request = new HttpRequestMessage(HttpMethod.Post, _checkoutSessionsUrl)
            {
                Content = new FormUrlEncodedContent(form),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", stripeSettings.SecretKey.Trim());
            // Stripe answers a repeated request for the same payment with the session it already created.
            request.Headers.Add("Idempotency-Key", payment.PaymentId.ToString());

            var httpClient = _httpClientFactory.CreateClient();

            using var responseMessage = await httpClient.SendAsync(request).ConfigureAwait(false);

            var responseContent = await responseMessage.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!responseMessage.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "Stripe Payment Request returned {StatusCode}. " + PaymentLog.Template + " Response: {ResponseBody}",
                    (int)responseMessage.StatusCode,
                    payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId,
                    responseContent);
            }

            responseMessage.EnsureSuccessStatusCode();

            using var session = JsonDocument.Parse(responseContent);

            var checkoutUrl = session.RootElement.TryGetProperty("url", out var url) ? url.GetString() : null;
            if (string.IsNullOrEmpty(checkoutUrl))
            {
                throw new InvalidOperationException("Stripe returned a Checkout Session without a url.");
            }

            _logger.LogInformation("Stripe Payment Request - Checkout Session {CheckoutSession} created. " + PaymentLog.Template,
                session.RootElement.TryGetProperty("id", out var sessionId) ? sessionId.GetString() : null,
                payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);

            return Redirect(checkoutUrl, CspHelper.GetCspNonce(_httpCtx, _config));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stripe Payment Request - Failed. OrderNumber: {OrderNumber} OrderId: {OrderUniqueId}",
                paymentSettings.OrderNumber, paymentSettings.OrderUniqueId);
            throw;
        }
    }

    /// <summary>
    /// The parameters of the Checkout Session that takes the payment.
    /// See https://docs.stripe.com/api/checkout/sessions/create
    /// </summary>
    internal static List<KeyValuePair<string, string>> BuildCheckoutSession(
        PaymentSettings paymentSettings,
        StripeSettings stripeSettings,
        OrderPayment payment,
        decimal total,
        Uri successUrl,
        Uri? cancelUrl,
        DateTimeOffset now)
    {
        var currency = paymentSettings.Currency.Trim();
        var paymentId = payment.PaymentId.ToString();
        var form = new List<KeyValuePair<string, string>>();

        void Add(string key, string? value)
        {
            if (!string.IsNullOrEmpty(value)) form.Add(new KeyValuePair<string, string>(key, value));
        }

        Add("mode", "payment");
        Add("client_reference_id", paymentId);
        Add("success_url", successUrl.ToString());
        Add("cancel_url", cancelUrl?.ToString());

        // The session carries the payment id for its own events, the payment intent for the charge's events.
        Add($"metadata[{PaymentIdMetadataKey}]", paymentId);
        Add("metadata[ekomOrderNumber]", paymentSettings.OrderNumber);
        Add($"payment_intent_data[metadata][{PaymentIdMetadataKey}]", paymentId);
        Add("payment_intent_data[metadata][ekomOrderNumber]", paymentSettings.OrderNumber);
        Add("payment_intent_data[description]", paymentSettings.OrderNumber);

        if (stripeSettings.UseDelayedCapture)
        {
            Add("payment_intent_data[capture_method]", "manual");
        }

        Add("customer_email", paymentSettings.CustomerInfo?.Email);
        Add("locale", ParseSupportedLanguages(stripeSettings.Locale, paymentSettings.Language));

        if (stripeSettings.CheckoutExpiresInMinutes is int minutes)
        {
            Add("expires_at", now.AddMinutes(minutes).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        }

        var methods = (stripeSettings.PaymentMethodTypes ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < methods.Length; i++)
        {
            Add($"payment_method_types[{i}]", methods[i]);
        }

        var lines = GetLines(paymentSettings, total, currency);
        for (var i = 0; i < lines.Count; i++)
        {
            Add($"line_items[{i}][quantity]", "1");
            Add($"line_items[{i}][price_data][currency]", currency.ToLowerInvariant());
            Add($"line_items[{i}][price_data][unit_amount]", lines[i].Amount.ToString(CultureInfo.InvariantCulture));
            Add($"line_items[{i}][price_data][product_data][name]", lines[i].Name);
        }

        return form;
    }

    /// <summary>
    /// What the customer sees on the Checkout page. Stripe charges the sum of the lines, so the order lines
    /// are only listed when they add up to the amount of the payment; otherwise the order is one line.
    /// </summary>
    static List<(string Name, long Amount)> GetLines(PaymentSettings paymentSettings, decimal total, string currency)
    {
        const int maxLines = 100;
        const int maxNameLength = 250;

        var totalAmount = StripeAmount.ToMinorUnits(total, currency);

        var lines = paymentSettings.Orders
            .Select(x => (
                Name: x.Quantity == 1 || string.IsNullOrWhiteSpace(x.Title)
                    ? x.Title ?? string.Empty
                    : string.Create(CultureInfo.InvariantCulture, $"{x.Title} × {x.Quantity:0.###}"),
                Amount: StripeAmount.ToMinorUnits(x.GrandTotal, currency)))
            .Select(x => (Name: x.Name.Length > maxNameLength ? x.Name[..maxNameLength] : x.Name, x.Amount))
            .ToList();

        if (lines.Count is 0 or > maxLines
            || lines.Any(x => x.Amount < 0 || string.IsNullOrWhiteSpace(x.Name))
            || lines.Sum(x => x.Amount) != totalAmount)
        {
            var name = string.IsNullOrWhiteSpace(paymentSettings.OrderName)
                ? paymentSettings.OrderNumber
                : paymentSettings.OrderName;

            lines = [(name, totalAmount)];
        }

        return lines;
    }

    /// <summary>
    /// The Checkout locale: the configured one, otherwise the payment's language when Stripe has it.
    /// Null lets Stripe follow the customer's browser.
    /// </summary>
    static string? ParseSupportedLanguages(string? locale, string language)
    {
        if (!string.IsNullOrWhiteSpace(locale))
        {
            return locale.Trim();
        }

        try
        {
            var parsed = CultureInfo.GetCultureInfo(language).TwoLetterISOLanguageName;

            return _checkoutLanguages.Contains(parsed) ? parsed.ToLowerInvariant() : null;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Markup that sends the customer to the Checkout page. The address is written as a script string,
    /// since Stripe's addresses carry a fragment that has to arrive unchanged.
    /// </summary>
    internal static string Redirect(string url, string? cspNonce)
    {
        var nonceAttribute = string.IsNullOrWhiteSpace(cspNonce)
            ? string.Empty
            : $" nonce=\"{WebUtility.HtmlEncode(cspNonce)}\"";

        return $"<script{nonceAttribute}>(function(){{ window.location.href = {JsonSerializer.Serialize(url)}; }}())</script>"
            + $"<noscript><a href=\"{WebUtility.HtmlEncode(url)}\">Continue to payment</a></noscript>";
    }
}
