using Ekom.Payments.Helpers;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Net;

namespace Ekom.Payments.Straumur;

/// <summary>
/// Receives a callback from Straumur when customer completes payment.
/// Changes order status and optionally runs a custom callback provided by the application consuming this library.
/// </summary>
[Route("ekom/payments/[controller]")]
[ApiController]
public class StraumurResponseController : ControllerBase
{
    readonly ILogger _logger;
    readonly PaymentsConfiguration _settings;
    readonly IOrderService _orderService;
    readonly IDatabaseFactory _dbFac;
    readonly IMailService _mailSvc;

    /// <summary>
    /// ctor
    /// </summary>
    public StraumurResponseController(
        ILogger<StraumurResponseController> logger,
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
    /// <param name="straumurResp">Straumur querystring parameters</param>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpGet, HttpPost]
    [Route("")]
    public async Task<IActionResult> Post([FromBody]Response straumurResp)
    {
        _logger.LogInformation("Straumur Payment Response - Start");

        _logger.LogDebug(JsonConvert.SerializeObject(straumurResp));

        if (straumurResp != null && ModelState.IsValid)
        {
            try
            {
                _logger.LogDebug("Straumur Payment Response - ModelState.IsValid");

                if (!Guid.TryParse(straumurResp.MerchantReference, out var orderId))
                {
                    if(straumurResp.MerchantReference == null)
                    {
                        _logger.LogWarning("Straumur Payment Response - MerchantReference is null");
                        return BadRequest();
                    }

                    var merchantRefParts = straumurResp.MerchantReference.Split(';');
                    var rawGuid = merchantRefParts.LastOrDefault();

                    if (!Guid.TryParse(rawGuid, out Guid newOrderId))
                    {
                        return BadRequest();
                    }

                    orderId = newOrderId;
                }

                if (straumurResp.Success == "false")
                {
                    return Ok("Payment not Valid");
                }

                _logger.LogInformation("Straumur Payment Response - OrderID: " + orderId);

                OrderStatus? order = await _orderService.GetAsync(orderId);
                if (order == null)
                {
                    _logger.LogWarning("Straumur Payment Response - Unable to find order {OrderId}", orderId);

                    return NotFound();
                }
                var paymentSettings = JsonConvert.DeserializeObject<PaymentSettings>(order.EkomPaymentSettingsData);
                var straumurSettings = JsonConvert.DeserializeObject<StraumurSettings>(order.EkomPaymentProviderData);

                var hmacKey = straumurSettings.HmacKey;
                var calculatedSignature = StraumurResponseHelper.GetHmacSignature(hmacKey, straumurResp);

                if (straumurResp.HmacSignature == calculatedSignature)
                {
                    _logger.LogInformation("Straumur Payment Response - DigitalSignatureResponse Verified");

                    if (!order.Paid)
                    {
                        try
                        {
                            var paymentData = new PaymentData
                            {
                                Id = order.UniqueId,
                                Date = DateTime.Now,
                                CardNumber = straumurResp.AdditionalData.CardNumber,
                                CustomData = JsonConvert.SerializeObject(straumurResp),
                                Amount = order.Amount.ToString(),
                            };

                            using var db = _dbFac.GetDatabase();
                            await db.InsertOrReplaceAsync(paymentData);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Straumur Payment Response - Error saving payment data");
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

                        _logger.LogInformation($"Straumur Payment Response - SUCCESS - Order ID: {order.UniqueId}");
                    }
                    else
                    {
                        _logger.LogInformation($"Straumur Payment Response - SUCCESS - Previously validated");
                    }

                    return Ok();
                }
                else
                {
                    _logger.LogInformation($"Straumur Payment Response - Verification Error - Order ID: {order.UniqueId}");

                    await Events.OnErrorAsync(this, new ErrorEventArgs
                    {
                        OrderStatus = order,
                    });

                    var cancelUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.CancelUrl, Request);

                    return StatusCode(500);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Straumur Payment Response - Failed. " + straumurResp.MerchantReference);
                await Events.OnErrorAsync(this, new ErrorEventArgs
                {
                    Exception = ex,
                });

                if (_settings.SendEmailAlerts)
                {
                    await _mailSvc.SendAsync(new System.Net.Mail.MailMessage
                    {
                        Subject = "Straumur Payment Response - Failed. " + straumurResp.MerchantReference,
                        Body = $"<p>Straumur Payment Response - Failed<p><br />{HttpContext.Request.GetDisplayUrl()}<br />" + ex.ToString(),
                        IsBodyHtml = true,
                    });
                }

                throw;
            }
        }

        _logger.LogDebug(JsonConvert.SerializeObject(ModelState));

        return BadRequest();
    }
}
