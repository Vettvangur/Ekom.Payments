using Ekom.Models.Payments;
using Ekom.Payments.Helpers;
using Ekom.Services;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Text;
using System.Text.Json;

namespace Ekom.Payments.PayPal;

/// <summary>
/// Where PayPal sends the customer after they approved a payment, and where PayPal sends its webhooks.
/// The payment is taken here: approving an order at PayPal does not move money yet.
/// </summary>
[Route("ekom/payments/[controller]")]
[ApiController]
public class PayPalResponseController : ControllerBase
{
    const string Provider = Payment._ppNodeName;

    readonly ILogger _logger;
    readonly PaymentsConfiguration _settings;
    readonly IOrderPayments _payments;
    readonly IUmbracoService _uService;
    readonly IHttpClientFactory _httpClientFactory;
    readonly IMailService _mailSvc;

    /// <summary>
    /// ctor
    /// </summary>
    public PayPalResponseController(
        ILogger<PayPalResponseController> logger,
        PaymentsConfiguration settings,
        IOrderPayments payments,
        IUmbracoService uService,
        IHttpClientFactory httpClientFactory,
        IMailService mailSvc)
    {
        _logger = logger;
        _settings = settings;
        _payments = payments;
        _uService = uService;
        _httpClientFactory = httpClientFactory;
        _mailSvc = mailSvc;
    }

