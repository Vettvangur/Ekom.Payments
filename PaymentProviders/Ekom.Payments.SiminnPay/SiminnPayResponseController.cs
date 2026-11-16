using Ekom.Models.Payments;
using Ekom.Payments.Helpers;
using Ekom.Payments.Providers;
using Ekom.Payments.SiminnPay.Model;
using Ekom.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Mail;

namespace Ekom.Payments.SiminnPay;

/// <summary>
/// Receives Síminn Pay's notification when the customer answers a payment request.
/// Verifies it and records the outcome on the Ekom order; Ekom then completes the checkout.
/// </summary>
[Route("ekom/payments/[controller]")]
[ApiController]
public class SiminnPayResponseController : ControllerBase
{
    readonly ILogger _logger;
    readonly PaymentsConfiguration _settings;
    readonly IOrderPayments _payments;
    readonly IOrderService _legacyOrders;
    readonly IMailService _mailSvc;
    readonly IUmbracoService _uService;

    /// <summary>
    /// ctor
    /// </summary>
    public SiminnPayResponseController(
        ILogger<SiminnPayResponseController> logger,
        PaymentsConfiguration settings,
        IOrderPayments payments,
        IOrderService legacyOrders,
        IMailService mailSvc,
        IUmbracoService uService)
    {
        _logger = logger;
        _settings = settings;
        _payments = payments;
        _legacyOrders = legacyOrders;
        _mailSvc = mailSvc;
        _uService = uService;
    }

