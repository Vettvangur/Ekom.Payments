using Ekom.Payments;
using Ekom.Payments.Helpers;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Net;
using System.Threading.Tasks;

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
    readonly IOrderService _orderService;
    readonly IDatabaseFactory _dbFac;
    readonly IMailService _mailSvc;

    /// <summary>
    /// ctor
    /// </summary>
    public NetgiroResponseController(
        ILogger<NetgiroResponseController> logger,
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
    /// Receives a callback from Netgiro when customer completes payment.
    /// Changes order status and optionally runs a custom callback provided by the application consuming this library.
    /// </summary>
    /// <param name="NetgiroResp">Netgiro querystring parameters</param>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpGet, HttpPost]
    [Route("")]
    public async Task<IActionResult> Post([FromQuery] Response NetgiroResp)
    {
        _logger.LogInformation("Netgiro Payment Response - Start");

        _logger.LogDebug(JsonConvert.SerializeObject(NetgiroResp));

        if (NetgiroResp != null && ModelState.IsValid)
        {
            try
            {
                _logger.LogDebug("Netgiro Payment Response - ModelState.IsValid");

                if (!Guid.TryParse(NetgiroResp.ReferenceNumber, out var orderId))
                {
                    return BadRequest();
                }

                _logger.LogInformation("Netgiro Payment Response - OrderID: " + orderId);

                OrderStatus? order = await _orderService.GetAsync(orderId);
                if (order == null)
                {
                    _logger.LogWarning("Netgiro Payment Response - Unable to find order {OrderId}", orderId);

                    return NotFound();
                }
                var paymentSettings = JsonConvert.DeserializeObject<PaymentSettings>(order.EkomPaymentSettingsData);
                var NetgiroSettings = JsonConvert.DeserializeObject<NetgiroSettings>(order.EkomPaymentProviderData);

                // perform payment provider specific security validation here
                bool isValid = true;

                if (isValid)
                {
                    _logger.LogInformation("Netgiro Payment Response - isValid");

                    if (!order.Paid)
                    {
                        try
                        {
                            var paymentData = new PaymentData
                            {
                                Id = order.UniqueId,
                                Date = DateTime.Now,
                                CardNumber = NetgiroResp.CardNumberMasked,
                                CustomData = JsonConvert.SerializeObject(NetgiroResp),
                                Amount = order.Amount.ToString(),
                            };

                            using var db = _dbFac.GetDatabase();
                            await db.InsertOrReplaceAsync(paymentData);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Netgiro Payment Response - Error saving payment data");
                        }

                        order.Paid = true;

                        using (var db = _dbFac.GetDatabase())
                        {
                            await db.UpdateAsync(order);
                        }

                        Events.OnSuccess(this, new SuccessEventArgs
                        {
                            OrderStatus = order,
                        });
                        _logger.LogInformation($"Netgiro Payment Response - SUCCESS - Order ID: {order.UniqueId}");
                    }
                    else
                    {
                        _logger.LogInformation($"Netgiro Payment Response - SUCCESS - Previously validated");
                    }

                    var successUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.SuccessUrl.ToString(), Request);
                    return Redirect(successUrl.ToString());
                }
                else
                {
                    _logger.LogInformation($"Netgiro Payment Response - Verification Error - Order ID: {order.UniqueId}");
                    Events.OnError(this, new ErrorEventArgs
                    {
                        OrderStatus = order,
                    });

                    var cancelUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.CancelUrl.ToString(), Request);
                    return Redirect(cancelUrl.ToString());
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Netgiro Payment Response - Failed");
                Events.OnError(this, new ErrorEventArgs
                {
                    Exception = ex,
                });

                if (_settings.SendEmailAlerts)
                {
                    await _mailSvc.SendAsync(new System.Net.Mail.MailMessage
                    {
                        Subject = "Netgiro Payment Response - Failed",
                        Body = $"<p>Netgiro Payment Response - Failed<p><br />{HttpContext.Request.GetDisplayUrl()}<br />" + ex.ToString(),
                        IsBodyHtml = true,
                    });
                }

                throw;
            }
        }

        _logger.LogDebug(JsonConvert.SerializeObject(ModelState));
        return Redirect("/");
    }
}