    /// <summary>
    /// The customer returns from PayPal. Takes the payment of the order they approved, records it on the
    /// Ekom order and sends them on to the shop's success page, or to its error page when PayPal refused.
    /// </summary>
    /// <param name="paymentId">The Ekom payment, which the provider put in the return address.</param>
    /// <param name="token">The PayPal order, which PayPal adds to the return address.</param>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpGet]
    [Route("")]
    public async Task<IActionResult> Get([FromQuery] Guid paymentId, [FromQuery] string? token)
    {
        if (paymentId == Guid.Empty || !PayPalClient.IsOrderId(token))
        {
            _logger.LogDebug("PayPal Payment Response - Invalid return address");

            return BadRequest();
        }

        OrderPayment? payment = null;

        try
        {
            // Until the payment is loaded its id is all we know.
            _logger.LogInformation("PayPal Payment Response - Start. PaymentId: {PaymentId} PayPalOrder: {PayPalOrder}", paymentId, token);

            payment = await _payments.GetAsync(paymentId);
            if (payment == null || !payment.BelongsTo(Provider))
            {
                _logger.LogWarning("PayPal Payment Response - Unable to find a PayPal payment. PaymentId: {PaymentId}", paymentId);

                return NotFound();
            }

            using var scope = PaymentLog.BeginScope(_logger, payment);

            var paymentSettings = payment.GetPaymentSettings()
                ?? throw new InvalidOperationException($"Payment {paymentId} has no payment settings.");

            PayPalOutcome outcome;
            try
            {
                outcome = await CompleteAsync(payment, ReadSettings(payment), token!);
            }
            catch (HttpRequestException ex)
            {
                // PayPal could not be reached or answered with an error. Nothing is known about the payment,
                // so nothing is recorded; the customer is sent to the error page instead of seeing this fail.
                await ReportAsync(ex, payment, paymentId, "order " + token);

                outcome = PayPalOutcome.NotApproved;
            }

            _logger.LogInformation("PayPal Payment Response - {PayPalOutcome}. " + PaymentLog.Template + " PayPalOrder: {PayPalOrder}",
                outcome, payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId, token);

            if (outcome is PayPalOutcome.Paid or PayPalOutcome.Pending)
            {
                var successUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.SuccessUrl, Request);

                return Redirect(PaymentsUriHelper.AddQueryString(successUrl, "?reference=" + payment.PaymentId).ToString());
            }

            var errorUrl = paymentSettings.ErrorUrl ?? paymentSettings.CancelUrl;

            return errorUrl == null
                ? BadRequest()
                : Redirect(PaymentsUriHelper.EnsureFullUri(errorUrl, Request).ToString());
        }
        catch (Exception ex)
        {
            await ReportAsync(ex, payment, paymentId, "order " + token);

            throw;
        }
    }

    /// <summary>
    /// Receives a PayPal webhook. PayPal is asked whether it sent the event before anything is recorded.
    /// An approved order is completed here as well, for customers who never came back from PayPal.
    /// </summary>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost]
    [Route("webhook")]
    public async Task<IActionResult> Webhook()
    {
        // PayPal verifies the event as it was sent.
        string payload;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
        {
            payload = await reader.ReadToEndAsync();
        }

        if (!PayPalEvent.TryParse(payload, out var payPalEvent))
        {
            _logger.LogDebug("PayPal Webhook - Not a PayPal event");

            return BadRequest();
        }

        // PayPal sends a webhook whatever it is subscribed to, also for payments made outside Ekom.
        if (!payPalEvent.IsHandled || !payPalEvent.TryGetPaymentId(out var paymentId))
        {
            _logger.LogDebug("PayPal Webhook - {PayPalEvent} {PayPalEventId} is not about an Ekom payment",
                payPalEvent.Type, payPalEvent.Id);

            return Ok();
        }

        OrderPayment? payment = null;

        try
        {
            _logger.LogInformation("PayPal Webhook - Start. PaymentId: {PaymentId} Event: {PayPalEvent} {PayPalEventId}",
                paymentId, payPalEvent.Type, payPalEvent.Id);

            payment = await _payments.GetAsync(paymentId);
            if (payment == null || !payment.BelongsTo(Provider))
            {
                _logger.LogWarning("PayPal Webhook - Unable to find a PayPal payment. PaymentId: {PaymentId}", paymentId);

                return NotFound();
            }

            using var scope = PaymentLog.BeginScope(_logger, payment);

            var payPalSettings = ReadSettings(payment);

            // A webhook that cannot be verified says nothing about the payment: it is logged, never recorded.
            if (string.IsNullOrWhiteSpace(payPalSettings.WebhookId))
            {
                _logger.LogError(
                    "PayPal Webhook - No PayPal webhook id, unable to verify. " + PaymentLog.Template,
                    payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);

                return StatusCode(500);
            }

            var client = new PayPalClient(_httpClientFactory, payPalSettings);

            var verified = await client.VerifyWebhookAsync(
                payPalSettings.WebhookId.Trim(),
                Request.Headers["PAYPAL-TRANSMISSION-ID"],
                Request.Headers["PAYPAL-TRANSMISSION-TIME"],
                Request.Headers["PAYPAL-TRANSMISSION-SIG"],
                Request.Headers["PAYPAL-CERT-URL"],
                Request.Headers["PAYPAL-AUTH-ALGO"],
                payload);

            if (!verified)
            {
                _logger.LogError(
                    "PayPal Webhook - Failed verification. " + PaymentLog.Template,
                    payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);

                return BadRequest();
            }

            if (payPalEvent.Type == PayPalEvent.OrderApproved)
            {
                var outcome = payPalEvent.ResourceId == null
                    ? PayPalOutcome.NotApproved
                    : await CompleteAsync(payment, payPalSettings, payPalEvent.ResourceId);

                _logger.LogInformation("PayPal Webhook - Approved order: {PayPalOutcome}. " + PaymentLog.Template,
                    outcome, payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);

                return Ok();
            }

            var transaction = ToTransaction(payment, payPalEvent);

            if (transaction == null)
            {
                _logger.LogInformation(
                    "PayPal Webhook - {PayPalEvent} needs no record. " + PaymentLog.Template,
                    payPalEvent.Type, payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);

                return Ok();
            }

            var result = await _payments.RecordAsync(transaction);

            _logger.LogInformation(
                "PayPal Webhook - {PayPalEvent} recorded as {PaymentEvent}. " + PaymentLog.Template + " FirstDelivery: {Recorded}",
                payPalEvent.Type, transaction.Type, payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId, result.Recorded);

            return Ok();
        }
        catch (Exception ex)
        {
            await ReportAsync(ex, payment, paymentId, $"event {payPalEvent.Type} {payPalEvent.Id}");

            // PayPal delivers the event again after an error.
            throw;
        }
    }

    /// <summary>
    /// Takes the payment of an order the customer approved and records the outcome. Safe to repeat:
    /// PayPal answers a repeated request with the first outcome and the capture is recorded once.
    /// </summary>
    async Task<PayPalOutcome> CompleteAsync(OrderPayment payment, PayPalSettings payPalSettings, string orderId)
    {
        var client = new PayPalClient(_httpClientFactory, payPalSettings);

        var response = await client.GetOrderAsync(orderId);
        if (!response.IsSuccess)
        {
            _logger.LogWarning("PayPal order {PayPalOrder} could not be read: {PayPalError}. " + PaymentLog.Template,
                orderId, response.Error, payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);

            return PayPalOutcome.NotApproved;
        }

        var order = PayPalOrder.From(response.Body);

        // The order id comes from the address the customer returned on. Only the order that was created
        // for this payment is acted on, so an order of another payment is never captured here.
        if (!order.IsFor(payment.PaymentId))
        {
            _logger.LogWarning("PayPal order {PayPalOrder} was not created for this payment. " + PaymentLog.Template,
                orderId, payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);

            return PayPalOutcome.NotApproved;
        }

        if (order.Status == "APPROVED")
        {
            response = await client.CompleteOrderAsync(orderId, payPalSettings.UseDelayedCapture, payment.PaymentId.ToString());

            if (!response.IsSuccess)
            {
                // PayPal refused to take the payment, e.g. the customer's card or balance was declined.
                if ((int)response.Status == 422)
                {
                    await _payments.RecordAsync(new PaymentTransaction
                    {
                        PaymentId = payment.PaymentId,
                        Type = PaymentTransactionType.Failed,
                        Message = response.Error,
                        Details = [new PaymentDetail("payPalOrder", "PayPal order", orderId, true)],
                        IdempotencyKey = $"{payment.PaymentId:N}:Failed:{orderId}:{response.Issue}",
                    });

                    return PayPalOutcome.Failed;
                }

                throw new HttpRequestException($"PayPal did not complete order {orderId}: {response.Error}", null, response.Status);
            }

            order = PayPalOrder.From(response.Body);
        }

        var transaction = order.Capture != null
            ? CaptureTransaction(payment, order.Capture, order.Id, null)
            : order.Authorization != null
                ? AuthorizationTransaction(payment, order.Authorization, order.Id, null)
                : null;

        if (transaction == null)
        {
            // Not approved yet, or a payment PayPal is still reviewing: its webhook tells how that ends.
            return order.Capture?.Status == "PENDING" || order.Authorization?.Status == "PENDING"
                ? PayPalOutcome.Pending
                : PayPalOutcome.NotApproved;
        }

        await _payments.RecordAsync(transaction);

        return transaction.Type == PaymentTransactionType.Failed ? PayPalOutcome.Failed : PayPalOutcome.Paid;
    }

    /// <summary>
    /// Translates a verified PayPal event into what is recorded on the order. Null when nothing is recorded.
    /// </summary>
    internal static PaymentTransaction? ToTransaction(OrderPayment payment, PayPalEvent payPalEvent)
    {
        var resource = payPalEvent.Resource;

        switch (payPalEvent.Type)
        {
            case PayPalEvent.CaptureCompleted:
            case PayPalEvent.CaptureDeclined:
            case PayPalEvent.CaptureDenied:
            case PayPalEvent.CapturePending:
                return CaptureTransaction(payment, resource, null, payPalEvent);

            case PayPalEvent.AuthorizationCreated:
            case PayPalEvent.AuthorizationVoided:
                return AuthorizationTransaction(payment, resource, null, payPalEvent);

            case PayPalEvent.CaptureRefunded:
            case PayPalEvent.CaptureReversed:
                // The resource is the refund, or the reversal PayPal made itself.
                return ToAmount(resource, payment) is decimal refunded and > 0
                    ? Transaction(payment, resource, null, payPalEvent) with
                    {
                        Type = PaymentTransactionType.Refunded,
                        Amount = refunded,
                        Message = payPalEvent.Type == PayPalEvent.CaptureReversed ? "PayPal reversed the payment" : null,
                        IdempotencyKey = $"{payment.PaymentId:N}:Refunded:{resource.Id}",
                    }
                    : null;

            default:
                return null;
        }
    }

    /// <summary>What a capture means for the order. Null while PayPal has not decided.</summary>
    internal static PaymentTransaction? CaptureTransaction(
        OrderPayment payment, PayPalPayment capture, string? orderId, PayPalEvent? payPalEvent)
    {
        var transaction = Transaction(payment, capture, orderId, payPalEvent);

        return capture.Status switch
        {
            "COMPLETED" => transaction with
            {
                Amount = ToAmount(capture, payment),
                IdempotencyKey = LegacyPaymentBridge.CaptureKey(payment.PaymentId),
            },
            "DECLINED" or "DENIED" or "FAILED" => transaction with
            {
                Type = PaymentTransactionType.Failed,
                Message = capture.Reason ?? "PayPal declined the payment",
            },
            _ => null,
        };
    }

    /// <summary>What an authorization means for the order. Null while PayPal has not decided.</summary>
    internal static PaymentTransaction? AuthorizationTransaction(
        OrderPayment payment, PayPalPayment authorization, string? orderId, PayPalEvent? payPalEvent)
    {
        var transaction = Transaction(payment, authorization, orderId, payPalEvent);

        return authorization.Status switch
        {
            "CREATED" => transaction with { Type = PaymentTransactionType.Authorized, Amount = ToAmount(authorization, payment) },
            "DENIED" => transaction with
            {
                Type = PaymentTransactionType.Failed,
                Message = authorization.Reason ?? "PayPal denied the authorization",
            },
            "VOIDED" => transaction with { Type = PaymentTransactionType.Voided, Message = "The authorization was voided or expired" },
            _ => null,
        };
    }

    static PaymentTransaction Transaction(OrderPayment payment, PayPalPayment payPalPayment, string? orderId, PayPalEvent? payPalEvent)
        => new()
        {
            PaymentId = payment.PaymentId,
            Type = PaymentTransactionType.Captured,
            TransactionId = payPalPayment.Id,
            PaymentMethod = "PayPal",
            OccurredUtc = payPalEvent?.CreatedUtc,
            Details =
            [
                new PaymentDetail("event", "PayPal event", payPalEvent?.Type ?? "Customer returned from PayPal"),
                new PaymentDetail("payPalTransaction", "PayPal transaction", payPalPayment.Id, true),
                new PaymentDetail("payPalOrder", "PayPal order", orderId, true),
                new PaymentDetail("status", "PayPal status", payPalPayment.Status),
            ],
            RawData = JsonConvert.SerializeObject(new
            {
                Event = payPalEvent == null ? null : new { payPalEvent.Id, payPalEvent.Type, Created = payPalEvent.CreatedUtc },
                Order = orderId,
                payPalPayment.Id,
                payPalPayment.Status,
                payPalPayment.Reason,
                payPalPayment.Amount,
                payPalPayment.Currency,
            }, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }),
        };

    /// <summary>
    /// An amount PayPal reports, when it is in the payment's currency. Null, for the payment's own amount, otherwise.
    /// </summary>
    static decimal? ToAmount(PayPalPayment payPalPayment, OrderPayment payment)
        => string.Equals(payPalPayment.Currency, payment.Currency, StringComparison.OrdinalIgnoreCase)
            ? payPalPayment.Amount
            : null;

    /// <summary>
    /// The settings stored with the payment, with the client secret read from the payment provider node
    /// or configuration again: it is not stored with the payment.
    /// </summary>
    PayPalSettings ReadSettings(OrderPayment payment)
    {
        var paymentSettings = payment.GetPaymentSettings()
            ?? throw new InvalidOperationException($"Payment {payment.PaymentId} has no payment settings.");
        var payPalSettings = payment.GetProviderData<PayPalSettings>() ?? new PayPalSettings();

        _uService.PopulatePaymentProviderProperties(
            paymentSettings,
            Provider,
            payPalSettings,
            PayPalSettings.Properties);

        return payPalSettings;
    }

    async Task ReportAsync(Exception ex, OrderPayment? payment, Guid paymentId, string subject)
    {
        _logger.LogError(ex,
            "PayPal Payment Response - Failed. OrderNumber: {OrderNumber} OrderId: {OrderUniqueId} PaymentId: {PaymentId} PayPal: {PayPalSubject}",
            payment?.OrderNumber, payment?.OrderUniqueId, paymentId, subject);

        if (_settings.SendEmailAlerts)
        {
            await _mailSvc.SendAsync(new System.Net.Mail.MailMessage
            {
                Subject = $"PayPal Payment Response - Failed. Order {payment?.OrderNumber ?? "unknown"}, payment {paymentId}",
                Body = $"<p>PayPal Payment Response - Failed<p><br />{HttpContext.Request.GetDisplayUrl()}<br />" + ex.ToString() + "<br><br> " + System.Net.WebUtility.HtmlEncode(subject),
                IsBodyHtml = true,
            });
        }
    }
}

