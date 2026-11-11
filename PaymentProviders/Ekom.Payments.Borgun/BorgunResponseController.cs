using Ekom.Payments.Helpers;
using Ekom.Payments.Providers;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Globalization;

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
    public async Task<IActionResult> Post([FromForm] Response borgunResponse)
    {
        _logger.LogInformation("Borgun Payment Response - Start");

        _logger.LogDebug(JsonConvert.SerializeObject(borgunResponse));

        try
        {
            _logger.LogDebug("Borgun Payment Response - ModelState.IsValid");


            if (!Guid.TryParse(borgunResponse.Reference, out var orderId))
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

            if (order.UniqueId != orderId
            || !PaymentRowGuard.BelongsTo(order, Payment._ppNodeName))
            {
                _logger.LogWarning("Borgun Payment Response - Order {OrderId} is not a Borgun payment", orderId);

                return NotFound();
            }

            if (order.Paid)
            {
                _logger.LogInformation("Borgun Payment Response - Already Paid");
                return Ok();
            }

            var paymentSettings = JsonConvert.DeserializeObject<PaymentSettings>(order.EkomPaymentSettingsData);

            ArgumentNullException.ThrowIfNull(paymentSettings);
            
            var borgunSettings = JsonConvert.DeserializeObject<BorgunSettings>(order.EkomPaymentProviderData);
            
            ArgumentNullException.ThrowIfNull(borgunSettings);

            if (string.IsNullOrWhiteSpace(borgunSettings.SecretCode)
            || string.IsNullOrWhiteSpace(borgunSettings.MerchantId))
            {
                _logger.LogError(
                    "Borgun Payment Response - Order {OrderId} has no Borgun secret code or merchant id, unable to verify",
                    orderId);

                await Events.OnErrorAsync(this, new ErrorEventArgs
                {
                    OrderStatus = order,
                });

                return BadRequest();
            }

            // Borgun returns the order id we sent with the payment request, the hash is only valid for that order
            var borgunOrderId = Payment.GetBorgunOrderId(paymentSettings, order.UniqueId);
            if (!string.Equals(borgunResponse.OrderId, borgunOrderId, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError(
                    "Borgun Payment Response - Order id does not match payment {OrderId}",
                    orderId);

                await Events.OnErrorAsync(this, new ErrorEventArgs
                {
                    OrderStatus = order,
                });

                return BadRequest();
            }

            var currencyFormat = new CultureInfo(paymentSettings.Currency, false).NumberFormat;

            var orderAmount = Payment.FormatPrice(order.Amount, borgunSettings);

            var orderhashcheck = CryptoHelpers.GetHMACSHA256(borgunSettings.SecretCode,
                new CheckHashMessage(borgunResponse.OrderId, orderAmount, paymentSettings.Currency).Message);

            _logger.LogInformation(
                "Borgun Payment Response - Validating orderId: {OrderId} and amount: {OrderAmount}",
                orderId,
                orderAmount);

            if (PaymentRowGuard.SignatureEquals(borgunResponse.OrderHash, orderhashcheck))
            {
                _logger.LogInformation("Borgun Payment Response - OrderHash Validation Success");

                if (!await PaymentRowGuard.TryMarkPaidAsync(_dbFac, order))
                {
                    _logger.LogInformation("Borgun Payment Response - Already Paid");
                    return Ok();
                }

                try
                {
                    var paymentData = new PaymentData
                    {
                        Id = order.UniqueId,
                        Date = DateTime.Now,
                        CustomData = borgunResponse.AuthorizationCode,
                        CardNumber = borgunResponse.CreditCardNumber,
                        Amount = order.Amount.ToString(CultureInfo.InvariantCulture),
                    };

                    await using var db = _dbFac.GetDatabase();
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

                await Events.OnSuccessAsync(this, new SuccessEventArgs
                {
                    OrderStatus = order,
                });

                _logger.LogInformation("Borgun Payment Response - SUCCESS");
                return Ok();
            }
            else
            {
                _logger.LogError(
                    "Borgun Payment Response - Failed hash verification for {OrderId}",
                    orderId
                );
                await Events.OnErrorAsync(this, new ErrorEventArgs
                {
                    OrderStatus = order,
                });

                return BadRequest();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Borgun Payment Response - Failed. " + borgunResponse.Reference);
            await Events.OnErrorAsync(this, new ErrorEventArgs
            {
                Exception = ex,
            });

            if (_settings.SendEmailAlerts)
            {
                await _mailSvc.SendAsync(new System.Net.Mail.MailMessage
                {
                    Subject = "Borgun Payment Response - Failed " + borgunResponse.Reference,
                    Body = $"<p>Borgun Payment Response - Failed<p><br />{HttpContext.Request.GetDisplayUrl()}<br />" + ex.ToString() + "<br><br> " + JsonConvert.SerializeObject(borgunResponse),
                    IsBodyHtml = true,
                });
            }

            throw;
        }
    }
}
