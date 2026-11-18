using Ekom.Models.Payments;
using Ekom.Payments.AltaPay.Model;
using Ekom.Payments.Helpers;
using Ekom.Payments.Providers;
using Ekom.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Globalization;
using System.Text.Json;

namespace Ekom.Payments.AltaPay;

/// <summary>
/// Receives AltaPay's responses for a payment.
/// Verifies the confirmation and records the capture on the Ekom order; Ekom then completes the checkout.
/// </summary>
[Route("ekom/payments/[controller]")]
[ApiController]
public class AltaResponseController : ControllerBase
{
    readonly ILogger _logger;
    readonly PaymentsConfiguration _settings;
    readonly IOrderPayments _payments;
    readonly IOrderService _legacyOrders;
    readonly IMailService _mailSvc;
    readonly HttpContext _httpCtx;

    /// <summary>
    /// ctor
    /// </summary>
    public AltaResponseController(
        ILogger<AltaResponseController> logger,
        PaymentsConfiguration settings,
        IOrderPayments payments,
        IOrderService legacyOrders,
        IMailService mailSvc,
        IHttpContextAccessor httpContext)
    {
        _logger = logger;
        _settings = settings;
        _payments = payments;
        _legacyOrders = legacyOrders;
        _mailSvc = mailSvc;
        _httpCtx = httpContext.HttpContext ?? throw new NotSupportedException("Payment requests require an httpcontext");
    }

