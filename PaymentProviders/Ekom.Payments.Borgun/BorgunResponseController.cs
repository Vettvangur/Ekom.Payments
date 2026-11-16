using Ekom.Models.Payments;
using Ekom.Payments.Helpers;
using Ekom.Payments.Providers;
using Ekom.Services;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Ekom.Payments.Borgun;

/// <summary>
/// Receives a callback from Borgun when a customer completes payment.
/// Verifies it and records the capture on the Ekom order; Ekom then completes the checkout.
/// </summary>
[Route("ekom/payments/[controller]")]
[ApiController]
public class BorgunResponseController : ControllerBase
{
    readonly ILogger _logger;
    readonly PaymentsConfiguration _settings;
    readonly IOrderPayments _payments;
    readonly IOrderService _legacyOrders;
    readonly IMailService _mailSvc;

    /// <summary>
    /// ctor
    /// </summary>
    public BorgunResponseController(
        ILogger<BorgunResponseController> logger,
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
    /// Receives a callback from Borgun when a customer completes payment.
    /// </summary>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpGet, HttpPost]
    [Route("")]
    public async Task<IActionResult> Post([FromForm] Response borgunResponse)
    {
        OrderPayment? payment = null;

        try
        {
            if (!Guid.TryParse(borgunResponse.Reference, out var paymentId))
            {
                return BadRequest();
            }

            // Until the payment is loaded its id is all we know.
            _logger.LogInformation("Borgun Payment Response - Start. PaymentId: {PaymentId}", paymentId);

            payment = await LegacyPaymentBridge.GetOrImportAsync(_payments, _legacyOrders, paymentId);

            if (payment == null)
            {
                _logger.LogWarning("Borgun Payment Response - Unable to find payment. PaymentId: {PaymentId}", paymentId);

                return NotFound();
            }

            if (!PaymentRowGuard.BelongsTo(payment, Payment._ppNodeName))
            {
                _logger.LogWarning("Borgun Payment Response - Not a Borgun payment. PaymentId: {PaymentId}", paymentId);

                return NotFound();
            }

            using var scope = PaymentLog.BeginScope(_logger, payment);

            var paymentSettings = payment.GetPaymentSettings();
            ArgumentNullException.ThrowIfNull(paymentSettings);

            var borgunSettings = payment.GetProviderData<BorgunSettings>();
            ArgumentNullException.ThrowIfNull(borgunSettings);

            // A callback that cannot be verified says nothing about the payment: it is logged, never recorded.
            if (string.IsNullOrWhiteSpace(borgunSettings.SecretCode)
            || string.IsNullOrWhiteSpace(borgunSettings.MerchantId))
            {
                _logger.LogError(
                    "Borgun Payment Response - No Borgun secret code or merchant id, unable to verify. " + PaymentLog.Template,
                    payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);

                return BadRequest();
            }

            // Borgun returns the order id we sent with the payment request, the hash is only valid for that order
            var borgunOrderId = Payment.GetBorgunOrderId(paymentSettings, payment.PaymentId);
            if (!string.Equals(borgunResponse.OrderId, borgunOrderId, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError(
                    "Borgun Payment Response - Order id does not match the payment. " + PaymentLog.Template,
                    payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);

                return BadRequest();
            }

            var orderAmount = Payment.FormatPrice(payment.Amount, borgunSettings);

            var orderhashcheck = CryptoHelpers.GetHMACSHA256(borgunSettings.SecretCode,
                new CheckHashMessage(borgunResponse.OrderId, orderAmount, paymentSettings.Currency).Message);

            if (!PaymentRowGuard.SignatureEquals(borgunResponse.OrderHash, orderhashcheck))
            {
                _logger.LogError(
                    "Borgun Payment Response - Failed hash verification. " + PaymentLog.Template,
                    payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);

                return BadRequest();
            }

            // Recorded once per payment, however often Borgun repeats the callback.
            var result = await _payments.RecordAsync(new PaymentTransaction
            {
                PaymentId = payment.PaymentId,
                Type = PaymentTransactionType.Captured,
                Amount = payment.Amount,
                AuthorizationCode = borgunResponse.AuthorizationCode,
                MaskedCardNumber = borgunResponse.CreditCardNumber,
                IdempotencyKey = LegacyPaymentBridge.CaptureKey(payment.PaymentId),
                Details =
                [
                    new PaymentDetail("orderid", "Borgun order id", borgunResponse.OrderId, true),
                    new PaymentDetail("step", "Step", borgunResponse.Step),
                ],
                RawData = JsonConvert.SerializeObject(new
                {
                    borgunResponse.Status,
                    borgunResponse.OrderId,
                    borgunResponse.AuthorizationCode,
                    borgunResponse.CreditCardNumber,
                    borgunResponse.Step,
                    borgunResponse.Reference,
                }),
            });

            _logger.LogInformation(
                "Borgun Payment Response - SUCCESS. " + PaymentLog.Template + " FirstDelivery: {Recorded}",
                payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId, result.Recorded);

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Borgun Payment Response - Failed. OrderNumber: {OrderNumber} OrderId: {OrderUniqueId} PaymentReference: {PaymentReference}",
                payment?.OrderNumber, payment?.OrderUniqueId, borgunResponse.Reference);

            if (_settings.SendEmailAlerts)
            {
                await _mailSvc.SendAsync(new System.Net.Mail.MailMessage
                {
                    Subject = $"Borgun Payment Response - Failed. Order {payment?.OrderNumber ?? "unknown"}, payment {borgunResponse.Reference}",
                    Body = $"<p>Borgun Payment Response - Failed<p><br />{HttpContext.Request.GetDisplayUrl()}<br />" + ex.ToString() + "<br><br> " + JsonConvert.SerializeObject(new
                    {
                        borgunResponse.Status, borgunResponse.OrderId, borgunResponse.Step, borgunResponse.Reference,
                    }),
                    IsBodyHtml = true,
                });
            }

            throw;
        }
    }
}
