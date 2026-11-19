using Ekom.Models.Payments;
using Ekom.Payments.Helpers;
using Ekom.Services;
using Ekom.Payments.Providers;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Ekom.Payments.Rapyd;

/// <summary>
/// Receives a callback from Rapyd when customer completes payment.
/// Changes order status and optionally runs a custom callback provided by the application consuming this library.
/// </summary>
// The route keeps the Valitor name: it is the callback url of payments already in flight.
[Route("ekom/payments/ValitorResponse")]
[ApiController]
public class RapydResponseController : ControllerBase
{
    readonly ILogger _logger;
    readonly PaymentsConfiguration _settings;
    readonly IOrderPayments _payments;
    readonly IOrderService _legacyOrders;
    readonly IMailService _mailSvc;

    /// <summary>
    /// ctor
    /// </summary>
    public RapydResponseController(
        ILogger<RapydResponseController> logger,
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
    /// Receives a callback from Rapyd when a customer completes payment.
    /// Verifies it and records the capture on the Ekom order; Ekom then completes the checkout.
    /// </summary>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpGet, HttpPost]
    [Route("")]
    public async Task<IActionResult> Post([FromQuery] Response rapydResp)
    {
        if (rapydResp == null || !ModelState.IsValid)
        {
            _logger.LogDebug("Rapyd Payment Response - Invalid model state");

            return BadRequest();
        }

        OrderPayment? payment = null;

        try
        {
            if (!Guid.TryParse(rapydResp.ReferenceNumber, out var paymentId))
            {
                return BadRequest();
            }

            // Until the payment is loaded its id is all we know.
            _logger.LogInformation("Rapyd Payment Response - Start. PaymentId: {PaymentId}", paymentId);

            payment = await LegacyPaymentBridge.GetOrImportAsync(_payments, _legacyOrders, paymentId);
            if (payment == null)
            {
                _logger.LogWarning("Rapyd Payment Response - Unable to find payment. PaymentId: {PaymentId}", paymentId);

                return NotFound();
            }

            if (!PaymentRowGuard.BelongsTo(payment, Payment._ppNodeName))
            {
                _logger.LogWarning("Rapyd Payment Response - Not a Rapyd payment. PaymentId: {PaymentId}", paymentId);

                return NotFound();
            }

            using var scope = PaymentLog.BeginScope(_logger, payment);

            var rapydSettings = payment.GetProviderData<RapydSettings>();

            // A callback that cannot be verified says nothing about the payment: it is logged, never recorded.
            if (rapydSettings == null
            || string.IsNullOrWhiteSpace(rapydSettings.VerificationCode)
            || string.IsNullOrWhiteSpace(rapydSettings.MerchantId))
            {
                _logger.LogError(
                    "Rapyd Payment Response - No Rapyd verification code or merchant id, unable to verify. " + PaymentLog.Template,
                    payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);

                return StatusCode(500);
            }

            string digitalSignature = CryptoHelpers.GetSHA256HexStringSum(rapydSettings.VerificationCode + rapydResp.ReferenceNumber);

            if (!PaymentRowGuard.SignatureEquals(rapydResp.DigitalSignatureResponse, digitalSignature))
            {
                _logger.LogError(
                    "Rapyd Payment Response - Failed signature verification. " + PaymentLog.Template,
                    payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);

                return StatusCode(500);
            }

            // Recorded once per payment, however often Rapyd repeats the callback.
            var result = await _payments.RecordAsync(new PaymentTransaction
            {
                PaymentId = payment.PaymentId,
                Type = PaymentTransactionType.Captured,
                Amount = payment.Amount,
                TransactionId = rapydResp.TransactionNumber == 0
                    ? null
                    : rapydResp.TransactionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                AuthorizationCode = rapydResp.AuthorizationNumber,
                MaskedCardNumber = rapydResp.CardNumberMasked,
                CardBrand = rapydResp.CardType,
                IdempotencyKey = LegacyPaymentBridge.CaptureKey(payment.PaymentId),
                Details =
                [
                    new PaymentDetail("contractNumber", "Contract number",
                        rapydResp.ContractNumber == 0 ? null : rapydResp.ContractNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), true),
                    new PaymentDetail("contractType", "Contract type", rapydResp.ContractType),
                    new PaymentDetail("date", "Rapyd date", rapydResp.Date),
                ],
                RawData = JsonConvert.SerializeObject(new
                {
                    rapydResp.ReferenceNumber,
                    rapydResp.CardType,
                    rapydResp.CardNumberMasked,
                    rapydResp.AuthorizationNumber,
                    rapydResp.TransactionNumber,
                    rapydResp.ContractNumber,
                    rapydResp.ContractType,
                    rapydResp.Date,
                }),
            });

            _logger.LogInformation(
                "Rapyd Payment Response - SUCCESS. " + PaymentLog.Template + " FirstDelivery: {Recorded}",
                payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId, result.Recorded);

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Rapyd Payment Response - Failed. OrderNumber: {OrderNumber} OrderId: {OrderUniqueId} PaymentReference: {PaymentReference}",
                payment?.OrderNumber, payment?.OrderUniqueId, rapydResp.ReferenceNumber);

            if (_settings.SendEmailAlerts)
            {
                await _mailSvc.SendAsync(new System.Net.Mail.MailMessage
                {
                    Subject = $"Rapyd Payment Response - Failed. Order {payment?.OrderNumber ?? "unknown"}, payment {rapydResp.ReferenceNumber}",
                    Body = $"<p>Rapyd Payment Response - Failed<p><br />{HttpContext.Request.GetDisplayUrl()}<br />" + ex.ToString() + "<br><br> " + JsonConvert.SerializeObject(new
                    {
                        rapydResp.ReferenceNumber, rapydResp.TransactionNumber, rapydResp.CardType,
                    }),
                    IsBodyHtml = true,
                });
            }

            throw;
        }
    }
}
