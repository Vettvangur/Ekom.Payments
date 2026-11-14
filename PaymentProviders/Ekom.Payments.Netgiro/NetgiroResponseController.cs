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
using System.Globalization;

namespace Ekom.Payments.Netgiro;

/// <summary>
/// Receives a callback from Netgiroer completes payment.
/// Changes order status and optionally runs a custom callback provided by the application consuming this library.
/// </summary>
[Route("ekom/payments/[controller]")]
[ApiController]
public class NetgiroResponseController : ControllerBase
{
    readonly ILogger _logger;
    readonly PaymentsConfiguration _settings;
    readonly IOrderPayments _payments;
    readonly IOrderService _legacyOrders;
    readonly IMailService _mailSvc;

    /// <summary>
    /// ctor
    /// </summary>
    public NetgiroResponseController(
        ILogger<NetgiroResponseController> logger,
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
    /// Receives a callback from Netgiro when a customer completes payment.
    /// Verifies it and records the capture on the Ekom order; Ekom then completes the checkout.
    /// </summary>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpGet, HttpPost]
    [Route("")]
    public async Task<IActionResult> Post([ModelBinder(BinderType = typeof(NetgiroResponseModelBinder))] Response netgiroResponse)
    {
        OrderPayment? payment = null;

        try
        {
            if (netgiroResponse.ReferenceNumber == Guid.Empty)
            {
                return BadRequest();
            }

            var paymentId = netgiroResponse.ReferenceNumber;

            // Until the payment is loaded its id is all we know.
            _logger.LogInformation("Netgiro Payment Response - Start. PaymentId: {PaymentId}", paymentId);

            payment = await LegacyPaymentBridge.GetOrImportAsync(_payments, _legacyOrders, paymentId);
            if (payment == null)
            {
                _logger.LogWarning("Netgiro Payment Response - Unable to find payment. PaymentId: {PaymentId}", paymentId);

                return NotFound();
            }

            if (!PaymentRowGuard.BelongsTo(payment, Payment._ppNodeName))
            {
                _logger.LogWarning("Netgiro Payment Response - Not a Netgiro payment. PaymentId: {PaymentId}", paymentId);

                return NotFound();
            }

            using var scope = PaymentLog.BeginScope(_logger, payment);

            var netgiroSettings = payment.GetProviderData<NetgiroSettings>();

            // A callback that cannot be verified says nothing about the payment: it is logged, never recorded.
            if (netgiroSettings == null
            || string.IsNullOrWhiteSpace(netgiroSettings.Secret)
            || netgiroSettings.ApplicationId == Guid.Empty)
            {
                _logger.LogError(
                    "Netgiro Payment Response - No Netgiro secret or application id, unable to verify. " + PaymentLog.Template,
                    payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);

                return BadRequest();
            }

            if (!IsVerified(netgiroResponse, payment.PaymentId, payment.Amount, netgiroSettings, out var rejection))
            {
                _logger.LogError(
                    "Netgiro Payment Response - Failed verification: {Reason}. " + PaymentLog.Template,
                    rejection, payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);

                return BadRequest();
            }

            if (string.IsNullOrEmpty(netgiroResponse.NetgiroSignature))
            {
                _logger.LogWarning(
                    "Netgiro Payment Response - Verified without NetgiroSignature, amount and status were not part of the signature. " + PaymentLog.Template,
                    payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);
            }

            // Recorded once per payment: Netgiro confirms twice (status 1, then 2).
            var result = await _payments.RecordAsync(new PaymentTransaction
            {
                PaymentId = payment.PaymentId,
                Type = PaymentTransactionType.Captured,
                Amount = payment.Amount,
                TransactionId = netgiroResponse.TransactionId,
                PaymentMethod = "Netgiro",
                IdempotencyKey = LegacyPaymentBridge.CaptureKey(payment.PaymentId),
                Details =
                [
                    new PaymentDetail("invoiceNumber", "Invoice number", netgiroResponse.InvoiceNumber, true),
                    new PaymentDetail("status", "Netgiro status", netgiroResponse.Status),
                ],
                RawData = JsonConvert.SerializeObject(new
                {
                    netgiroResponse.ReferenceNumber,
                    netgiroResponse.TransactionId,
                    netgiroResponse.InvoiceNumber,
                    netgiroResponse.TotalAmount,
                    netgiroResponse.Status,
                    netgiroResponse.ConfirmationCode,
                }),
            });

            _logger.LogInformation(
                "Netgiro Payment Response - SUCCESS. " + PaymentLog.Template + " FirstDelivery: {Recorded}",
                payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId, result.Recorded);

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Netgiro Payment Response - Failed. OrderNumber: {OrderNumber} OrderId: {OrderUniqueId} PaymentReference: {PaymentReference}",
                payment?.OrderNumber, payment?.OrderUniqueId, netgiroResponse?.ReferenceNumber);

            if (_settings.SendEmailAlerts)
            {
                await _mailSvc.SendAsync(new System.Net.Mail.MailMessage
                {
                    Subject = $"Netgiro Payment Response - Failed. Order {payment?.OrderNumber ?? "unknown"}, payment {netgiroResponse?.ReferenceNumber}",
                    Body = $"<p>Netgiro Payment Response - Failed<p><br />{HttpContext.Request.GetDisplayUrl()}<br />" + ex.ToString() + "<br><br> " + JsonConvert.SerializeObject(new
                    {
                        netgiroResponse?.ReferenceNumber, netgiroResponse?.TransactionId, netgiroResponse?.InvoiceNumber, netgiroResponse?.Status,
                    }),
                    IsBodyHtml = true,
                });
            }

            throw;
        }
    }

