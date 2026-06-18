using Ekom.Payments.TeyaConsumerloans.Models;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Globalization;
using System.Net.Mail;
using System.Threading.Channels;

namespace Ekom.Payments.TeyaConsumerloans;

internal sealed record PollRequest(OrderStatus Order, TeyaConsumerloansSettings Settings);

/// <summary>
/// Hosted background service that polls <c>GET /online/status</c> for orders whose
/// customers closed the browser before being redirected back to the response endpoint.
/// <para>
/// Callers enqueue an order via <see cref="Enqueue"/> and return immediately.
/// The service owns the per-order <see cref="CancellationTokenSource"/> lifetime and
/// tracks all active polling tasks so they finish gracefully on host shutdown.
/// </para>
/// </summary>
public class TeyaConsumerloansPollingService : BackgroundService
{
    /// <summary>
    /// How often each order is checked. Docs recommend 3-5 seconds.
    /// </summary>
    public static TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(5);

    static readonly HashSet<string> TerminalStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "CANCELED", "FAILED", "PROGRESSEXPIRED", "TOKENEXPIRED",
    };

    const string SuccessStatus = "SUCCESS";

    readonly Channel<PollRequest> _queue = Channel.CreateUnbounded<PollRequest>(
        new UnboundedChannelOptions { SingleReader = true });

    readonly ILogger<TeyaConsumerloansPollingService> _logger;
    readonly IHttpClientFactory _httpClientFactory;
    readonly IDatabaseFactory _dbFac;
    readonly IOrderService _orderService;
    readonly PaymentsConfiguration _settings;
    readonly IMailService _mailSvc;
    readonly IHttpContextAccessor _httpContextAccessor;

    public TeyaConsumerloansPollingService(
        ILogger<TeyaConsumerloansPollingService> logger,
        IHttpClientFactory httpClientFactory,
        IDatabaseFactory dbFac,
        IOrderService orderService,
        PaymentsConfiguration settings,
        IMailService mailSvc,
        IHttpContextAccessor httpContextAccessor)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _dbFac = dbFac;
        _orderService = orderService;
        _settings = settings;
        _mailSvc = mailSvc;
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>
    /// Enqueues an order for polling. Returns immediately.
    /// </summary>
    public void Enqueue(OrderStatus order, TeyaConsumerloansSettings settings)
        => _queue.Writer.TryWrite(new PollRequest(order, settings));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var activeTasks = new List<Task>();

        await foreach (var req in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            activeTasks.RemoveAll(t => t.IsCompleted);

            // CTS is linked to stoppingToken so all polls are cancelled on host shutdown.
            // Ownership transfers to PollOrderAsync which disposes it when done.
            var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            cts.CancelAfter(TimeSpan.FromMinutes(req.Settings.TokenValidMinutes!.Value));
            activeTasks.Add(PollOrderAsync(req.Order, req.Settings, cts));
        }

        // Drain: wait for all in-flight polls to finish before the host tears down.
        if (activeTasks.Count > 0)
        {
            await Task.WhenAll(activeTasks).ConfigureAwait(false);
        }
    }

    async Task PollOrderAsync(OrderStatus order, TeyaConsumerloansSettings settings, CancellationTokenSource cts)
    {
        // cts is disposed here, after PollAsync finishes, not in the caller.
        using (cts)
        {
            await PollAsync(order, settings, cts.Token).ConfigureAwait(false);
        }
    }

    async Task PollAsync(OrderStatus order, TeyaConsumerloansSettings teyaSettings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order.CustomData);
        var token = order.CustomData;

        var client = new TeyaConsumerloansClient(_httpClientFactory, _logger);
        while (!cancellationToken.IsCancellationRequested)
        {
            var status = await client.GetStatusAsync(teyaSettings, token).ConfigureAwait(false);

            _logger.LogDebug("Teya Consumer Loans Polling Service - OrderId: {OrderId} Status: {Status}", order.UniqueId, status);

            if (TerminalStatuses.Contains(status))
            {
                _logger.LogInformation("Teya Consumer Loans Polling Service - Terminal status {Status} for OrderId: {OrderId}.", status, order.UniqueId);
                break;
            }

            if (string.Equals(status, SuccessStatus, StringComparison.OrdinalIgnoreCase))
            {
                await MarkAsPaid(order, teyaSettings).ConfigureAwait(false);
                break;
            }

            await Task.Delay(PollingInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    async Task MarkAsPaid(OrderStatus order, TeyaConsumerloansSettings teyaSettings)
    {
        var token = order.CustomData;
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(teyaSettings.MerchantId);

        _logger.LogInformation("Teya Consumer Loans Response - Start. Token: {Token}", token);

        try
        {
            if (order.Paid)
            {
                _logger.LogInformation("Teya Consumer Loans Response - SUCCESS - Previously validated");
                return;
            }

            var redirectUrl = order.EkomPaymentSettings.SuccessUrl;
            ArgumentNullException.ThrowIfNull(redirectUrl);

            var client = new TeyaConsumerloansClient(_httpClientFactory, _logger);
            var contractInfo = await client.ValidateLoanAsync(
                teyaSettings,
                new ValidateRequest
                {
                    Token = token,
                    RedirectUrl = redirectUrl.ToString(),
                    MerchantNumber = teyaSettings.MerchantId,
                }).ConfigureAwait(false);

            try
            {
                var paymentData = new PaymentData
                {
                    Id = order.UniqueId,
                    Date = DateTime.Now,
                    CustomData = JsonConvert.SerializeObject(contractInfo),
                    PaymentMethod = "Teya Consumer Loans",
                    Amount = order.Amount.ToString(CultureInfo.InvariantCulture),
                };

                await using var db = _dbFac.GetDatabase();
                await db.InsertOrReplaceAsync(paymentData).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Teya Consumer Loans Response - Error saving payment data");
            }

            order.Paid = true;
            order.CustomData = token;
            await _orderService.UpdateAsync(order).ConfigureAwait(false);

            await Events.OnSuccessAsync(this, new SuccessEventArgs
            {
                OrderStatus = order,
            }).ConfigureAwait(false);

            _logger.LogInformation("Teya Consumer Loans Response - SUCCESS. OrderId: {OrderId}", order.UniqueId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Teya Consumer Loans Response - Failed. OrderId: {OrderId}", order.UniqueId);

            await Events.OnErrorAsync(this, new ErrorEventArgs
            {
                Exception = ex,
                OrderStatus = order,
            }).ConfigureAwait(false);

            if (_settings.SendEmailAlerts)
            {
                await _mailSvc.SendAsync(new MailMessage
                {
                    Subject = "Teya Consumer Loans Response - Failed " + order.UniqueId,
                    Body = $"<p>Teya Consumer Loans Response - Failed<p><br />{_httpContextAccessor.HttpContext?.Request.GetDisplayUrl()}<br />{ex}",
                    IsBodyHtml = true,
                }).ConfigureAwait(false);
            }

            throw;
        }
    }
}
