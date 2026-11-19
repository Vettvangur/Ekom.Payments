using Ekom.Models.Payments;
using Ekom.Payments.Helpers;
using Ekom.Payments.Providers;
using Ekom.Services;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Ekom.Payments.Teya;

/// <summary>
/// Receives a callback from Teya when a customer completes payment.
/// Verifies it and records the capture on the Ekom order; Ekom then completes the checkout.
/// </summary>
// The route keeps the Borgun name: it is the callback url of payments already in flight.
[Route("ekom/payments/BorgunResponse")]
[ApiController]
public class TeyaResponseController : ControllerBase
{
    readonly ILogger _logger;
    readonly PaymentsConfiguration _settings;
    readonly IOrderPayments _payments;
    readonly IOrderService _legacyOrders;
    readonly IMailService _mailSvc;

    /// <summary>
    /// ctor
    /// </summary>
    public TeyaResponseController(
        ILogger<TeyaResponseController> logger,
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
    /// Receives a callback from Teya when a customer completes payment.
    /// </summary>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpGet, HttpPost]
    [Route("")]
    public async Task<IActionResult> Post([FromForm] Response teyaResponse)
    {
        OrderPayment? payment = null;

        try
        {
            if (!Guid.TryParse(teyaResponse.Reference, out var paymentId))
            {
                return BadRequest();
            }

            // Until the payment is loaded its id is all we know.
            _logger.LogInformation("Teya Payment Response - Start. PaymentId: {PaymentId}", paymentId);

            payment = await LegacyPaymentBridge.GetOrImportAsync(_payments, _legacyOrders, paymentId);

            if (payment == null)
            {
                _logger.LogWarning("Teya Payment Response - Unable to find payment. PaymentId: {PaymentId}", paymentId);

                return NotFound();
            }

            if (!PaymentRowGuard.BelongsTo(payment, Payment._ppNodeName))
            {
                _logger.LogWarning("Teya Payment Response - Not a Teya payment. PaymentId: {PaymentId}", paymentId);

                return NotFound();
            }

            using var scope = PaymentLog.BeginScope(_logger, payment);

            var paymentSettings = payment.GetPaymentSettings();
            ArgumentNullException.ThrowIfNull(paymentSettings);

            var teyaSettings = payment.GetProviderData<TeyaSettings>();
            ArgumentNullException.ThrowIfNull(teyaSettings);

            // A callback that cannot be verified says nothing about the payment: it is logged, never recorded.
            if (string.IsNullOrWhiteSpace(teyaSettings.SecretCode)
            || string.IsNullOrWhiteSpace(teyaSettings.MerchantId))
            {
                _logger.LogError(
                    "Teya Payment Response - No Teya secret code or merchant id, unable to verify. " + PaymentLog.Template,
                    payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);

                return BadRequest();
            }

            // Teya returns the order id we sent with the payment request, the hash is only valid for that order
            var teyaOrderId = Payment.GetTeyaOrderId(paymentSettings, payment.PaymentId);
            if (!string.Equals(teyaResponse.OrderId, teyaOrderId, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError(
                    "Teya Payment Response - Order id does not match the payment. " + PaymentLog.Template,
                    payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);

                return BadRequest();
            }

            var orderAmount = Payment.FormatPrice(payment.Amount, teyaSettings);

            var orderhashcheck = CryptoHelpers.GetHMACSHA256(teyaSettings.SecretCode,
                new CheckHashMessage(teyaResponse.OrderId, orderAmount, paymentSettings.Currency).Message);

            if (!PaymentRowGuard.SignatureEquals(teyaResponse.OrderHash, orderhashcheck))
            {
                _logger.LogError(
                    "Teya Payment Response - Failed hash verification. " + PaymentLog.Template,
                    payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);

                return BadRequest();
            }

            // The hash covers the order id, amount and currency, not the status: only a successful status is a capture.
            if (!string.Equals(teyaResponse.Status, "Ok", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Teya Payment Response - Status is not Ok, nothing recorded. " + PaymentLog.Template + " Status: {Status}",
                    payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId, teyaResponse.Status);

                return Ok();
            }

            // Recorded once per payment, however often Teya repeats the callback.
            var result = await _payments.RecordAsync(new PaymentTransaction
            {
                PaymentId = payment.PaymentId,
                Type = PaymentTransactionType.Captured,
                Amount = payment.Amount,
                AuthorizationCode = teyaResponse.AuthorizationCode,
                MaskedCardNumber = teyaResponse.CreditCardNumber,
                IdempotencyKey = LegacyPaymentBridge.CaptureKey(payment.PaymentId),
                Details =
                [
                    new PaymentDetail("orderid", "Teya order id", teyaResponse.OrderId, true),
                    new PaymentDetail("step", "Step", teyaResponse.Step),
                ],
                RawData = JsonConvert.SerializeObject(new
                {
                    teyaResponse.Status,
                    teyaResponse.OrderId,
                    teyaResponse.AuthorizationCode,
                    teyaResponse.CreditCardNumber,
                    teyaResponse.Step,
                    teyaResponse.Reference,
                }),
            });

            _logger.LogInformation(
                "Teya Payment Response - SUCCESS. " + PaymentLog.Template + " FirstDelivery: {Recorded}",
                payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId, result.Recorded);

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Teya Payment Response - Failed. OrderNumber: {OrderNumber} OrderId: {OrderUniqueId} PaymentReference: {PaymentReference}",
                payment?.OrderNumber, payment?.OrderUniqueId, teyaResponse.Reference);

            if (_settings.SendEmailAlerts)
            {
                await _mailSvc.SendAsync(new System.Net.Mail.MailMessage
                {
                    Subject = $"Teya Payment Response - Failed. Order {payment?.OrderNumber ?? "unknown"}, payment {teyaResponse.Reference}",
                    Body = $"<p>Teya Payment Response - Failed<p><br />{HttpContext.Request.GetDisplayUrl()}<br />" + ex.ToString() + "<br><br> " + JsonConvert.SerializeObject(new
                    {
                        teyaResponse.Status, teyaResponse.OrderId, teyaResponse.Step, teyaResponse.Reference,
                    }),
                    IsBodyHtml = true,
                });
            }

            throw;
        }
    }
}