    /// <summary>
    /// Receives a callback from Straumur when customer completes payment.
    /// Changes order status and optionally runs a custom callback provided by the application consuming this library.
    /// </summary>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpGet, HttpPost]
    [Route("")]
    public async Task<IActionResult> Post(PaymentResponse response)
    {
        _logger.LogInformation("Alta Payment Response - Start");

        var model = response.GetApiResponse();

        _logger.LogDebug(JsonConvert.SerializeObject(model));


        if (model == null || !ModelState.IsValid)
        {
            _logger.LogDebug(JsonConvert.SerializeObject(ModelState));
            return BadRequest();
        }

        _logger.LogDebug("Alta Payment Response - ModelState.IsValid");

        var paid = model.Body.PaymentStatus == PaymentStatus.Success;
        OrderPayment? payment = null;
        PaymentSettings? paymentSettings = null;

        // A response that is not a success records nothing unless it is verified. The customer's browser
        // is sent to the error page; the server notification gets 200 as before.
        IActionResult NotPaid() => NotPaidResult(paymentSettings, model);

        try
        {
            var transaction = model.Body.Transactions?.FirstOrDefault();
            if (transaction == null || !TryGetOrderId(transaction.ShopOrderId, out var orderId))
            {
                _logger.LogWarning("Alta Payment Response - ShopOrderId has no order id");
                return paid ? BadRequest() : NotPaid();
            }

            // Until the payment is loaded the order id is all we know.
            _logger.LogInformation("Alta Payment Response - Start. OrderId: {OrderUniqueId}", orderId);

            payment = await FindPaymentAsync(orderId);

            if (payment == null)
            {
                _logger.LogWarning("Alta Payment Response - Unable to find an AltaPay payment. OrderId: {OrderUniqueId}", orderId);

                return paid ? NotFound() : NotPaid();
            }

            using var scope = PaymentLog.BeginScope(_logger, payment);

            paymentSettings = payment.GetPaymentSettings()
                ?? throw new InvalidOperationException($"Payment {payment.PaymentId} has no payment settings.");
            paymentSettings.SuccessUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.SuccessUrl, _httpCtx.Request);

            var altaSettings = payment.GetProviderData<AltaSettings>();

            // A response that cannot be verified says nothing about the payment: it is logged, never recorded.
            if (altaSettings == null || string.IsNullOrWhiteSpace(altaSettings.Terminal))
            {
                _logger.LogError("Alta Payment Response - No AltaPay settings, unable to verify. " + PaymentLog.Template,
                    payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);

                return paid ? StatusCode(500) : NotPaid();
            }

            // The checksum is the only proof that the response comes from AltaPay,
            // it requires a shared secret configured on the AltaPay terminal and on the payment provider
            if (string.IsNullOrWhiteSpace(altaSettings.CustomerInformationSharedSecret))
            {
                _logger.LogError(
                    "Alta Payment Response - No CustomerInformationSharedSecret, unable to verify. "
                    + "Configure the shared secret on the AltaPay terminal and on the payment provider. " + PaymentLog.Template,
                    payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);

                return paid ? StatusCode(500) : NotPaid();
            }

            if (!ChecksumMatches(payment, altaSettings, response, transaction, orderId))
            {
                _logger.LogError("Alta Payment Response - Failed checksum verification. " + PaymentLog.Template,
                    payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);

                return paid ? StatusCode(500) : NotPaid();
            }

            if (!paid)
            {
                await RecordFailureAsync(payment, altaSettings, transaction, model);

                return NotPaid();
            }

            // Recorded once per payment: AltaPay sends both a browser response and a server notification.
            var result = await _payments.RecordAsync(new PaymentTransaction
            {
                PaymentId = payment.PaymentId,
                Type = PaymentTransactionType.Captured,
                Amount = payment.Amount,
                TransactionId = transaction.TransactionId,
                MaskedCardNumber = transaction.CreditCardMaskedPan,
                IdempotencyKey = LegacyPaymentBridge.CaptureKey(payment.PaymentId),
                Details =
                [
                    new PaymentDetail("altaPaymentId", "AltaPay payment id", transaction.PaymentId, true),
                    new PaymentDetail("authType", "Authorization type", transaction.AuthType),
                    new PaymentDetail("cardStatus", "Card status", transaction.CardStatus),
                    new PaymentDetail("terminal", "Terminal", altaSettings.Terminal),
                ],
                RawData = JsonConvert.SerializeObject(model),
            });

            _logger.LogInformation("Alta Payment Response - SUCCESS. " + PaymentLog.Template + " FirstDelivery: {Recorded}",
                payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId, result.Recorded);

            return Redirect(paymentSettings.SuccessUrl.ToString());
        }
        catch (Exception ex)
        {
            var transaction = model.Body.Transactions.FirstOrDefault();
            _logger.LogError(ex, "Alta Payment Response - Failed. OrderNumber: {OrderNumber} OrderId: {OrderUniqueId} ShopOrderId: {ShopOrderId}",
                payment?.OrderNumber, payment?.OrderUniqueId, transaction?.ShopOrderId);

            if (_settings.SendEmailAlerts)
            {
                await _mailSvc.SendAsync(new System.Net.Mail.MailMessage
                {
                    Subject = $"Alta Payment Response - Failed. Order {payment?.OrderNumber ?? "unknown"}, {transaction?.ShopOrderId}",
                    Body = $"<p>Alta Payment Response - Failed<p><br />{HttpContext.Request.GetDisplayUrl()}<br />" + ex.ToString() + "<br><br> " + JsonConvert.SerializeObject(model),
                    IsBodyHtml = true,
                });
            }

            throw;
        }
    }

    /// <summary>
    /// AltaPay refers to a payment by the order id. The latest AltaPay payment of that order is the one being answered.
    /// </summary>
    async Task<OrderPayment?> FindPaymentAsync(Guid orderId)
    {
        var payment = await LegacyPaymentBridge.GetOrImportByReferenceAsync(
            _payments, _legacyOrders, Payment._ppNodeName, orderId.ToString());

        return payment != null && PaymentRowGuard.BelongsTo(payment, Payment._ppNodeName) ? payment : null;
    }

    /// <summary>
    /// The checksum AltaPay calculates with the shared secret: the only proof that a response comes from AltaPay.
    /// </summary>
    static bool ChecksumMatches(OrderPayment payment, AltaSettings altaSettings, PaymentResponse response, Transaction transaction, Guid orderId)
    {
        if (string.IsNullOrWhiteSpace(altaSettings.CustomerInformationSharedSecret))
        {
            return false;
        }

        var calculatedChecksum = AltaResponseHelper.CalculateChecksum(new ChecksumCalculationRequest
        {
            Amount = payment.Amount.ToString("F2", CultureInfo.InvariantCulture),
            Currency = transaction.MerchantCurrency.ToString(),
            OrderId = orderId.ToString(),
            Secret = altaSettings.CustomerInformationSharedSecret
        });

        return payment.OrderUniqueId == orderId && PaymentRowGuard.SignatureEquals(response.Checksum, calculatedChecksum);
    }

    /// <summary>AltaPay's results for a payment that did not go through.</summary>
    static bool IsFailure(string? result)
        => string.Equals(result, "Fail", StringComparison.OrdinalIgnoreCase)
        || string.Equals(result, "Failed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(result, "Error", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Records a verified failure. Other results that are not a success (f.x. Open) are not final and record nothing.
    /// </summary>
    async Task RecordFailureAsync(OrderPayment payment, AltaSettings altaSettings, Transaction transaction, APIResponse model)
    {
        if (!IsFailure(model.Body.Result))
        {
            _logger.LogInformation("Alta Payment Response - Result {AltaResult} is not final. " + PaymentLog.Template,
                model.Body.Result, payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);
            return;
        }

        // The browser response and the server notification carry the same transaction: recorded once.
        var result = await _payments.RecordAsync(new PaymentTransaction
        {
            PaymentId = payment.PaymentId,
            Type = PaymentTransactionType.Failed,
            TransactionId = string.IsNullOrWhiteSpace(transaction.TransactionId) ? null : transaction.TransactionId,
            Message = string.IsNullOrWhiteSpace(model.Header?.ErrorMessage) ? "AltaPay reported the payment as failed" : model.Header.ErrorMessage,
            Details =
            [
                new PaymentDetail("result", "AltaPay result", model.Body.Result),
                new PaymentDetail("terminal", "Terminal", altaSettings.Terminal),
            ],
            RawData = JsonConvert.SerializeObject(model),
        });

        _logger.LogWarning("Alta Payment Response - FAILED. " + PaymentLog.Template + " Result: {AltaResult} FirstDelivery: {Recorded}",
            payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId, model.Body.Result, result.Recorded);
    }

    /// <summary>
    /// The answer to a response that is not a success. The customer's browser, recognised by the marker
    /// on the callback_ok address, is sent to the error page; the server notification gets 200 as before.
    /// </summary>
    IActionResult NotPaidResult(PaymentSettings? paymentSettings, APIResponse model)
    {
        if (!Request.Query.ContainsKey(Payment.BrowserQueryKey) || paymentSettings?.ErrorUrl == null)
        {
            return Ok("Payment not Valid");
        }

        var errorUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.ErrorUrl, _httpCtx.Request).ToString();
        var transaction = model.Body?.Transactions?.FirstOrDefault();
        if (transaction != null)
        {
            errorUrl = AddTransactionQueryParams(errorUrl, transaction, model);
        }

        return Redirect(QueryHelpers.AddQueryString(errorUrl, "altapayFailed", "true"));
    }

    /// <summary>The shop order id is the order id, optionally prefixed with "item;item;".</summary>
    static bool TryGetOrderId(string? shopOrderId, out Guid orderId)
    {
        orderId = Guid.Empty;

        if (string.IsNullOrWhiteSpace(shopOrderId))
        {
            return false;
        }

        return Guid.TryParse(shopOrderId, out orderId)
            || Guid.TryParse(shopOrderId.Split(';', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(), out orderId);
    }

    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpGet, HttpPost]
    [Route("fail")]
    public async Task<IActionResult> Fail(PaymentResponse? response, CancellationToken ct = default)
    {
        _logger.LogInformation("Alta Payment Fail Response - Start");

        if (response is null)
        {
            _logger.LogWarning("Alta Payment Fail Response - PaymentResponse is null");
            return BadRequest();
        }

        APIResponse? model;
        try
        {
            model = response.GetApiResponse();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Alta Payment Fail Response - GetApiResponse threw");
            await NotifyErrorAsync(ex, null, null, ct);
            return StatusCode(StatusCodes.Status500InternalServerError);
        }

        if (model is null)
        {
            _logger.LogWarning("Alta Payment Fail Response - model is null");
            return BadRequest();
        }

        if (!ModelState.IsValid)
        {
            _logger.LogWarning("Alta Payment Fail Response - ModelState invalid {@ModelState}", ModelState);
            return BadRequest(ModelState);
        }

        var transaction = model.Body?.Transactions?.FirstOrDefault();

        if (transaction is null)
        {
            _logger.LogWarning("Alta Payment Fail Response - No transactions in response");
            return BadRequest();
        }

        _logger.LogWarning(
            "Alta Payment Fail Response - Message: {Message}", System.Text.Json.JsonSerializer.Serialize(model)
        );

        if (!TryGetOrderId(transaction.ShopOrderId, out var orderId))
        {
            _logger.LogWarning(
                "Alta Payment Fail Response - Invalid ShopOrderId {ShopOrderId}",
                transaction.ShopOrderId
            );
            return BadRequest();
        }

        _logger.LogInformation("Alta Payment Fail Response - Parsed OrderID {OrderId}", orderId);

        OrderPayment? order;

        try
        {
            order = await FindPaymentAsync(orderId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Alta Payment Fail Response - Error getting payment. OrderId: {OrderUniqueId}", orderId);
            await NotifyErrorAsync(ex, transaction.ShopOrderId, orderId, ct);
            return StatusCode(StatusCodes.Status500InternalServerError);
        }

        if (order is null)
        {
            _logger.LogWarning("Alta Payment Fail Response - Unable to find an AltaPay payment. OrderId: {OrderUniqueId}", orderId);
            return NotFound();
        }

        // A failure is recorded only when the response passes the same checksum verification as a success.
        // Without it nothing is recorded: the customer is only sent to the error page.
        try
        {
            var altaSettings = order.GetProviderData<AltaSettings>();
            if (altaSettings != null
                && !string.IsNullOrWhiteSpace(altaSettings.Terminal)
                && ChecksumMatches(order, altaSettings, response, transaction, orderId))
            {
                await RecordFailureAsync(order, altaSettings, transaction, model);
            }
            else
            {
                _logger.LogInformation("Alta Payment Fail Response - Not verified, nothing recorded. " + PaymentLog.Template,
                    order.OrderNumber, order.OrderUniqueId, order.PaymentId);
            }
        }
        catch (Exception ex)
        {
            // The customer is still sent to the error page.
            _logger.LogError(ex, "Alta Payment Fail Response - Unable to record the failure. " + PaymentLog.Template,
                order.OrderNumber, order.OrderUniqueId, order.PaymentId);
            await NotifyErrorAsync(ex, transaction.ShopOrderId, orderId, ct);
        }

        PaymentSettings? paymentSettings;
        try
        {
            paymentSettings = order.GetPaymentSettings();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Alta Payment Fail Response - Unable to read payment settings. " + PaymentLog.Template,
                order.OrderNumber, order.OrderUniqueId, order.PaymentId);
            await NotifyErrorAsync(ex, transaction.ShopOrderId, orderId, ct);
            paymentSettings = null;
        }

        Uri? redirectUri = null;

        // 1. Resolve base redirect URL
        try
        {
            if (paymentSettings?.ErrorUrl != null)
            {
                redirectUri = PaymentsUriHelper.EnsureFullUri(
                    paymentSettings.ErrorUrl,
                    _httpCtx.Request);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error making ErrorUrl absolute for order {OrderId}",
                orderId);
        }

        // 2. Fallback if needed
        if (redirectUri == null)
        {
            try
            {
                redirectUri = PaymentsUriHelper.EnsureFullUri(
                    new Uri("/?altapayerror=true", UriKind.Relative),
                    _httpCtx.Request);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Fallback ErrorUrl also failed for order {OrderId}",
                    orderId);
            }
        }

        // 3. Hard failure
        if (redirectUri == null)
        {
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                "Payment failed");
        }

        // 4. Append transaction query params
        var redirectUrl = redirectUri.ToString();

        redirectUrl = AddTransactionQueryParams(
            redirectUrl,
            transaction,
            model);

        // explicit frontend flag
        redirectUrl = QueryHelpers.AddQueryString(
            redirectUrl,
            "altapayFailed",
            "true");
        

        _logger.LogInformation(
            "Alta Payment Fail Response - Redirecting to {ErrorUrl}",
            redirectUrl);

        return Redirect(redirectUrl);



        async Task NotifyErrorAsync(
            Exception ex,
            string? shopOrderId,
            Guid? orderId,
            CancellationToken token)
        {
            try
            {
                if (_settings.SendEmailAlerts)
                {
                    var subject = $"Alta Payment Fail Response - Failed. {shopOrderId ?? orderId?.ToString() ?? "Unknown"}";

                    var body = $@"
                        <p>Alta Payment Fail Response - Failed</p>
                        <p>Request URL: {HttpContext.Request.GetDisplayUrl()}</p>
                        <p>ShopOrderId: {shopOrderId}</p>
                        <p>OrderId: {orderId}</p>
                        <pre>{ex}</pre>";

                    var msg = new System.Net.Mail.MailMessage
                    {
                        Subject = subject,
                        Body = body,
                        IsBodyHtml = true
                    };

                    await _mailSvc.SendAsync(msg);

                    msg.Dispose();

                }
            }
            catch (Exception notifyEx)
            {
                _logger.LogError(
                    notifyEx,
                    "Alta Payment Fail Response - Failed while sending error notification"
                );
            }
        }
    }

    private static string AddTransactionQueryParams(
    string baseUrl,
    Transaction tx,
    APIResponse model)
    {
        var query = new Dictionary<string, string?>
        {
            ["transactionId"] = tx.TransactionId,
            ["paymentId"] = tx.PaymentId,
            ["reservedAmount"] = tx.ReservedAmount.ToString("F2", CultureInfo.InvariantCulture),
            ["capturedAmount"] = tx.CapturedAmount.ToString("F2", CultureInfo.InvariantCulture),
            ["refundedAmount"] = tx.RefundedAmount.ToString("F2", CultureInfo.InvariantCulture),
            ["currency"] = tx.MerchantCurrencyAlpha,
            ["paymentStatus"] = model.Body.PaymentStatus.ToString(),
            ["result"] = model.Body.Result,
        };

        // Optional AltaPay error context
        if (!string.IsNullOrWhiteSpace(model.Header?.ErrorCode))
            query["errorCode"] = model.Header.ErrorCode;

        if (!string.IsNullOrWhiteSpace(model.Header?.ErrorMessage))
            query["errorMessage"] = model.Header.ErrorMessage;

        return QueryHelpers.AddQueryString(baseUrl, query!);
    }

    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpGet, HttpPost]
    [Route("callback")]
    public async Task<IActionResult> CallbackFormAsync(CallbackFromRequest request)
    {
        var template = "";

        var eventArgs = new CallbackUrlEventArgs()
        {
            Request = request,
            Template = template
        };

        await Events.OnCallbackUrlAsync(this, eventArgs);

        // AltaPay shows the returned template as the payment form page
        return Content(eventArgs.Template ?? string.Empty, "text/html");
    }
}
