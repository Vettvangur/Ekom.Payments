using Ekom.Models.Payments;
using Ekom.Payments.Helpers;
using Ekom.Services;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Ekom.Payments.AsynchronousExample;

/// <summary>
/// Receives a callback from the payment provider when a customer completes a payment.
/// Records the outcome on the Ekom order; Ekom then updates the payment status and completes the checkout.
/// </summary>
[Route("ekom/payments/[controller]")]
[ApiController]
public class AsynchronousExampleResponseController : ControllerBase
{
    const string Provider = Payment._ppNodeName;

    readonly ILogger _logger;
    readonly PaymentsConfiguration _settings;
    readonly IOrderPayments _payments;
    readonly IMailService _mailSvc;

    /// <summary>
    /// ctor
    /// </summary>
    public AsynchronousExampleResponseController(
        ILogger<AsynchronousExampleResponseController> logger,
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
    /// Receives a callback from the provider when a customer completes payment.
    /// </summary>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpGet, HttpPost]
    [Route("")]
    public async Task<IActionResult> Post([FromQuery] Response AsynchronousExampleResp)
    {
        try
        {
            if (!Guid.TryParse(AsynchronousExampleResp.ReferenceNumber, out var paymentId))
            {
                return BadRequest();
            }

            _logger.LogInformation("AsynchronousExample Payment Response - PaymentId: {PaymentId}", paymentId);

            OrderPayment? payment = await _payments.GetAsync(paymentId);
            if (payment == null || !payment.BelongsTo(Provider))
            {
                _logger.LogWarning("AsynchronousExample Payment Response - Unable to find payment. PaymentId: {PaymentId}", paymentId);

                return NotFound();
            }

            var paymentSettings = payment.GetPaymentSettings()
                ?? throw new InvalidOperationException($"Payment {paymentId} has no payment settings.");

            // Every log line from here on leads with the order.
            using var scope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["OrderNumber"] = payment.OrderNumber,
                ["OrderUniqueId"] = payment.OrderUniqueId,
                ["PaymentId"] = payment.PaymentId,
                ["PaymentProvider"] = Provider,
            });

            // perform payment provider specific security validation here
            bool isValid = !AsynchronousExampleResp.Failed;

            if (isValid)
            {
                // Recording is safe to repeat: the same callback delivered twice is stored once.
                var result = await _payments.RecordAsync(new PaymentTransaction
                {
                    PaymentId = payment.PaymentId,
                    Type = PaymentTransactionType.Captured,
                    TransactionId = AsynchronousExampleResp.TransactionNumber == 0 ? null : AsynchronousExampleResp.TransactionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    AuthorizationCode = AsynchronousExampleResp.AuthorizationNumber,
                    CardBrand = AsynchronousExampleResp.CardType,
                    MaskedCardNumber = AsynchronousExampleResp.CardNumberMasked,
                    Details = [new PaymentDetail("reference", "Reference", AsynchronousExampleResp.ReferenceNumber, true)],
                    RawData = JsonConvert.SerializeObject(AsynchronousExampleResp),
                });

                _logger.LogInformation(
                    "AsynchronousExample Payment Response - SUCCESS - OrderNumber: {OrderNumber} OrderId: {OrderUniqueId} PaymentId: {PaymentId} FirstDelivery: {Recorded}",
                    payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId, result.Recorded);

                var successUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.SuccessUrl.ToString(), Request);
                return Redirect(successUrl.ToString());
            }

            await _payments.RecordAsync(new PaymentTransaction
            {
                PaymentId = payment.PaymentId,
                Type = PaymentTransactionType.Failed,
                TransactionId = AsynchronousExampleResp.TransactionNumber == 0 ? null : AsynchronousExampleResp.TransactionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Message = string.IsNullOrWhiteSpace(AsynchronousExampleResp.Message) ? "Payment was declined" : AsynchronousExampleResp.Message,
                RawData = JsonConvert.SerializeObject(AsynchronousExampleResp),
            });

            _logger.LogWarning(
                "AsynchronousExample Payment Response - FAILED - OrderNumber: {OrderNumber} OrderId: {OrderUniqueId} PaymentId: {PaymentId}",
                payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);

            var cancelUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.CancelUrl.ToString(), Request);
            return Redirect(cancelUrl.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AsynchronousExample Payment Response - Failed");

            if (_settings.SendEmailAlerts)
            {
                await _mailSvc.SendAsync(new System.Net.Mail.MailMessage
                {
                    Subject = "AsynchronousExample Payment Response - Failed",
                    Body = $"<p>AsynchronousExample Payment Response - Failed<p><br />{HttpContext.Request.GetDisplayUrl()}<br />" + ex.ToString() + "<br><br> " + JsonConvert.SerializeObject(AsynchronousExampleResp),
                    IsBodyHtml = true,
                });
            }

            throw;
        }
    }
}
