using Ekom.Payments.AltaPay.Model;
using Ekom.Payments.Helpers;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Globalization;

namespace Ekom.Payments.AltaPay;

/// <summary>
/// Receives a callback from Straumur when customer completes payment.
/// Changes order status and optionally runs a custom callback provided by the application consuming this library.
/// </summary>
[Route("ekom/payments/[controller]")]
[ApiController]
public class AltaResponseController : ControllerBase
{
    readonly ILogger _logger;
    readonly PaymentsConfiguration _settings;
    readonly IOrderService _orderService;
    readonly IDatabaseFactory _dbFac;
    readonly IMailService _mailSvc;
    readonly HttpContext _httpCtx;

    /// <summary>
    /// ctor
    /// </summary>
    public AltaResponseController(
        ILogger<AltaResponseController> logger,
        PaymentsConfiguration settings,
        IOrderService orderService,
        IDatabaseFactory dbFac,
        IMailService mailSvc,
        IHttpContextAccessor httpContext)
    {
        _logger = logger;
        _settings = settings;
        _orderService = orderService;
        _dbFac = dbFac;
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

        if (model.Body.PaymentStatus != PaymentStatus.Success)
        {
            return Ok("Payment not Valid");
        }

        try
        {
            var transaction = model.Body.Transactions.First();
            if (!Guid.TryParse(transaction.ShopOrderId, out var orderId))
            {
                if(transaction.ShopOrderId == null)
                {
                    _logger.LogWarning("Alta Payment Response - MerchantReference is null");
                    return BadRequest();
                }

                var merchantRefParts = transaction.ShopOrderId.Split(';');
                var rawGuid = merchantRefParts.LastOrDefault();

                if (!Guid.TryParse(rawGuid, out Guid newOrderId))
                {
                    return BadRequest();
                }

                orderId = newOrderId;
            }

            _logger.LogInformation("Alta Payment Response - OrderID: " + orderId);

            OrderStatus? order = await _orderService.GetByCustomAsync(orderId.ToString());

            if (order == null)
            {
                _logger.LogWarning("Alta Payment Response - Unable to find order {OrderId}", orderId);

                return NotFound();
            }

            if (order.Paid)
            {
                _logger.LogInformation("Alta Payment Response - Order {OrderId} already paid", orderId);
                return Ok();
            }

            var paymentSettings = JsonConvert.DeserializeObject<PaymentSettings>(order.EkomPaymentSettingsData);
            paymentSettings.SuccessUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.SuccessUrl, _httpCtx.Request);
            var altaSettings = JsonConvert.DeserializeObject<AltaSettings>(order.EkomPaymentProviderData);

            var checksumValid = true;
            if (altaSettings.CustomerInformationSharedSecret != null)
            {
                _logger.LogInformation("Alta Payment Response - Validationg checksum");

                if (string.IsNullOrEmpty(response.Checksum))
                {
                    _logger.LogWarning("Alta Payment Response - Expected incoming checksum not given.");
                }

                var calculatedChecksum = AltaResponseHelper.CalculateChecksum(new ChecksumCalculationRequest
                {
                    Amount = order.Amount.ToString("F2", CultureInfo.InvariantCulture),
                    Currency = transaction.MerchantCurrency.ToString(),
                    OrderId = orderId.ToString(),
                    Secret = altaSettings.CustomerInformationSharedSecret
                });

                checksumValid = response.Checksum == calculatedChecksum;
                _logger.LogInformation($"Alta Payment Response - Checksum is {(checksumValid ? "valid" : "invalid")}");
            }

            if (!order.UniqueId.Equals(orderId) || !checksumValid)
            {
                _logger.LogInformation($"Alta Payment Response - Verification Error - Order ID: {order.UniqueId}");

                await Events.OnErrorAsync(this, new ErrorEventArgs
                {
                    OrderStatus = order,
                });

                var cancelUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.CancelUrl, Request);

                return StatusCode(500);
            }

            _logger.LogInformation("Alta Payment Response - DigitalSignatureResponse Verified");

