using Ekom.Payments.PayTrail.Models;
using LinqToDB;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Net.Mail;

namespace Ekom.Payments.PayTrail;

/// <summary>
/// Receives a callback from PayTrail when customer completes payment.
/// </summary>
[Route("ekom/payments/[controller]")]
[ApiController]
public class PayTrailResponseController : ControllerBase
{
    readonly ILogger _logger;
    readonly PaymentsConfiguration _settings;
    readonly IOrderService _orderService;
    readonly IDatabaseFactory _dbFac;
    readonly IMailService _mailSvc;

    public PayTrailResponseController(
        ILogger<PayTrailResponseController> logger,
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

    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpGet, HttpPost]
    [Route("")]
    public async Task<IActionResult> Post()
    {
        var parameters = Request.Query.ToDictionary(x => x.Key, x => x.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        var isCallback = parameters.ContainsKey("callback");

        _logger.LogInformation("PayTrail Payment Response - Start");
        _logger.LogDebug(JsonConvert.SerializeObject(parameters));

        try
        {
            var callback = CreateCallback(parameters);
            if (callback == null || !Guid.TryParse(callback.Stamp, out var orderId))
            {
                return BadRequest();
            }

            var order = await _orderService.GetAsync(orderId).ConfigureAwait(false);
            if (order == null)
            {
                _logger.LogWarning("PayTrail Payment Response - Unable to find order {OrderId}", orderId);
                return NotFound();
            }

            var paymentSettings = JsonConvert.DeserializeObject<PaymentSettings>(order.EkomPaymentSettingsData);
            var payTrailSettings = JsonConvert.DeserializeObject<PayTrailSettings>(order.EkomPaymentProviderData ?? string.Empty);

            if (paymentSettings == null || payTrailSettings == null)
            {
                return BadRequest();
            }

            if (!PayTrailHmacHelper.IsValidSignature(payTrailSettings.SecretKey, parameters, callback.Signature))
            {
                _logger.LogWarning("PayTrail Payment Response - Signature mismatch for order {OrderId}", orderId);
                await Events.OnErrorAsync(this, new ErrorEventArgs
                {
                    OrderStatus = order,
                });
                return Unauthorized();
            }

            if (callback.Status.Equals("ok", StringComparison.InvariantCultureIgnoreCase))
            {
                var expectedOrderAmount = GetExpectedOrderAmount(order, paymentSettings);
                var expectedAmount = Payment.ToMinorUnits(expectedOrderAmount, paymentSettings.Currency);
                if (callback.Amount != expectedAmount)
                {
                    _logger.LogWarning("PayTrail Payment Response - Amount mismatch for order {OrderId}. Expected {ExpectedAmount}, got {ActualAmount}", orderId, expectedAmount, callback.Amount);
                    await Events.OnErrorAsync(this, new ErrorEventArgs
                    {
                        OrderStatus = order,
                    });
                    return BadRequest();
                }

                if (await TryMarkOrderPaidAsync(order).ConfigureAwait(false))
                {
                    await SavePaymentDataAsync(order, callback, parameters, expectedOrderAmount).ConfigureAwait(false);

                    order.Paid = true;
                    await Events.OnSuccessAsync(this, new SuccessEventArgs
                    {
                        OrderStatus = order,
                    });

                    _logger.LogInformation("PayTrail Payment Response - SUCCESS - Order ID: {OrderId}", order.UniqueId);
                }
                else
                {
                    _logger.LogInformation("PayTrail Payment Response - SUCCESS - Previously validated - Order ID: {OrderId}", order.UniqueId);
                }

                if (isCallback)
                {
                    return Ok();
                }

                var successUrl = Ekom.Payments.Helpers.PaymentsUriHelper.EnsureFullUri(paymentSettings.SuccessUrl.ToString(), Request);
                return Redirect(successUrl.ToString());
            }

            await Events.OnErrorAsync(this, new ErrorEventArgs
            {
                OrderStatus = order,
            });

            if (isCallback)
            {
                return Ok();
            }

            var cancelUrl = Ekom.Payments.Helpers.PaymentsUriHelper.EnsureFullUri(paymentSettings.CancelUrl.ToString(), Request);
            return Redirect(cancelUrl.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PayTrail Payment Response - Failed");
            await Events.OnErrorAsync(this, new ErrorEventArgs
            {
                Exception = ex,
            });

            if (_settings.SendEmailAlerts)
            {
                await _mailSvc.SendAsync(new MailMessage
                {
                    Subject = "PayTrail Payment Response - Failed",
                    Body = $"<p>PayTrail Payment Response - Failed<p><br />{HttpContext.Request.GetDisplayUrl()}<br />" + ex + "<br><br> " + JsonConvert.SerializeObject(parameters),
                    IsBodyHtml = true,
                });
            }

            throw;
        }
    }

    static decimal GetExpectedOrderAmount(OrderStatus order, PaymentSettings paymentSettings)
    {
        var orderLines = paymentSettings.Orders?.ToList();

        return orderLines is { Count: > 0 }
            ? orderLines.Sum(x => x.GrandTotal)
            : order.Amount;
    }

    async Task<bool> TryMarkOrderPaidAsync(OrderStatus order)
    {
        using var db = _dbFac.GetDatabase();
        var updatedRows = await db.OrderStatus
            .Where(x => x.UniqueId == order.UniqueId && !x.Paid)
            .Set(x => x.Paid, true)
            .UpdateAsync()
            .ConfigureAwait(false);

        return updatedRows == 1;
    }

    static PayTrailCallback? CreateCallback(IReadOnlyDictionary<string, string> parameters)
    {
        if (!parameters.TryGetValue("signature", out var signature)
            || !parameters.TryGetValue("checkout-stamp", out var stamp)
            || !parameters.TryGetValue("checkout-status", out var status)
            || !parameters.TryGetValue("checkout-amount", out var amount))
        {
            return null;
        }

        return new PayTrailCallback
        {
            Account = parameters.GetValueOrDefault("checkout-account") ?? string.Empty,
            Algorithm = parameters.GetValueOrDefault("checkout-algorithm") ?? string.Empty,
            Amount = int.Parse(amount),
            Stamp = stamp,
            Reference = parameters.GetValueOrDefault("checkout-reference") ?? string.Empty,
            TransactionId = parameters.GetValueOrDefault("checkout-transaction-id") ?? string.Empty,
            Status = status,
            Provider = parameters.GetValueOrDefault("checkout-provider") ?? string.Empty,
            Signature = signature,
        };
    }

    async Task SavePaymentDataAsync(OrderStatus order, PayTrailCallback callback, Dictionary<string, string> parameters, decimal amount)
    {
        try
        {
            var paymentData = new PaymentData
            {
                Id = order.UniqueId,
                Date = DateTime.Now,
                PaymentMethod = callback.Provider,
                CustomData = JsonConvert.SerializeObject(new PayTrailPaymentData
                {
                    TransactionId = callback.TransactionId,
                    Provider = callback.Provider,
                    Status = callback.Status,
                    Parameters = parameters,
                }),
                Amount = amount.ToString(),
            };

            using var db = _dbFac.GetDatabase();
            await db.InsertOrReplaceAsync(paymentData).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PayTrail Payment Response - Error saving payment data");
        }
    }
}
