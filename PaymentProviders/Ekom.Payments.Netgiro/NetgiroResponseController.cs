using Ekom.Payments.Helpers;
using Ekom.Payments.Providers;
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
    /// <param name="netgiroResponse">Netgiro query-string or form parameters</param>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpGet, HttpPost]
    [Route("")]
    public async Task<IActionResult> Post([ModelBinder(BinderType = typeof(NetgiroResponseModelBinder))] Response netgiroResponse)
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

            if (order.UniqueId != netgiroResponse.ReferenceNumber
            || !PaymentRowGuard.BelongsTo(order, Payment._ppNodeName))
            {
                _logger.LogWarning(
                    "Netgiro Payment Response - Order {ReferenceNumber} is not a Netgiro payment",
                    netgiroResponse.ReferenceNumber);

                return NotFound();
            }

            if (order.Paid)
            {
                _logger.LogInformation("Netgiro Payment Response - Already Paid");
                return Ok();
            }

            var paymentSettings = JsonConvert.DeserializeObject<PaymentSettings>(order.EkomPaymentSettingsData);
            var netgiroSettings = JsonConvert.DeserializeObject<NetgiroSettings>(order.EkomPaymentProviderData);

            if (paymentSettings == null
            || netgiroSettings == null
            || string.IsNullOrWhiteSpace(netgiroSettings.Secret)
            || netgiroSettings.ApplicationId == Guid.Empty)
            {
                _logger.LogError(
                    "Netgiro Payment Response - Order {ReferenceNumber} has no Netgiro secret or application id, unable to verify",
                    netgiroResponse.ReferenceNumber);

                await Events.OnErrorAsync(this, new ErrorEventArgs
                {
                    OrderStatus = order,
                });

                return BadRequest();
            }

            var currencyFormat = new CultureInfo(paymentSettings.Currency, false).NumberFormat;

            if (IsVerified(netgiroResponse, order, netgiroSettings, out var rejection))
            {
                _logger.LogInformation("Netgiro Payment Response Hit - PaymentSuccessful");

                if (string.IsNullOrEmpty(netgiroResponse.NetgiroSignature))
                {
                    _logger.LogWarning(
                        "Netgiro Payment Response - {ReferenceNumber} verified without NetgiroSignature, amount and status were not part of the signature",
                        netgiroResponse.ReferenceNumber);
                }

                if (!await PaymentRowGuard.TryMarkPaidAsync(_dbFac, order))
                {
                    _logger.LogInformation("Netgiro Payment Response - Already Paid");
                    return Ok();
                }

                try
                {
                    var paymentData = new PaymentData
                    {
                        Id = order.UniqueId,
                        Date = DateTime.Now,
                        CustomData = netgiroResponse.InvoiceNumber,
                        Amount = order.Amount.ToString(currencyFormat),
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
                    _logger.LogError(ex, "Netgiro Payment Response - Error saving payment data");
                }
#pragma warning restore CA1031 // Do not catch general exception types

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
                    "Netgiro Payment Response - Failed verification for {ReferenceNumber}: {Reason}",
                    netgiroResponse.ReferenceNumber,
                    rejection
                );

                return BadRequest();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Netgiro Payment Response - Failed. " + netgiroResponse?.ReferenceNumber);

            await Events.OnErrorAsync(this, new ErrorEventArgs
            {
                Exception = ex,
            });

            if (_settings.SendEmailAlerts)
            {
                await _mailSvc.SendAsync(new System.Net.Mail.MailMessage
                {
                    Subject = "Netgiro Payment Response - Failed. " + netgiroResponse?.ReferenceNumber,
                    Body = $"<p>Netgiro Payment Response - Failed<p><br />{HttpContext.Request.GetDisplayUrl()}<br />" + ex.ToString() + "<br><br> " + JsonConvert.SerializeObject(netgiroResponse),
                    IsBodyHtml = true,
                });
            }

            throw;
        }
    }

    /// <summary>
    /// Verifies a callback against the payment row it references. <br />
    /// https://netgiro.github.io/docs/iframe-and-http-post/http-post-request/ <br />
    /// NetgiroSignature = SHA256(SecretKey + ReferenceNumber + TransactionId + InvoiceNumber + TotalAmount + Status) <br />
    /// Signature = SHA256(SecretKey + ReferenceNumber + ConfirmationCode + InvoiceNumber), used when NetgiroSignature is not sent.
    /// </summary>
    internal static bool IsVerified(
        Response netgiroResponse,
        OrderStatus order,
        NetgiroSettings netgiroSettings,
        out string rejection)
    {
        var referenceNumber = order.UniqueId.ToString();
        string received;
        string expected;

        if (!IsNumeric(netgiroResponse.InvoiceNumber))
        {
            rejection = "InvoiceNumber missing or not numeric";
            return false;
        }

        if (!string.IsNullOrEmpty(netgiroResponse.NetgiroSignature))
        {
            if (string.IsNullOrEmpty(netgiroResponse.TransactionId))
            {
                rejection = "TransactionId missing";
                return false;
            }

            // 1 - unconfirmed, the payment is confirmed by our reply to this callback. 2 - confirmed. 5 - canceled
            if (netgiroResponse.Status != "1" && netgiroResponse.Status != "2")
            {
                rejection = "Status is not unconfirmed or confirmed";
                return false;
            }

            if (!IsNumeric(netgiroResponse.TotalAmount)
            || !decimal.TryParse(netgiroResponse.TotalAmount, NumberStyles.None, CultureInfo.InvariantCulture, out var totalAmount)
            || totalAmount != decimal.Parse(Payment.FormatPrice(order.Amount), CultureInfo.InvariantCulture))
            {
                rejection = "TotalAmount does not match the order";
                return false;
            }

            received = netgiroResponse.NetgiroSignature;
            expected = CryptoHelpers.GetSHA256HexStringSum(
                Payment.CombineSignature(
                    netgiroSettings.Secret,
                    referenceNumber,
                    netgiroResponse.TransactionId,
                    netgiroResponse.InvoiceNumber,
                    netgiroResponse.TotalAmount,
                    netgiroResponse.Status));
        }
        else
        {
            if (string.IsNullOrEmpty(netgiroResponse.ConfirmationCode))
            {
                rejection = "ConfirmationCode missing";
                return false;
            }

            received = netgiroResponse.Signature;
            expected = CryptoHelpers.GetSHA256HexStringSum(
                Payment.CombineSignature(
                    netgiroSettings.Secret,
                    referenceNumber,
                    netgiroResponse.ConfirmationCode,
                    netgiroResponse.InvoiceNumber));
        }

        if (!PaymentRowGuard.SignatureEquals(received, expected))
        {
            rejection = "Signature mismatch";
            return false;
        }

        // The signature we send with the payment request is not a payment confirmation
        if (PaymentRowGuard.SignatureEquals(received, Payment.RequestSignature(netgiroSettings, order.UniqueId, order.Amount)))
        {
            rejection = "Signature is the payment request signature";
            return false;
        }

        rejection = string.Empty;
        return true;
    }

    static bool IsNumeric(string? value)
        => !string.IsNullOrEmpty(value) && value.All(x => x is >= '0' and <= '9');
}