/// <summary>How completing an approved PayPal order ended.</summary>
enum PayPalOutcome
{
    /// <summary>The payment was captured or authorized.</summary>
    Paid,

    /// <summary>PayPal is reviewing the payment and reports the outcome by webhook.</summary>
    Pending,

    /// <summary>PayPal refused the payment.</summary>
    Failed,

    /// <summary>The customer has not approved the order, or it is not this payment's order.</summary>
    NotApproved,
}

/// <summary>
/// A PayPal webhook event.
/// See https://developer.paypal.com/api/rest/webhooks/event-names/
/// </summary>
sealed class PayPalEvent
{
    public const string OrderApproved = "CHECKOUT.ORDER.APPROVED";
    public const string CaptureCompleted = "PAYMENT.CAPTURE.COMPLETED";
    public const string CaptureDeclined = "PAYMENT.CAPTURE.DECLINED";
    public const string CaptureDenied = "PAYMENT.CAPTURE.DENIED";
    public const string CapturePending = "PAYMENT.CAPTURE.PENDING";
    public const string CaptureRefunded = "PAYMENT.CAPTURE.REFUNDED";
    public const string CaptureReversed = "PAYMENT.CAPTURE.REVERSED";
    public const string AuthorizationCreated = "PAYMENT.AUTHORIZATION.CREATED";
    public const string AuthorizationVoided = "PAYMENT.AUTHORIZATION.VOIDED";

