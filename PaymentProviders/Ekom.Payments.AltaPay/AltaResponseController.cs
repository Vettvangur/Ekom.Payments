using Ekom.Payments.AltaPay.Model;
using Ekom.Payments.Helpers;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

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

    /// <summary>
    /// ctor
    /// </summary>
    public AltaResponseController(
        ILogger<AltaResponseController> logger,
        PaymentsConfiguration settings,
        IOrderService orderService,
        IDatabaseFactory dbFac,
        IMailService mailSvc)
    {
        _logger = logger;
        _settings = settings;
        _orderService = orderService;
        _dbFac = dbFac;
        _mailSvc = mailSvc;
    }

    /// <summary>
    /// Receives a callback from Straumur when customer completes payment.
    /// Changes order status and optionally runs a custom callback provided by the application consuming this library.
    /// </summary>
    /// <param name="altaResp">Alta querystring parameters</param>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpGet, HttpPost]
    [Route("")]
    public async Task<IActionResult> Post([FromBody] PaymentCallback model)
    {
        _logger.LogInformation("Alta Payment Response - Start");

        _logger.LogDebug(JsonConvert.SerializeObject(model));

        if (model == null || !ModelState.IsValid)
        {
            _logger.LogDebug(JsonConvert.SerializeObject(ModelState));
            return BadRequest();
        }

        _logger.LogDebug("Alta Payment Response - ModelState.IsValid");

        if (model.PaymentStatus != PaymentStatus.SUCCEEDED)
        {
            return Ok("Payment not Valid");
        }

        try
        {
            if (!Guid.TryParse(model.Order.OrderId, out var orderId))
            {
                if(model.Order.OrderId == null)
                {
                    _logger.LogWarning("Alta Payment Response - MerchantReference is null");
                    return BadRequest();
                }

                var merchantRefParts = model.Order.OrderId.Split(';');
                var rawGuid = merchantRefParts.LastOrDefault();

                if (!Guid.TryParse(rawGuid, out Guid newOrderId))
                {
                    return BadRequest();
                }

                orderId = newOrderId;
            }

            _logger.LogInformation("Alta Payment Response - OrderID: " + orderId);

            OrderStatus? order = await _orderService.GetAsync(orderId);
            if (order == null)
            {
                _logger.LogWarning("Alta Payment Response - Unable to find order {OrderId}", orderId);

                return NotFound();
            }
            var paymentSettings = JsonConvert.DeserializeObject<PaymentSettings>(order.EkomPaymentSettingsData);
            var altaSettings = JsonConvert.DeserializeObject<AltaSettings>(order.EkomPaymentProviderData);

            if (!model.SessionId.Equals(altaSettings.SessionId) || !order.UniqueId.Equals(model.Order.OrderId))
            {
                _logger.LogInformation($"Alta Payment Response - Verification Error - Order ID: {order.UniqueId}");

                await Model.Events.OnErrorAsync(this, new ErrorEventArgs
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
                        CardNumber = $"**** **** **** {model.CardInformation.LastFourDigits}",
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

                await Model.Events.OnSuccessAsync(this, new SuccessEventArgs
                {
                    OrderStatus = order,
                });

                _logger.LogInformation($"Alta Payment Response - SUCCESS - Order ID: {order.UniqueId}");
            }
            else
            {
                _logger.LogInformation($"Alta Payment Response - SUCCESS - Previously validated");
            }

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Alta Payment Response - Failed. " + model.Order.OrderId);
            await Model.Events.OnErrorAsync(this, new ErrorEventArgs
            {
                Exception = ex,
            });

            if (_settings.SendEmailAlerts)
            {
                await _mailSvc.SendAsync(new System.Net.Mail.MailMessage
                {
                    Subject = "Alta Payment Response - Failed. " + model.Order.OrderId,
                    Body = $"<p>Alta Payment Response - Failed<p><br />{HttpContext.Request.GetDisplayUrl()}<br />" + ex.ToString() + "<br><br> " + JsonConvert.SerializeObject(model),
                    IsBodyHtml = true,
                });
            }

            throw;
        }

    }
}
