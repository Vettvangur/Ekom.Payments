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

namespace Ekom.Payments.Valitor;

/// <summary>
/// Receives a callback from Valitor when customer completes payment.
/// Changes order status and optionally runs a custom callback provided by the application consuming this library.
/// </summary>
[Route("ekom/payments/[controller]")]
[ApiController]
public class ValitorResponseController : ControllerBase
{
    readonly ILogger _logger;
    readonly PaymentsConfiguration _settings;
    readonly IOrderPayments _payments;
    readonly IOrderService _legacyOrders;
    readonly IMailService _mailSvc;

    /// <summary>
    /// ctor
    /// </summary>
    public ValitorResponseController(
        ILogger<ValitorResponseController> logger,
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
    /// Receives a callback from Valitor when a customer completes payment.
    /// Verifies it and records the capture on the Ekom order; Ekom then completes the checkout.
    /// </summary>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpGet, HttpPost]
    [Route("")]
    public async Task<IActionResult> Post([FromQuery] Response valitorResp)
    {
        if (valitorResp == null || !ModelState.IsValid)
        {
            _logger.LogDebug("Valitor Payment Response - Invalid model state");

            return BadRequest();
        }

        OrderPayment? payment = null;

        try
        {
            if (!Guid.TryParse(valitorResp.ReferenceNumber, out var paymentId))
            {
                return BadRequest();
            }

            // Until the payment is loaded its id is all we know.
            _logger.LogInformation("Valitor Payment Response - Start. PaymentId: {PaymentId}", paymentId);

            payment = await LegacyPaymentBridge.GetOrImportAsync(_payments, _legacyOrders, paymentId);
            if (payment == null)
            {
                _logger.LogWarning("Valitor Payment Response - Unable to find payment. PaymentId: {PaymentId}", paymentId);

                return NotFound();
            }

            if (!PaymentRowGuard.BelongsTo(payment, Payment._ppNodeName))
            {
                _logger.LogWarning("Valitor Payment Response - Not a Valitor payment. PaymentId: {PaymentId}", paymentId);

                return NotFound();
            }

            using var scope = PaymentLog.BeginScope(_logger, payment);

            var valitorSettings = payment.GetProviderData<ValitorSettings>();

            // A callback that cannot be verified says nothing about the payment: it is logged, never recorded.
            if (valitorSettings == null
            || string.IsNullOrWhiteSpace(valitorSettings.VerificationCode)
            || string.IsNullOrWhiteSpace(valitorSettings.MerchantId))
            {
                _logger.LogError(
                    "Valitor Payment Response - No Valitor verification code or merchant id, unable to verify. " + PaymentLog.Template,
                    payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);

                return StatusCode(500);
            }

            string digitalSignature = CryptoHelpers.GetSHA256HexStringSum(valitorSettings.VerificationCode + valitorResp.ReferenceNumber);

            if (!PaymentRowGuard.SignatureEquals(valitorResp.DigitalSignatureResponse, digitalSignature))
            {
                _logger.LogError(
                    "Valitor Payment Response - Failed signature verification. " + PaymentLog.Template,
                    payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);

                return StatusCode(500);
            }

            // Recorded once per payment, however often Valitor repeats the callback.
            var result = await _payments.RecordAsync(new PaymentTransaction
            {
                PaymentId = payment.PaymentId,
                Type = PaymentTransactionType.Captured,
                Amount = payment.Amount,
                TransactionId = valitorResp.TransactionNumber == 0
                    ? null
                    : valitorResp.TransactionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                AuthorizationCode = valitorResp.AuthorizationNumber,
                MaskedCardNumber = valitorResp.CardNumberMasked,
                CardBrand = valitorResp.CardType,
                IdempotencyKey = LegacyPaymentBridge.CaptureKey(payment.PaymentId),
                Details =
                [
                    new PaymentDetail("contractNumber", "Contract number",
                        valitorResp.ContractNumber == 0 ? null : valitorResp.ContractNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), true),
                    new PaymentDetail("contractType", "Contract type", valitorResp.ContractType),
                    new PaymentDetail("date", "Valitor date", valitorResp.Date),
                ],
                RawData = JsonConvert.SerializeObject(new
                {
                    valitorResp.ReferenceNumber,
                    valitorResp.CardType,
                    valitorResp.CardNumberMasked,
                    valitorResp.AuthorizationNumber,
                    valitorResp.TransactionNumber,
                    valitorResp.ContractNumber,
                    valitorResp.ContractType,
                    valitorResp.Date,
                }),
            });

            _logger.LogInformation(
                "Valitor Payment Response - SUCCESS. " + PaymentLog.Template + " FirstDelivery: {Recorded}",
                payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId, result.Recorded);

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Valitor Payment Response - Failed. OrderNumber: {OrderNumber} OrderId: {OrderUniqueId} PaymentReference: {PaymentReference}",
                payment?.OrderNumber, payment?.OrderUniqueId, valitorResp.ReferenceNumber);

            if (_settings.SendEmailAlerts)
            {
                await _mailSvc.SendAsync(new System.Net.Mail.MailMessage
                {
                    Subject = $"Valitor Payment Response - Failed. Order {payment?.OrderNumber ?? "unknown"}, payment {valitorResp.ReferenceNumber}",
                    Body = $"<p>Valitor Payment Response - Failed<p><br />{HttpContext.Request.GetDisplayUrl()}<br />" + ex.ToString() + "<br><br> " + JsonConvert.SerializeObject(new
                    {
                        valitorResp.ReferenceNumber, valitorResp.TransactionNumber, valitorResp.CardType,
                    }),
                    IsBodyHtml = true,
                });
            }

            throw;
        }
    }
}