    /// <summary>The events that say something about an Ekom payment. Every other event is ignored.</summary>
    static readonly HashSet<string> _handled = new(StringComparer.Ordinal)
    {
        OrderApproved, CaptureCompleted, CaptureDeclined, CaptureDenied, CapturePending, CaptureRefunded, CaptureReversed,
        AuthorizationCreated, AuthorizationVoided,
    };

    string? _orderCustomId;

    public string? Id { get; private init; }
    public string Type { get; private init; } = string.Empty;
    public DateTime? CreatedUtc { get; private init; }

    /// <summary>The id of the order, capture, authorization or refund the event is about.</summary>
    public string? ResourceId => Resource.Id;

    /// <summary>The capture, authorization or refund the event is about. For an order only its id is read.</summary>
    public PayPalPayment Resource { get; private init; } = PayPalPayment.From(default);

    public bool IsHandled => _handled.Contains(Type);

    /// <summary>
    /// The Ekom payment the event is about: the custom id PayPal copies from the order to its payments.
    /// </summary>
    public bool TryGetPaymentId(out Guid paymentId)
        => Guid.TryParse(Resource.CustomId ?? _orderCustomId, out paymentId);

    /// <summary>Reads a webhook body. False when it is not a PayPal event.</summary>
    public static bool TryParse(string? payload, out PayPalEvent payPalEvent)
    {
        payPalEvent = new PayPalEvent();

        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            var type = PayPalJson.String(root, "event_type");
            var resource = PayPalJson.Child(root, "resource");

            if (string.IsNullOrEmpty(type) || resource.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            payPalEvent = new PayPalEvent
            {
                Id = PayPalJson.String(root, "id"),
                Type = type,
                CreatedUtc = DateTimeOffset.TryParse(
                    PayPalJson.String(root, "create_time"),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var created)
                        ? created.UtcDateTime
                        : null,
                Resource = PayPalPayment.From(resource),
                _orderCustomId = PayPalOrder.From(resource).CustomId,
            };

            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