    internal static bool IsVerified(
        Response netgiroResponse,
        Guid paymentId,
        decimal amount,
        NetgiroSettings netgiroSettings,
        out string rejection)
    {
        var referenceNumber = paymentId.ToString();
        string received;
        string expected;

        if (!IsNumeric(netgiroResponse.InvoiceNumber))
        {
            rejection = "InvoiceNumber missing or not numeric";
            return false;
        }

        if (!string.IsNullOrEmpty(netgiroResponse.NetgiroSignature))
        {
            if (string.IsNullOrEmpty(netgiroResponse.TransactionId))
            {
                rejection = "TransactionId missing";
                return false;
            }

            // 1 - unconfirmed, the payment is confirmed by our reply to this callback. 2 - confirmed. 5 - canceled
            if (netgiroResponse.Status != "1" && netgiroResponse.Status != "2")
            {
                rejection = "Status is not unconfirmed or confirmed";
                return false;
            }

            if (!IsNumeric(netgiroResponse.TotalAmount)
            || !decimal.TryParse(netgiroResponse.TotalAmount, NumberStyles.None, CultureInfo.InvariantCulture, out var totalAmount)
            || totalAmount != decimal.Parse(Payment.FormatPrice(amount), CultureInfo.InvariantCulture))
            {
                rejection = "TotalAmount does not match the order";
                return false;
            }

            received = netgiroResponse.NetgiroSignature;
            expected = CryptoHelpers.GetSHA256HexStringSum(
                Payment.CombineSignature(
                    netgiroSettings.Secret,
                    referenceNumber,
                    netgiroResponse.TransactionId,
                    netgiroResponse.InvoiceNumber,
                    netgiroResponse.TotalAmount,
                    netgiroResponse.Status));
        }
        else
        {
            if (string.IsNullOrEmpty(netgiroResponse.ConfirmationCode))
            {
                rejection = "ConfirmationCode missing";
                return false;
            }

            received = netgiroResponse.Signature;
            expected = CryptoHelpers.GetSHA256HexStringSum(
                Payment.CombineSignature(
                    netgiroSettings.Secret,
                    referenceNumber,
                    netgiroResponse.ConfirmationCode,
                    netgiroResponse.InvoiceNumber));
        }

        if (!PaymentRowGuard.SignatureEquals(received, expected))
        {
            rejection = "Signature mismatch";
            return false;
        }

        // The signature we send with the payment request is not a payment confirmation
        if (PaymentRowGuard.SignatureEquals(received, Payment.RequestSignature(netgiroSettings, paymentId, amount)))
        {
            rejection = "Signature is the payment request signature";
            return false;
        }

        rejection = string.Empty;
        return true;
    }

    static bool IsNumeric(string? value)
        => !string.IsNullOrEmpty(value) && value.All(x => x is >= '0' and <= '9');
}
