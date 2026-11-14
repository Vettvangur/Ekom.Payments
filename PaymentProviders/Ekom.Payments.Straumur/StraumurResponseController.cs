using Ekom.Models.Payments;
using Ekom.Payments.Helpers;
using Ekom.Payments.Providers;
using Ekom.Services;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Globalization;

namespace Ekom.Payments.Straumur;

/// <summary>
/// Receives a callback from Straumur when customer completes payment.
/// Changes order status and optionally runs a custom callback provided by the application consuming this library.
/// </summary>
[Route("ekom/payments/[controller]")]
[ApiController]
public class StraumurResponseController : ControllerBase
{
    readonly ILogger _logger;
    readonly PaymentsConfiguration _settings;
    readonly IOrderPayments _payments;
    readonly IOrderService _legacyOrders;
    readonly IMailService _mailSvc;

    /// <summary>
    /// ctor
    /// </summary>
    public StraumurResponseController(
        ILogger<StraumurResponseController> logger,
        PaymentsConfiguration settings,
        IOrderPayments payments,
        IOrderService legacyOrders,
        IMailService mailSvc)
    {
        _logger = logger;
        _settings = settings;
        _payments = payments;
        _legacyOrders = legacyOrders;
        _mailSvc = mailSvc;
    }

    /// <summary>
    /// Receives Straumur's webhook for a payment event (authorization, capture, refund, ...).
    /// Verifies it and records the event on the Ekom order; Ekom updates the payment status and completes the checkout.
    /// </summary>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpGet, HttpPost]
    [Route("")]
    public async Task<IActionResult> Post([FromBody]Response straumurResp)
    {
        if (straumurResp == null || !ModelState.IsValid)
        {
            _logger.LogDebug("Straumur Payment Response - Invalid model state");

            return BadRequest();
        }

        OrderPayment? payment = null;

        try
        {
            if (!TryGetPaymentId(straumurResp.MerchantReference, out var paymentId))
            {
                _logger.LogWarning("Straumur Payment Response - MerchantReference has no payment id");

                return BadRequest();
            }

            // Until the payment is loaded its id is all we know.
            _logger.LogInformation("Straumur Payment Response - Start. PaymentId: {PaymentId}", paymentId);

            payment = await LegacyPaymentBridge.GetOrImportAsync(_payments, _legacyOrders, paymentId);
            if (payment == null)
            {
                _logger.LogWarning("Straumur Payment Response - Unable to find payment. PaymentId: {PaymentId}", paymentId);

                return NotFound();
            }

            if (!PaymentRowGuard.BelongsTo(payment, Payment._ppNodeName))
            {
                _logger.LogWarning("Straumur Payment Response - Not a Straumur payment. PaymentId: {PaymentId}", paymentId);

                return NotFound();
            }

            using var scope = PaymentLog.BeginScope(_logger, payment);

            var straumurSettings = payment.GetProviderData<StraumurSettings>();

            // A webhook that cannot be verified says nothing about the payment: it is logged, never recorded.
            if (straumurSettings == null || string.IsNullOrWhiteSpace(straumurSettings.HmacKey))
            {
                _logger.LogError(
                    "Straumur Payment Response - No Straumur HMAC key, unable to verify. " + PaymentLog.Template,
                    payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);

                return StatusCode(500);
            }

            var calculatedSignature = StraumurResponseHelper.GetHmacSignature(straumurSettings.HmacKey, straumurResp);

            if (!PaymentRowGuard.SignatureEquals(straumurResp.HmacSignature, calculatedSignature, ignoreCase: false))
            {
                _logger.LogError(
                    "Straumur Payment Response - Failed signature verification. " + PaymentLog.Template,
                    payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);

                return StatusCode(500);
            }

            var eventType = straumurResp.AdditionalData?.EventType ?? PaymentEvent.Authorization;
            var transaction = ToTransaction(payment, straumurSettings, straumurResp, eventType);

            if (transaction == null)
            {
                _logger.LogInformation(
                    "Straumur Payment Response - {StraumurEvent} needs no record. " + PaymentLog.Template,
                    eventType, payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);

                return Ok();
            }

            var result = await _payments.RecordAsync(transaction);

            _logger.LogInformation(
                "Straumur Payment Response - {StraumurEvent} recorded as {PaymentEvent}. " + PaymentLog.Template + " FirstDelivery: {Recorded}",
                eventType, transaction.Type, payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId, result.Recorded);

            return transaction.Type == PaymentTransactionType.Failed ? Ok("Payment not Valid") : Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Straumur Payment Response - Failed. OrderNumber: {OrderNumber} OrderId: {OrderUniqueId} PaymentReference: {PaymentReference}",
                payment?.OrderNumber, payment?.OrderUniqueId, straumurResp.MerchantReference);

            if (_settings.SendEmailAlerts)
            {
                await _mailSvc.SendAsync(new System.Net.Mail.MailMessage
                {
                    Subject = $"Straumur Payment Response - Failed. Order {payment?.OrderNumber ?? "unknown"}, payment {straumurResp.MerchantReference}",
                    Body = $"<p>Straumur Payment Response - Failed<p><br />{HttpContext.Request.GetDisplayUrl()}<br />" + ex.ToString() + "<br><br> " + Sanitized(straumurResp),
                    IsBodyHtml = true,
                });
            }

            throw;
        }
    }

    /// <summary>
    /// Translates a verified Straumur event into what is recorded on the order. Null when nothing is recorded.
    /// </summary>
    internal static PaymentTransaction? ToTransaction(
        OrderPayment payment, StraumurSettings settings, Response response, PaymentEvent eventType)
    {
        var details = response.AdditionalData;
        var transaction = new PaymentTransaction
        {
            PaymentId = payment.PaymentId,
            Type = PaymentTransactionType.Captured,
            Amount = payment.Amount,
            TransactionId = response.PayfacReference,
            AuthorizationCode = details?.AuthCode,
            MaskedCardNumber = details?.CardNumber ?? details?.CardSummary,
            PaymentMethod = details?.PaymentMethod,
            Details =
            [
                new PaymentDetail("event", "Straumur event", eventType.ToString()),
                new PaymentDetail("checkoutReference", "Checkout reference", response.CheckoutReference, true),
                new PaymentDetail("threeDAuthenticated", "3-D Secure", details?.ThreeDAuthenticated),
                new PaymentDetail("cardUsage", "Card usage", details?.CardUsage),
            ],
            RawData = Sanitized(response),
        };

        // Straumur declined or could not complete the payment. This is a verified answer from the gateway.
        if (string.Equals(response.Success, "false", StringComparison.OrdinalIgnoreCase))
        {
            return eventType is PaymentEvent.Authorization or PaymentEvent.Capture
                ? transaction with { Type = PaymentTransactionType.Failed, Message = response.Reason ?? "Payment was not successful" }
                : null;
        }

        var recurring = settings.RecurringProcessingModel is RecurringProccessingModel.Subscription or RecurringProccessingModel.CardOnFile;

        switch (eventType)
        {
            case PaymentEvent.Authorization:
                // Recurring payments are confirmed by the event that follows the authorization.
                if (recurring) return null;

                // With delayed capture the money is only reserved now and captured later.
                if (settings.UseDelayedCapture)
                    return transaction with { Type = PaymentTransactionType.Authorized };

                // Otherwise Straumur captures straight away: the authorization is the payment, as before.
                return transaction with { IdempotencyKey = LegacyPaymentBridge.CaptureKey(payment.PaymentId) };

            case PaymentEvent.Capture:
            case PaymentEvent.Tokenization:
                return transaction with { IdempotencyKey = LegacyPaymentBridge.CaptureKey(payment.PaymentId) };

            case PaymentEvent.Refund:
                // Straumur sends amounts in minor units.
                return decimal.TryParse(response.Amount, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minorUnits) && minorUnits > 0
                    ? transaction with { Type = PaymentTransactionType.Refunded, Amount = minorUnits / 100m, Message = response.Reason }
                    : null;

            default:
                return null;
        }
    }

    /// <summary>The merchant reference is the payment id, optionally prefixed with "item;item;".</summary>
    static bool TryGetPaymentId(string? merchantReference, out Guid paymentId)
    {
        paymentId = Guid.Empty;
        if (merchantReference == null) return false;

        return Guid.TryParse(merchantReference, out paymentId)
            || Guid.TryParse(merchantReference.Split(';').LastOrDefault(), out paymentId);
    }

    /// <summary>The webhook without its signature and card token.</summary>
    static string Sanitized(Response response) => JsonConvert.SerializeObject(new
    {
        response.CheckoutReference,
        response.PayfacReference,
        response.MerchantReference,
        response.Amount,
        response.Currency,
        response.Reason,
        response.Success,
        AdditionalData = response.AdditionalData == null ? null : new
        {
            EventType = response.AdditionalData.EventType.ToString(),
            response.AdditionalData.CardUsage,
            response.AdditionalData.CardNumber,
            response.AdditionalData.ThreeDAuthenticated,
            response.AdditionalData.OriginalPayfacReference,
            response.AdditionalData.PaymentMethod,
            response.AdditionalData.CardSummary,
            response.AdditionalData.AuthCode,
            response.AdditionalData.CardExpiryDate,
        },
    });
}
