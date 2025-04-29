using Ekom.Payments.Helpers;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Globalization;

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
    /// <param name="netgiroResponse">Netgiro querystring parameters</param>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost]
    [Route("")]
    public async Task<IActionResult> Post([FromBody] Response netgiroResponse)
    {
        _logger.LogInformation("Netgiro Payment Response - Start");

        _logger.LogDebug("{NetgiroResponse}", netgiroResponse);

        try
        {
            _logger.LogDebug("Netgiro Payment Response - ModelState.IsValid");

            if (netgiroResponse.ReferenceNumber == Guid.Empty)
            {
                return BadRequest();
            }

            _logger.LogInformation("Netgiro Payment Response - ReferenceNumber: " + netgiroResponse.ReferenceNumber);

            OrderStatus? order = await _orderService.GetAsync(netgiroResponse.ReferenceNumber);
            if (order == null)
            {
                _logger.LogWarning("Netgiro Payment Response - Unable to find order {ReferenceNumber}", netgiroResponse.ReferenceNumber);

                return NotFound();
            }

            if (order.Paid)
            {
                _logger.LogInformation("Netgiro Payment Response - Already Paid");
                return Ok();
            }

            var paymentSettings = JsonConvert.DeserializeObject<PaymentSettings>(order.EkomPaymentSettingsData);
            var netgiroSettings = JsonConvert.DeserializeObject<NetgiroSettings>(order.EkomPaymentProviderData);

            var currencyFormat = new CultureInfo(paymentSettings.Currency, false).NumberFormat;

            var sig = CryptoHelpers.GetSHA256HexStringSum(
                Payment.CombineSignature(
                    netgiroSettings.Secret,
                    netgiroResponse.ReferenceNumber.ToString(),
                    netgiroResponse.ConfirmationCode,
                    netgiroResponse.InvoiceNumber));

            if (sig.Equals(netgiroResponse.Signature, StringComparison.InvariantCultureIgnoreCase))
            {
                _logger.LogInformation("Netgiro Payment Response Hit - PaymentSuccessful");

                try
                {
                    var paymentData = new PaymentData
                    {
                        Id = order.UniqueId,
                        Date = DateTime.Now,
                        CustomData = netgiroResponse.InvoiceNumber,
                        Amount = order.Amount.ToString(currencyFormat),
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
                    _logger.LogError(ex, "Netgiro Payment Response - Error saving payment data");
                }
#pragma warning restore CA1031 // Do not catch general exception types

                order.Paid = true;

                using (var db = _dbFac.GetDatabase())
                {
                    await db.UpdateAsync(order);
                }

                await Events.OnSuccessAsync(this, new SuccessEventArgs
                {
                    OrderStatus = order,
                });

                _logger.LogInformation("Netgiro Payment Response - SUCCESS");
                return Ok();
            }
            else
            {
                await Events.OnErrorAsync(this, new ErrorEventArgs
                {
                    OrderStatus = order,
                });
                _logger.LogError(
                    "Netgiro Payment Response - Failed verification for {ReferenceNumber}",
                    netgiroResponse.ReferenceNumber
                );

                return BadRequest();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Netgiro Payment Response - Failed");
            await Events.OnErrorAsync(this, new ErrorEventArgs
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
}