            if (!order.Paid)
            {
                try
                {
                    var paymentData = new PaymentData
                    {
                        Id = order.UniqueId,
                        Date = DateTime.Now,
                        CardNumber = transaction.CreditCardMaskedPan,
                        CustomData = JsonConvert.SerializeObject(model),
                        Amount = order.Amount.ToString(),
                    };

                    using var db = _dbFac.GetDatabase();
                    await db.InsertOrReplaceAsync(paymentData);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Alta Payment Response - Error saving payment data");
                }

                order.Paid = true;

                using (var db = _dbFac.GetDatabase())
                {
                    await db.UpdateAsync(order);
                }

                await Events.OnSuccessAsync(this, new SuccessEventArgs
                {
                    OrderStatus = order,
                });

                _logger.LogInformation($"Alta Payment Response - SUCCESS - Order ID: {order.UniqueId}");
            }
            else
            {
                _logger.LogInformation($"Alta Payment Response - SUCCESS - Previously validated");
            }

            return Redirect(paymentSettings.SuccessUrl.ToString());
        }
        catch (Exception ex)
        {
            var transaction = model.Body.Transactions.FirstOrDefault();
            _logger.LogError(ex, "Alta Payment Response - Failed. " + transaction?.ShopOrderId);
            await Events.OnErrorAsync(this, new ErrorEventArgs
            {
                Exception = ex,
            });

            if (_settings.SendEmailAlerts)
            {
                await _mailSvc.SendAsync(new System.Net.Mail.MailMessage
                {
                    Subject = "Alta Payment Response - Failed. " + transaction?.ShopOrderId,
                    Body = $"<p>Alta Payment Response - Failed<p><br />{HttpContext.Request.GetDisplayUrl()}<br />" + ex.ToString() + "<br><br> " + JsonConvert.SerializeObject(model),
                    IsBodyHtml = true,
                });
            }

            throw;
        }
    }

    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpGet, HttpPost]
    [Route("fail")]
    public async Task<IActionResult> Fail(PaymentResponse response)
    {
        _logger.LogInformation("Alta Payment Fail Response - Start");

        var model = response.GetApiResponse();

        _logger.LogDebug(JsonConvert.SerializeObject(model));


        if (model == null || !ModelState.IsValid)
        {
            _logger.LogDebug(JsonConvert.SerializeObject(ModelState));
            return BadRequest();
        }

        _logger.LogDebug("Alta Payment Fail Response - ModelState.IsValid");

        try
        {
            var transaction = model.Body.Transactions.First();
            if (!Guid.TryParse(transaction.ShopOrderId, out var orderId))
            {
                if (transaction.ShopOrderId == null)
                {
                    _logger.LogWarning("Alta Payment Fail Response - ShopOrderId is null");
                    return BadRequest();
                }

                var merchantRefParts = transaction.ShopOrderId.Split(';');
                var rawGuid = merchantRefParts.LastOrDefault();

                if (!Guid.TryParse(rawGuid, out Guid newOrderId))
                {
                    return BadRequest();
                }

                orderId = newOrderId;
            }

            _logger.LogInformation("Alta Payment Fail Response - OrderID: " + orderId);

            OrderStatus? order = await _orderService.GetAsync(orderId);
            if (order == null)
            {
                _logger.LogWarning("Alta Payment Fail Response - Unable to find order {OrderId}", orderId);

                return NotFound();
            }
            var paymentSettings = JsonConvert.DeserializeObject<PaymentSettings>(order.EkomPaymentSettingsData);

            paymentSettings.ErrorUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.ErrorUrl, _httpCtx.Request);

            _logger.LogInformation("Alta Payment Fail Response - Redirects to " + paymentSettings.ErrorUrl);

            return Redirect(paymentSettings.ErrorUrl.ToString());
        }
        catch (Exception ex)
        {
            var transaction = model.Body.Transactions.FirstOrDefault();
            _logger.LogError(ex, "Alta Payment Fail Response - Failed. " + transaction?.ShopOrderId);
            await Events.OnErrorAsync(this, new ErrorEventArgs
            {
                Exception = ex,
            });

            if (_settings.SendEmailAlerts)
            {
                await _mailSvc.SendAsync(new System.Net.Mail.MailMessage
                {
                    Subject = "Alta Payment Fail Response - Failed. " + transaction?.ShopOrderId,
                    Body = $"<p>Alta Payment Fail Response - Failed<p><br />{HttpContext.Request.GetDisplayUrl()}<br />" + ex.ToString() + "<br><br> " + JsonConvert.SerializeObject(model),
                    IsBodyHtml = true,
                });
            }

            throw;
        }
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

        return Ok(eventArgs.Template);
    }
}
