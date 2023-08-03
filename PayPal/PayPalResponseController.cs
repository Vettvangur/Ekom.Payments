using Ekom.Payments.Helpers;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Net;
using System.Text;

namespace Ekom.Payments.PayPal;

/// <summary>
/// Receives a callback from PayPal when customer completes payment.
/// Changes order status and optionally runs a custom callback provided by the application consuming this library.
/// </summary>
[Route("ekom/payments/[controller]")]
[ApiController]
public class PayPalResponseController : ControllerBase
{
    readonly ILogger _logger;
    readonly PaymentsConfiguration _settings;
    readonly IOrderService _orderService;
    readonly IDatabaseFactory _dbFac;
    readonly IMailService _mailSvc;

    /// <summary>
    /// ctor
    /// </summary>
    public PayPalResponseController(
        ILogger<PayPalResponseController> logger,
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
    /// Receives a callback from PayPal when customer completes payment.
    /// Changes order status and optionally runs a custom callback provided by the application consuming this library.
    /// </summary>
    /// <param name="PayPalResponse">PayPal querystring parameters</param>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpGet, HttpPost]
    [Route("")]
    public async Task<IActionResult> Post([FromQuery] Response PayPalResponse)
    {
        _logger.LogInformation("PayPal Payment Response - Start");

        _logger.LogDebug(JsonConvert.SerializeObject(PayPalResponse));

        try
        {
            _logger.LogDebug("PayPal Payment Response - ModelState.IsValid");

            if (!Guid.TryParse(PayPalResponse.Custom, out var orderId))
            {
                return BadRequest();
            }

            _logger.LogInformation("PayPal Payment Response - {OrderID}", orderId);

            OrderStatus? orderStatus = await _orderService.GetAsync(orderId).ConfigureAwait(false);
            if (orderStatus == null)
            {
                _logger.LogWarning("PayPal Payment Response - Unable to find order {OrderId}", orderId);

                return NotFound();
            }
            var paymentSettings = JsonConvert.DeserializeObject<PaymentSettings>(orderStatus.EkomPaymentSettingsData);
            
            if (orderStatus.EkomPaymentProviderData == null)
            {
                _logger.LogWarning("PayPal Payment Response - Unable to find order {dataName} from PayPal for order {OrderId}", nameof(orderStatus.EkomPaymentProviderData), orderId);
                throw new ArgumentNullException(nameof(orderStatus.EkomPaymentProviderData));
            }
            var paypalSettings = JsonConvert.DeserializeObject<PayPalSettings>(orderStatus.EkomPaymentProviderData);

            bool validateIPN = await VerifyTask(paypalSettings).ConfigureAwait(false);

            if (validateIPN)
            {
                _logger.LogInformation("PayPal Payment Response - isValid");

                if (!orderStatus.Paid)
                {
                    try
                    {
                        var paymentData = new PaymentData
                        {
                            Id = orderStatus.UniqueId,
                            Date = DateTime.Now,
                            PaymentMethod = "PayPal",
                            CustomData = JsonConvert.SerializeObject(PayPalResponse),
                            Amount = orderStatus.Amount.ToString("F2", paypalSettings.CultureInfo),
                        };

                        using var db = _dbFac.GetDatabase();
                        await db.InsertOrReplaceAsync(paymentData);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "PayPal Payment Response - Error saving payment data");
                        if (_settings.SendEmailAlerts)
                        {
#pragma warning disable CA2000 // Handled by mail service
                            await _mailSvc.SendAsync(new System.Net.Mail.MailMessage
                            {
                                Subject = "PayPal Payment Response - Error saving payment data",
                                Body = $"<p>PayPal Payment Response - Error saving payment data<p><br />{HttpContext.Request.GetDisplayUrl()}<br />" + ex.ToString(),
                                IsBodyHtml = true,
                            });
#pragma warning restore CA2000 // Dispose objects before losing scope
                        }
                    }

                    orderStatus.Paid = true;

                    using (var db = _dbFac.GetDatabase())
                    {
                        await db.UpdateAsync(orderStatus);
                    }

                    Events.OnSuccess(this, new SuccessEventArgs
                    {
                        OrderStatus = orderStatus,
                    });
                    _logger.LogInformation($"PayPal Payment Response - SUCCESS - Order ID: {orderStatus.UniqueId}");
                }
                else
                {
                    _logger.LogInformation($"PayPal Payment Response - SUCCESS - Previously validated");
                }

                var successUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.SuccessUrl.ToString(), Request);
                return Redirect(successUrl.ToString());
            }
            else
            {
                _logger.LogInformation($"PayPal Payment Response - Verification Error - Order ID: {orderStatus.UniqueId}");
                Events.OnError(this, new ErrorEventArgs
                {
                    OrderStatus = orderStatus,
                });

                var cancelUrl = PaymentsUriHelper.EnsureFullUri(paymentSettings.CancelUrl.ToString(), Request);
                return Redirect(cancelUrl.ToString());
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PayPal Payment Response - Failed");
            Events.OnError(this, new ErrorEventArgs
            {
                Exception = ex,
            });

            if (_settings.SendEmailAlerts)
            {
                await _mailSvc.SendAsync(new System.Net.Mail.MailMessage
                {
                    Subject = "PayPal Payment Response - Failed",
                    Body = $"<p>PayPal Payment Response - Failed<p><br />{HttpContext.Request.GetDisplayUrl()}<br />" + ex.ToString(),
                    IsBodyHtml = true,
                });
            }

            throw;
        }
    }

    private async Task<bool> VerifyTask(PayPalSettings paypalSettings)
    {
        var verificationResponse = string.Empty;

        try
        {
            HttpWebRequest verificationRequest;

            //Set values for the verification request
            verificationRequest.Method = "POST";
            verificationRequest.ContentType = "application/x-www-form-urlencoded";
            var param = Request.BinaryRead(ipnRequest.ContentLength);
            var strRequest = Encoding.ASCII.GetString(param);

            //Add cmd=_notify-validate to the payload
            strRequest = "cmd=_notify-validate&" + strRequest;
            verificationRequest.ContentLength = strRequest.Length;

            //Attach payload to the verification request
            var streamOut = new StreamWriter(verificationRequest.GetRequestStream(), Encoding.ASCII);
            streamOut.Write(strRequest);
            streamOut.Close();

            //Send the request to PayPal and get the response
            var streamIn = new StreamReader(verificationRequest.GetResponse().GetResponseStream());
            verificationResponse = streamIn.ReadToEnd();
            streamIn.Close();

        }
        catch (Exception exception)
        {
            //Capture exception for manual investigation
        }

        ProcessVerificationResponse(verificationResponse);
    }
}
