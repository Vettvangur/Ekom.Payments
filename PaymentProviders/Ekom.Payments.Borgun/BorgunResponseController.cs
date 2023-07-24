using Ekom.Payments;
using Ekom.Payments.Helpers;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Globalization;
using System.Net;
using System.Threading.Tasks;

namespace Ekom.Payments.Borgun;

/// <summary>
/// Receives a callback from Borgun when customer completes payment.
/// Changes order status and optionally runs a custom callback provided by the application consuming this library.
/// </summary>
[Route("ekom/payments/[controller]")]
[ApiController]
public class BorgunResponseController : ControllerBase
{
    readonly ILogger _logger;
    readonly PaymentsConfiguration _settings;
    readonly IOrderService _orderService;
    readonly IDatabaseFactory _dbFac;
    readonly IMailService _mailSvc;

    /// <summary>
    /// ctor
    /// </summary>
    public BorgunResponseController(
        ILogger<BorgunResponseController> logger,
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
    /// Receives a callback from Borgun when customer completes payment.
    /// Changes order status and optionally runs a custom callback provided by the application consuming this library.
    /// </summary>
    /// <param name="borgunResponse">Borgun querystring parameters</param>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpGet, HttpPost]
    [Route("")]
    public async Task<IActionResult> Post([FromQuery] Response borgunResponse)
    {
        _logger.LogInformation("Borgun Payment Response - Start");

        _logger.LogDebug(JsonConvert.SerializeObject(borgunResponse));

        try
        {
            _logger.LogDebug("Borgun Payment Response - ModelState.IsValid");

            if (!Guid.TryParse(borgunResponse.OrderId, out var orderId))
            {
                return BadRequest();
            }

            _logger.LogInformation("Borgun Payment Response - {OrderID}", orderId);

            OrderStatus? order = await _orderService.GetAsync(orderId);
            if (order == null)
            {
                _logger.LogWarning("Borgun Payment Response - Unable to find order {OrderId}", orderId);

                return NotFound();
            }

            if (order.Paid)
            {
                _logger.LogInformation("Borgun Payment Response - Already Paid");
                return Ok();
            }

            var paymentSettings = JsonConvert.DeserializeObject<PaymentSettings>(order.EkomPaymentSettingsData);
            var borgunSettings = JsonConvert.DeserializeObject<BorgunSettings>(order.EkomPaymentProviderData);

            var currencyFormat = new CultureInfo(paymentSettings.Currency, false).NumberFormat;

            string orderAmount = order.Amount.ToString("#.00", currencyFormat);

            string orderhashcheck = CryptoHelpers.GetHMACSHA256(borgunSettings.SecretCode,
                new CheckHashMessage(borgunResponse.OrderId, orderAmount, paymentSettings.Currency).Message);

            _logger.LogInformation(
                "Borgun Payment Response - Validating orderid: {OrderId} and amount: {OrderAmount}",
                orderId,
                orderAmount);

            if (string.Compare(borgunResponse.OrderHash, orderhashcheck, true) == 0)
            {
                _logger.LogInformation("Borgun Payment Response - OrderHash Validation Success");

                try
                {
                    var paymentData = new PaymentData
                    {
                        Id = order.UniqueId,
                        Date = DateTime.Now,
                        CustomData = borgunResponse.AuthorizationCode,
                        CardNumber = borgunResponse.CreditCardNumber,
                        Amount = order.Amount.ToString(),
                    };

                    using var db = _dbFac.GetDatabase();
                    await db.InsertOrReplaceAsync(paymentData);
                }
                // Intended to ward in case of breaking schema changes,
                // at this point customer has paid so in order of importance,
                // this step is lower than the ones after
#pragma warning disable CA1031 // We could be more exact in the sql issues we might face but this will do
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Borgun Payment Response - Error saving payment data");
                }
#pragma warning restore CA1031 // Do not catch general exception types

                order.Paid = true;

                using (var db = _dbFac.GetDatabase())
                {
                    await db.UpdateAsync(order);
                }

                Events.OnSuccess(this, new SuccessEventArgs
                {
                    OrderStatus = order,
                });

                _logger.LogInformation("Borgun Payment Response - SUCCESS");
                return Ok();
            }
            else
            {
                _logger.LogError(
                    "Borgun Payment Response - Failed hash verification: {ReceivedOrderHash} {ComputedOrderhash}",
                    borgunResponse.OrderHash,
                    orderhashcheck
                );
                Events.OnError(this, new ErrorEventArgs
                {
                    OrderStatus = order,
                });

                return BadRequest();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Borgun Payment Response - Failed");
            Events.OnError(this, new ErrorEventArgs
            {
                Exception = ex,
            });

            if (_settings.SendEmailAlerts)
            {
                await _mailSvc.SendAsync(new System.Net.Mail.MailMessage
                {
                    Subject = "Borgun Payment Response - Failed",
                    Body = $"<p>Borgun Payment Response - Failed<p><br />{HttpContext.Request.GetDisplayUrl()}<br />" + ex.ToString(),
                    IsBodyHtml = true,
                });
            }

            throw;
        }
    }
}
