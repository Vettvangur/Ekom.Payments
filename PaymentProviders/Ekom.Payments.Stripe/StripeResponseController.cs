using Ekom.Models.Payments;
using Ekom.Services;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Text;

namespace Ekom.Payments.Stripe;

/// <summary>
/// Receives Stripe's webhooks. Add <c>/ekom/payments/striperesponse</c> as a webhook endpoint in Stripe
/// and store its signing secret in the provider's settings.
/// </summary>
[Route("ekom/payments/[controller]")]
[ApiController]
public class StripeResponseController : ControllerBase
{
    const string Provider = Payment._ppNodeName;

    readonly ILogger _logger;
    readonly PaymentsConfiguration _settings;
    readonly IOrderPayments _payments;
    readonly IMailService _mailSvc;

    /// <summary>
    /// ctor
    /// </summary>
    public StripeResponseController(
        ILogger<StripeResponseController> logger,
        PaymentsConfiguration settings,
        IOrderPayments payments,
        IMailService mailSvc)
    {
        _logger = logger;
        _settings = settings;
        _payments = payments;
        _mailSvc = mailSvc;
    }

    /// <summary>
    /// Receives a Stripe event about a Checkout Session or a charge (paid, expired, captured, refunded, ...).
    /// Verifies its signature and records the event on the Ekom order; Ekom updates the payment status
    /// and completes the checkout.
    /// </summary>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost]
    [Route("")]
    public async Task<IActionResult> Post()
    {
        // The signature covers the body exactly as Stripe sent it.
        string payload;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
        {
            payload = await reader.ReadToEndAsync();
        }

        if (!StripeEvent.TryParse(payload, out var stripeEvent))
        {
            _logger.LogDebug("Stripe Payment Response - Not a Stripe event");

            return BadRequest();
        }

        // Stripe sends an endpoint whatever it is subscribed to, also for payments made outside Ekom.
        if (!stripeEvent.IsHandled || !stripeEvent.TryGetPaymentId(out var paymentId))
        {
            _logger.LogDebug("Stripe Payment Response - {StripeEvent} {StripeEventId} is not about an Ekom payment",
                stripeEvent.Type, stripeEvent.Id);

            return Ok();
        }

        OrderPayment? payment = null;

        try
        {
            // Until the payment is loaded its id is all we know.
            _logger.LogInformation("Stripe Payment Response - Start. PaymentId: {PaymentId} Event: {StripeEvent} {StripeEventId}",
                paymentId, stripeEvent.Type, stripeEvent.Id);

            payment = await _payments.GetAsync(paymentId);
            if (payment == null)
            {
                _logger.LogWarning("Stripe Payment Response - Unable to find payment. PaymentId: {PaymentId}", paymentId);

                return NotFound();
            }

            if (!payment.BelongsTo(Provider))
            {
                _logger.LogWarning("Stripe Payment Response - Not a Stripe payment. PaymentId: {PaymentId}", paymentId);

                return NotFound();
            }

            using var scope = PaymentLog.BeginScope(_logger, payment);

            var stripeSettings = payment.GetProviderData<StripeSettings>();

            // A webhook that cannot be verified says nothing about the payment: it is logged, never recorded.
            if (stripeSettings == null || string.IsNullOrWhiteSpace(stripeSettings.WebhookSecret))
            {
                _logger.LogError(
                    "Stripe Payment Response - No Stripe webhook secret, unable to verify. " + PaymentLog.Template,
                    payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);

                return StatusCode(500);
            }

            if (!StripeSignature.IsValid(payload, Request.Headers["Stripe-Signature"], stripeSettings.WebhookSecret, DateTimeOffset.UtcNow))
            {
                _logger.LogError(
                    "Stripe Payment Response - Failed signature verification. " + PaymentLog.Template,
                    payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);

                return BadRequest();
            }

            var transaction = ToTransaction(payment, stripeSettings, stripeEvent);

            if (transaction == null)
            {
                _logger.LogInformation(
                    "Stripe Payment Response - {StripeEvent} needs no record. " + PaymentLog.Template,
                    stripeEvent.Type, payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);

                return Ok();
            }

            var result = await _payments.RecordAsync(transaction);

            _logger.LogInformation(
                "Stripe Payment Response - {StripeEvent} recorded as {PaymentEvent}. " + PaymentLog.Template + " FirstDelivery: {Recorded}",
                stripeEvent.Type, transaction.Type, payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId, result.Recorded);

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Stripe Payment Response - Failed. OrderNumber: {OrderNumber} OrderId: {OrderUniqueId} PaymentId: {PaymentId} Event: {StripeEvent} {StripeEventId}",
                payment?.OrderNumber, payment?.OrderUniqueId, paymentId, stripeEvent.Type, stripeEvent.Id);

            if (_settings.SendEmailAlerts)
            {
                await _mailSvc.SendAsync(new System.Net.Mail.MailMessage
                {
                    Subject = $"Stripe Payment Response - Failed. Order {payment?.OrderNumber ?? "unknown"}, payment {paymentId}",
                    Body = $"<p>Stripe Payment Response - Failed<p><br />{HttpContext.Request.GetDisplayUrl()}<br />" + ex.ToString() + "<br><br> " + Sanitized(stripeEvent),
                    IsBodyHtml = true,
                });
            }

            // Stripe delivers the event again after an error.
            throw;
        }
    }

    /// <summary>
    /// Translates a verified Stripe event into what is recorded on the order. Null when nothing is recorded.
    /// </summary>
    internal static PaymentTransaction? ToTransaction(OrderPayment payment, StripeSettings settings, StripeEvent stripeEvent)
    {
        var transaction = new PaymentTransaction
        {
            PaymentId = payment.PaymentId,
            Type = PaymentTransactionType.Captured,
            TransactionId = stripeEvent.PaymentIntent,
            CardBrand = stripeEvent.CardBrand,
            MaskedCardNumber = stripeEvent.CardLast4 == null ? null : "****" + stripeEvent.CardLast4,
            PaymentMethod = stripeEvent.PaymentMethodType,
            OccurredUtc = stripeEvent.CreatedUtc,
            Details =
            [
                new PaymentDetail("event", "Stripe event", stripeEvent.Type),
                new PaymentDetail("paymentIntent", "Payment intent", stripeEvent.PaymentIntent, true),
                stripeEvent.IsCheckoutSession
                    ? new PaymentDetail("checkoutSession", "Checkout session", stripeEvent.ObjectId, true)
                    : new PaymentDetail("charge", "Charge", stripeEvent.ObjectId, true),
                new PaymentDetail("mode", "Stripe mode", stripeEvent.LiveMode ? "Live" : "Test"),
            ],
            RawData = Sanitized(stripeEvent),
        };

        var captured = transaction with { IdempotencyKey = LegacyPaymentBridge.CaptureKey(payment.PaymentId) };

        switch (stripeEvent.Type)
        {
            case StripeEvent.CheckoutCompleted:
                if (stripeEvent.PaymentStatus is "paid" or "no_payment_required")
                    return captured;

                // Unpaid with delayed capture: the amount is reserved and captured later at Stripe.
                // Unpaid otherwise: a payment method that confirms later, Stripe follows up with its outcome.
                return settings.UseDelayedCapture
                    ? transaction with { Type = PaymentTransactionType.Authorized }
                    : null;

            case StripeEvent.CheckoutAsyncPaymentSucceeded:
                return captured;

            case StripeEvent.CheckoutAsyncPaymentFailed:
                return transaction with { Type = PaymentTransactionType.Failed, Message = "The payment did not go through" };

            case StripeEvent.CheckoutExpired:
                return transaction with { Type = PaymentTransactionType.Cancelled, Message = "The Checkout page expired before the customer paid" };

            case StripeEvent.ChargeCaptured:
                return captured with { Amount = ToAmount(stripeEvent.AmountCaptured, stripeEvent, payment) };

            case StripeEvent.ChargeExpired:
                return transaction with { Type = PaymentTransactionType.Voided, Message = "The authorization expired before it was captured" };

            case StripeEvent.ChargeRefunded:
                // Releasing a reserved amount reaches us as a refund of a charge that was never captured.
                if (stripeEvent.Captured == false)
                    return transaction with { Type = PaymentTransactionType.Voided, Message = "The authorization was released" };

                // Stripe reports everything refunded so far, a partial refund is the difference from before.
                var refundedSoFar = stripeEvent.AmountRefunded ?? 0;
                var refunded = refundedSoFar - (stripeEvent.PreviousAmountRefunded ?? 0);

                return refunded > 0 && ToAmount(refunded, stripeEvent, payment) is decimal amount
                    ? transaction with
                    {
                        Type = PaymentTransactionType.Refunded,
                        Amount = amount,
                        IdempotencyKey = $"{payment.PaymentId:N}:Refunded:{refundedSoFar}",
                    }
                    : null;

            default:
                return null;
        }
    }

    /// <summary>
    /// An amount of a charge in the payment's currency. Null, for the payment's own amount,
    /// when Stripe charged in another currency than the payment was requested in.
    /// </summary>
    static decimal? ToAmount(long? minorUnits, StripeEvent stripeEvent, OrderPayment payment)
        => minorUnits is long value
            && stripeEvent.Currency != null
            && string.Equals(stripeEvent.Currency, payment.Currency, StringComparison.OrdinalIgnoreCase)
                ? StripeAmount.FromMinorUnits(value, stripeEvent.Currency)
                : null;

    /// <summary>What is kept of an event: its identifiers, status and amounts, nothing about the customer.</summary>
    static string Sanitized(StripeEvent stripeEvent) => JsonConvert.SerializeObject(new
    {
        stripeEvent.Id,
        stripeEvent.Type,
        Created = stripeEvent.CreatedUtc,
        stripeEvent.LiveMode,
        Object = new
        {
            Id = stripeEvent.ObjectId,
            stripeEvent.PaymentIntent,
            stripeEvent.PaymentStatus,
            stripeEvent.Currency,
            stripeEvent.AmountTotal,
            stripeEvent.Amount,
            stripeEvent.AmountCaptured,
            stripeEvent.AmountRefunded,
            stripeEvent.Captured,
            stripeEvent.Refunded,
            stripeEvent.PaymentMethodType,
            stripeEvent.CardBrand,
            stripeEvent.CardLast4,
        },
    }, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
}
