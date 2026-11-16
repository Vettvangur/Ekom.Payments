using Ekom.Models.Payments;
using Ekom.Payments.Providers;
using Ekom.Services;
using Ekom.Payments.PayTrail.Models;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Net.Mail;

namespace Ekom.Payments.PayTrail;

/// <summary>
/// Receives a callback from PayTrail when customer completes payment.
/// </summary>
[Route("ekom/payments/[controller]")]
[ApiController]
public class PayTrailResponseController : ControllerBase
{
    readonly ILogger _logger;
    readonly PaymentsConfiguration _settings;
    readonly IOrderPayments _payments;
    readonly IOrderService _legacyOrders;
    readonly IMailService _mailSvc;

    /// <summary>
    /// ctor
    /// </summary>
    public PayTrailResponseController(
        ILogger<PayTrailResponseController> logger,
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
    /// Receives PayTrail's redirect and server callback for a payment.
    /// Verifies it and records the outcome on the Ekom order; Ekom then completes the checkout.
    /// </summary>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpGet, HttpPost]
    [Route("")]
    public async Task<IActionResult> Post()
    {
        var parameters = Request.Query.ToDictionary(x => x.Key, x => x.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        var isCallback = parameters.ContainsKey("callback");
        OrderPayment? payment = null;

        try
        {
            var callback = CreateCallback(parameters);
            if (callback == null || !Guid.TryParse(callback.Stamp, out var paymentId))
            {
                return BadRequest();
            }

            // Until the payment is loaded its id is all we know.
            _logger.LogInformation("PayTrail Payment Response - Start. PaymentId: {PaymentId}", paymentId);

            payment = await LegacyPaymentBridge.GetOrImportAsync(_payments, _legacyOrders, paymentId).ConfigureAwait(false);
            if (payment == null || !PaymentRowGuard.BelongsTo(payment, Payment._ppNodeName))
            {
                _logger.LogWarning("PayTrail Payment Response - Unable to find a PayTrail payment. PaymentId: {PaymentId}", paymentId);
                return NotFound();
            }

            using var scope = PaymentLog.BeginScope(_logger, payment);

            var paymentSettings = payment.GetPaymentSettings();
            var payTrailSettings = payment.GetProviderData<PayTrailSettings>();

            if (paymentSettings == null || payTrailSettings == null)
            {
                return BadRequest();
            }

            var safeParameters = parameters.ToDictionary(x => x.Key, x => x.Key.Equals("signature", StringComparison.InvariantCultureIgnoreCase) ? "[redacted]" : x.Value);

            if (payTrailSettings.DebugLog)
            {
                _logger.LogInformation("PayTrail payment response payload: {ResponsePayload}", JsonConvert.SerializeObject(safeParameters));
            }

            // A response that cannot be verified says nothing about the payment: it is logged, never recorded.
            if (!PayTrailHmacHelper.IsValidSignature(payTrailSettings.SecretKey, parameters, callback.Signature))
            {
                _logger.LogWarning("PayTrail Payment Response - Signature mismatch. " + PaymentLog.Template,
                    payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);
                return Unauthorized();
            }

            var transaction = new PaymentTransaction
            {
                PaymentId = payment.PaymentId,
                Type = PaymentTransactionType.Captured,
                Amount = payment.Amount,
                TransactionId = string.IsNullOrWhiteSpace(callback.TransactionId) ? null : callback.TransactionId,
                PaymentMethod = string.IsNullOrWhiteSpace(callback.Provider) ? null : callback.Provider,
                Details =
                [
                    new PaymentDetail("reference", "PayTrail reference", callback.Reference, true),
                    new PaymentDetail("status", "PayTrail status", callback.Status),
                ],
                RawData = JsonConvert.SerializeObject(safeParameters),
            };

            if (callback.Status.Equals("ok", StringComparison.InvariantCultureIgnoreCase))
            {
                var expectedOrderAmount = GetExpectedOrderAmount(payment, paymentSettings);
                var expectedAmount = Payment.ToMinorUnits(expectedOrderAmount, paymentSettings.Currency);
                if (callback.Amount != expectedAmount)
                {
                    _logger.LogWarning(
                        "PayTrail Payment Response - Amount mismatch. Expected {ExpectedAmount}, got {ActualAmount}. " + PaymentLog.Template,
                        expectedAmount, callback.Amount, payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);
                    return BadRequest();
                }

                // Recorded once per payment: PayTrail sends both a redirect and a server callback.
                var result = await _payments.RecordAsync(transaction with
                {
                    Amount = expectedOrderAmount,
                    IdempotencyKey = LegacyPaymentBridge.CaptureKey(payment.PaymentId),
                }).ConfigureAwait(false);

                _logger.LogInformation("PayTrail Payment Response - SUCCESS. " + PaymentLog.Template + " FirstDelivery: {Recorded}",
                    payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId, result.Recorded);

                if (isCallback)
                {
                    return Ok();
                }

                var successUrl = Ekom.Payments.Helpers.PaymentsUriHelper.EnsureFullUri(paymentSettings.SuccessUrl.ToString(), Request);
                return Redirect(successUrl.ToString());
            }

            // "fail" is PayTrail's verified answer that the payment did not go through. "pending" and "delayed" are not final.
            if (callback.Status.Equals("fail", StringComparison.InvariantCultureIgnoreCase))
            {
                await _payments.RecordAsync(transaction with
                {
                    Type = PaymentTransactionType.Failed,
                    Message = "PayTrail reported the payment as failed",
                }).ConfigureAwait(false);

                _logger.LogWarning("PayTrail Payment Response - FAILED. " + PaymentLog.Template,
                    payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);
            }
            else
            {
                _logger.LogInformation("PayTrail Payment Response - Status {PayTrailStatus} is not final. " + PaymentLog.Template,
                    callback.Status, payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);
            }

            if (isCallback)
            {
                return Ok();
            }

            var cancelUrl = Ekom.Payments.Helpers.PaymentsUriHelper.EnsureFullUri(paymentSettings.CancelUrl.ToString(), Request);
            return Redirect(cancelUrl.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PayTrail Payment Response - Failed. OrderNumber: {OrderNumber} OrderId: {OrderUniqueId} PaymentId: {PaymentId}",
                payment?.OrderNumber, payment?.OrderUniqueId, payment?.PaymentId);

            if (_settings.SendEmailAlerts)
            {
                await _mailSvc.SendAsync(new MailMessage
                {
                    Subject = $"PayTrail Payment Response - Failed. Order {payment?.OrderNumber ?? "unknown"}",
                    Body = $"<p>PayTrail Payment Response - Failed<p><br />{HttpContext.Request.GetDisplayUrl()}<br />" + ex + "<br><br> "
                        + JsonConvert.SerializeObject(parameters.Where(x => !x.Key.Equals("signature", StringComparison.InvariantCultureIgnoreCase)).ToDictionary(x => x.Key, x => x.Value)),
                    IsBodyHtml = true,
                });
            }

            throw;
        }
    }

    static decimal GetExpectedOrderAmount(OrderPayment payment, PaymentSettings paymentSettings)
    {
        var orderLines = paymentSettings.Orders?.ToList();

        return orderLines is { Count: > 0 }
            ? orderLines.Sum(x => x.GrandTotal)
            : payment.Amount;
    }

    static PayTrailCallback? CreateCallback(IReadOnlyDictionary<string, string> parameters)
    {
        if (!parameters.TryGetValue("signature", out var signature)
            || !parameters.TryGetValue("checkout-stamp", out var stamp)
            || !parameters.TryGetValue("checkout-status", out var status)
            || !parameters.TryGetValue("checkout-amount", out var amount))
        {
            return null;
        }

        return new PayTrailCallback
        {
            Account = parameters.GetValueOrDefault("checkout-account") ?? string.Empty,
            Algorithm = parameters.GetValueOrDefault("checkout-algorithm") ?? string.Empty,
            Amount = int.Parse(amount),
            Stamp = stamp,
            Reference = parameters.GetValueOrDefault("checkout-reference") ?? string.Empty,
            TransactionId = parameters.GetValueOrDefault("checkout-transaction-id") ?? string.Empty,
            Status = status,
            Provider = parameters.GetValueOrDefault("checkout-provider") ?? string.Empty,
            Signature = signature,
        };
    }
}