    /// <summary>
    /// Receives Síminn Pay's notification for a payment order.
    /// </summary>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpGet, HttpPost]
    [Route("")]
    public async Task<IActionResult> Post(SiminnPayOrderStatus notificationCallBack)
    {
        if (!ModelState.IsValid || notificationCallBack == null)
        {
            return BadRequest();
        }

        // Until the payment is loaded Síminn Pay's order key is all we know.
        _logger.LogInformation("SiminnPay Response - Start. OrderKey: {OrderKey}", notificationCallBack.OrderKey);

        OrderPayment? payment = null;

        try
        {
            payment = await LegacyPaymentBridge.GetOrImportByReferenceAsync(
                _payments, _legacyOrders, Payment._ppNodeName, notificationCallBack.OrderKey.ToString());
            if (payment == null || !PaymentRowGuard.BelongsTo(payment, Payment._ppNodeName))
            {
                _logger.LogWarning("SiminnPay Response - Unable to find a Síminn Pay payment. OrderKey: {OrderKey}", notificationCallBack.OrderKey);

                return NotFound();
            }

            using var scope = PaymentLog.BeginScope(_logger, payment);

            var siminnPaySettings = ReadSettings(payment);

            // A notification that cannot be verified says nothing about the payment: it is logged, never recorded.
            if (string.IsNullOrWhiteSpace(siminnPaySettings?.Secret))
            {
                _logger.LogWarning("SiminnPay Response - Missing shared secret. " + PaymentLog.Template,
                    payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);
                return Unauthorized();
            }

            if (!IsValidSignature(notificationCallBack, siminnPaySettings.Secret))
            {
                _logger.LogWarning("SiminnPay Response - Signature mismatch. " + PaymentLog.Template,
                    payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);
                return Unauthorized();
            }

            if (notificationCallBack.Amount != payment.Amount)
            {
                _logger.LogWarning(
                    "SiminnPay Response - Amount mismatch. Expected {ExpectedAmount}, received {ReceivedAmount}. " + PaymentLog.Template,
                    payment.Amount, notificationCallBack.Amount, payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);
                return BadRequest();
            }

            var transaction = ToTransaction(payment, notificationCallBack);
            if (transaction == null)
            {
                _logger.LogInformation("SiminnPay Response - Status {SiminnPayStatus} is not final. " + PaymentLog.Template,
                    notificationCallBack.Status, payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);
                return Ok();
            }

            var result = await _payments.RecordAsync(transaction);

            _logger.LogInformation(
                "SiminnPay Response - {SiminnPayStatus} recorded as {PaymentEvent}. " + PaymentLog.Template + " FirstDelivery: {Recorded}",
                notificationCallBack.Status, transaction.Type, payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId, result.Recorded);

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SiminnPay Response - Failed. OrderNumber: {OrderNumber} OrderId: {OrderUniqueId} OrderKey: {OrderKey}",
                payment?.OrderNumber, payment?.OrderUniqueId, notificationCallBack.OrderKey);

            if (_settings.SendEmailAlerts)
            {
                await _mailSvc.SendAsync(new MailMessage
                {
                    Subject = $"Síminn Pay Response - Failed. Order {payment?.OrderNumber ?? "unknown"}",
                    Body = "<p>Síminn Pay Response - Failed<p><br />" + ex,
                    IsBodyHtml = true,
                });
            }

            throw;
        }
    }

    /// <summary>
    /// Translates a verified Síminn Pay status into what is recorded on the order. Null while the customer has not answered.
    /// </summary>
    internal static PaymentTransaction? ToTransaction(OrderPayment payment, SiminnPayOrderStatus notification)
    {
        var transaction = new PaymentTransaction
        {
            PaymentId = payment.PaymentId,
            Type = PaymentTransactionType.Captured,
            Amount = payment.Amount,
            TransactionId = notification.TransactionDetails?.TransactionId,
            PaymentMethod = "Síminn Pay",
            Details =
            [
                new PaymentDetail("orderKey", "Síminn Pay order key", notification.OrderKey.ToString(), true),
                new PaymentDetail("status", "Síminn Pay status", notification.Status.ToString()),
            ],
        };

        return notification.Status switch
        {
            SiminnPayStatus.PaymentSuccessful => transaction with { IdempotencyKey = LegacyPaymentBridge.CaptureKey(payment.PaymentId) },
            SiminnPayStatus.CancelledByCustomer => transaction with { Type = PaymentTransactionType.Cancelled, Message = "Cancelled by the customer" },
            SiminnPayStatus.CancelledByMerchant => transaction with { Type = PaymentTransactionType.Cancelled, Message = "Cancelled by the merchant" },
            SiminnPayStatus.Expired => transaction with { Type = PaymentTransactionType.Failed, Message = "The payment request expired" },
            SiminnPayStatus.AcquirerError => transaction with { Type = PaymentTransactionType.Failed, Message = "The acquirer reported an error" },
            SiminnPayStatus.UnknownError => transaction with { Type = PaymentTransactionType.Failed, Message = "Síminn Pay reported an unknown error" },
            _ => null,
        };
    }

    /// <summary>
    /// Lets the storefront poll the state of a payment request while the customer answers it in the Síminn Pay app.
    /// </summary>
    [HttpGet("status")]
    public async Task<ActionResult> StatusAsync(Guid siminnPayOrderKey)
    {
        if (siminnPayOrderKey == Guid.Empty)
        {
            return BadRequest("Missing siminnPayOrderKey parameter.");
        }

        var payment = await LegacyPaymentBridge.GetOrImportByReferenceAsync(
            _payments, _legacyOrders, Payment._ppNodeName, siminnPayOrderKey.ToString());
        if (payment == null || !PaymentRowGuard.BelongsTo(payment, Payment._ppNodeName))
        {
            return NotFound("Pay order not found.");
        }

        var svc = new SiminnPayService(ReadSettings(payment)!, _logger);
        var initialStatus = await svc.GetStatus(siminnPayOrderKey);

        _logger.LogDebug("SiminnPay Status Requested - Status: {Status} " + PaymentLog.Template,
            initialStatus.Status, payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);

        return new JsonResult(new SiminnPayStatusView(payment, initialStatus));
    }

    // Síminn Pay's credentials are read from the payment provider node, not stored with the payment.
    SiminnPaySettings? ReadSettings(OrderPayment payment)
    {
        var paymentSettings = payment.GetPaymentSettings()
            ?? throw new InvalidOperationException($"Payment {payment.PaymentId} has no payment settings.");
        var siminnPaySettings = new SiminnPaySettings();

        _uService.PopulatePaymentProviderProperties(
            paymentSettings,
            Payment._ppNodeName,
            siminnPaySettings,
            SiminnPaySettings.Properties);

        return siminnPaySettings;
    }

    private static bool IsValidSignature(SiminnPayOrderStatus notificationCallBack, string secret)
    {
        if (string.IsNullOrWhiteSpace(notificationCallBack.HMAC))
        {
            return false;
        }

        string body = notificationCallBack.OrderKey.ToString() +
                    (int)notificationCallBack.Amount +
                    notificationCallBack.ExpiresAt.ToString("dd.MM.yyyy HH:mm:ss");

        var signature = CryptoHelpers.GetHMACSHA256(secret, body);

        return notificationCallBack.HMAC.Equals(signature, StringComparison.InvariantCultureIgnoreCase);
    }
}
