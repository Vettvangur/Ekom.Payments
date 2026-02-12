using Ekom.Payments.Helpers;
using Ekom.Payments.SiminnPay.Model;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Globalization;
using System.Net.Mail;

namespace Ekom.Payments.SiminnPay;

/// <summary>
/// Receives a callback from Straumur when customer completes payment.
/// Changes order status and optionally runs a custom callback provided by the application consuming this library.
/// </summary>
[Route("ekom/payments/[controller]")]
[ApiController]
public class SiminnPayResponseController : ControllerBase
{
    readonly ILogger _logger;
    readonly PaymentsConfiguration _settings;
    readonly IOrderService _orderService;
    readonly IDatabaseFactory _dbFac;
    readonly IMailService _mailSvc;
    readonly HttpContext _httpCtx;
    readonly IUmbracoService _uService;

    /// <summary>
    /// ctor
    /// </summary>
    public SiminnPayResponseController(
        ILogger<SiminnPayResponseController> logger,
        PaymentsConfiguration settings,
        IOrderService orderService,
        IDatabaseFactory dbFac,
        IMailService mailSvc,
        IHttpContextAccessor httpContext,
        IUmbracoService uService)
    {
        _logger = logger;
        _settings = settings;
        _orderService = orderService;
        _dbFac = dbFac;
        _mailSvc = mailSvc;
        _httpCtx = httpContext.HttpContext ?? throw new NotSupportedException("Payment requests require an httpcontext");
        _uService = uService;
    }

    /// <summary>
    /// Receives a callback from Straumur when customer completes payment.
    /// Changes order status and optionally runs a custom callback provided by the application consuming this library.
    /// </summary>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpGet, HttpPost]
    [Route("")]
    public async Task<IActionResult> Post(SiminnPayOrderStatus notificationCallBack)
    {
        _logger.LogInformation("SiminnPay Response Hit - OrderKey: {OrderKey}", notificationCallBack?.OrderKey);

        if (!ModelState.IsValid || notificationCallBack == null)
        {
            return BadRequest();
        }

        try
        {
            OrderStatus? order = await _orderService.GetAsync(notificationCallBack.OrderKey);
            if (order == null)
            {
                _logger.LogWarning("SiminnPay Response - Unable to find order {OrderKey}", notificationCallBack.OrderKey);

                return NotFound();
            }
            if (order.Paid)
            {
                return Ok();
            }

            var paymentSettings = JsonConvert.DeserializeObject<PaymentSettings>(order.EkomPaymentSettingsData);
            var siminnPaySettings = JsonConvert.DeserializeObject<SiminnPaySettings>(order.EkomPaymentProviderData);

            string body = notificationCallBack.OrderKey.ToString() +
                    ((int)notificationCallBack.Amount) +
                    notificationCallBack.ExpiresAt.ToString("dd.MM.yyyy HH:mm:ss");

            var signature = CryptoHelpers.GetHMACSHA256(siminnPaySettings.Secret, body);

            if (!notificationCallBack.HMAC.Equals(signature, StringComparison.InvariantCultureIgnoreCase))
            {
                _logger.LogDebug("HMAC body: {Body}", body);
                _logger.LogDebug("Posted HMAC: {HMAC}", notificationCallBack.HMAC);
                _logger.LogDebug("Generated HMAC: {Signature}", signature);

                _logger.LogWarning("Signature mismatch, unauthorized order update request");

                await Model.Events.OnErrorAsync(this, new ErrorEventArgs
                {
                    OrderStatus = order,
                });
                return Unauthorized();
            }

            _logger.LogInformation("SiminnPay Response Hit - Signature verified successfully - notificationCallBack.Status: " + notificationCallBack.Status);

            if (order.Paid == false)
            {
                try
                {
                    var currencyFormat = new CultureInfo(paymentSettings.Currency, false).NumberFormat;
                    var paymentData = new PaymentData
                    {
                        Id = order.UniqueId,
                        Date = DateTime.Now,
                        CustomData = notificationCallBack.TransactionDetails.TransactionId,
                        Amount = order.Amount.ToString(currencyFormat),
                    };

                    using var db = _dbFac.GetDatabase();
                    await db.InsertOrReplaceAsync(paymentData);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "SiminnPay Response - Error saving payment data");
                }


                if (notificationCallBack.Status == SiminnPayStatus.PaymentSuccessful)
                {
                    order.Paid = true;
                    using (var db = _dbFac.GetDatabase())
                    {
                        await db.UpdateAsync(order);
                    }

                    await Model.Events.OnSuccessAsync(this, new SuccessEventArgs
                    {
                        OrderStatus = order,
                    });

                    _logger.LogDebug("SiminnPay Response Hit - SUCCESS");
                }
                else
                {
                    await Model.Events.OnErrorAsync(this, new ErrorEventArgs
                    {
                        OrderStatus = order,
                    });
                }
            }
            
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Síminn Pay Response Error");
            await Model.Events.OnErrorAsync(this, new ErrorEventArgs
            {
                Exception = ex,
            });

            if (_settings.SendEmailAlerts)
            {
                await _mailSvc.SendAsync(new MailMessage
                {
                    Subject = "Síminn Pay Response - Failed",
                    Body = "<p>Síminn Pay Response - Failed<p><br />" + ex,
                    IsBodyHtml = true,
                });
            }

            throw;
        }
    }

    [HttpGet("status")]
    public async Task<ActionResult> StatusAsync(Guid siminnPayOrderKey)
    {
        _logger.LogDebug("SiminnPay Status Requested - siminnPayOrderKey: {SiminnPayOrderKey}", siminnPayOrderKey);

        if (siminnPayOrderKey == Guid.Empty)
        {
            return BadRequest("Missing siminnPayOrderKey parameter.");
        }

        OrderStatus? order = await _orderService.GetByCustomAsync(siminnPayOrderKey.ToString());
        if (order == null)
        {
            return NotFound("Pay order not found.");
        }

        var paymentSettings = order.EkomPaymentSettings;
        var siminnPaySettings = paymentSettings.CustomSettings.ContainsKey(typeof(SiminnPaySettings))
                ? paymentSettings.CustomSettings[typeof(SiminnPaySettings)] as SiminnPaySettings
                : new SiminnPaySettings();

        _uService.PopulatePaymentProviderProperties(
            paymentSettings,
            Payment._ppNodeName,
            siminnPaySettings,
            SiminnPaySettings.Properties);

        var svc = new SiminnPayService(siminnPaySettings.ApiKey, siminnPaySettings.ApiUrl, _logger);
        var initialStatus = await svc.GetStatus(siminnPayOrderKey);

        _logger.LogDebug("SiminnPay Status Requested - Status: {Status}", initialStatus.Status);

        if (initialStatus.Status == SiminnPayStatus.Expired
        || initialStatus.Status == SiminnPayStatus.CancelledByCustomer)
        {
            _logger.LogDebug("SiminnPay Status Requested - Status expired or cancelled, updated siminnPayOrder");
        }

        return new JsonResult(new SiminnPayStatusView(order, initialStatus));
    }
}
