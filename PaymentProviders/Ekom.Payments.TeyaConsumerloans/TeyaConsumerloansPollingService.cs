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

    readonly Channel<PollingRequest> _queue = Channel.CreateUnbounded<PollingRequest>(
        new UnboundedChannelOptions { SingleReader = true });

    readonly ILogger<TeyaConsumerloansPollingService> _logger;
    readonly IDatabaseFactory _dbFac;
    readonly IOrderService _orderService;
    readonly PaymentsConfiguration _settings;
    readonly IMailService _mailSvc;
    readonly IHttpContextAccessor _httpContextAccessor;
    readonly IPaymentExecutionContext _paymentExecutionContext;
    readonly TeyaConsumerloansClient _client;

    public TeyaConsumerloansPollingService(
        ILogger<TeyaConsumerloansPollingService> logger,
        IHttpClientFactory httpClientFactory,
        IDatabaseFactory dbFac,
        IOrderService orderService,
        PaymentsConfiguration settings,
        IMailService mailSvc,
        IHttpContextAccessor httpContextAccessor,
        IPaymentExecutionContext paymentExecutionContext)
    {
        _logger = logger;
        _dbFac = dbFac;
        _orderService = orderService;
        _settings = settings;
        _mailSvc = mailSvc;
        _httpContextAccessor = httpContextAccessor;
        _paymentExecutionContext = paymentExecutionContext;
        _client = new TeyaConsumerloansClient(httpClientFactory, _logger);
    }

    /// <summary>
    /// Enqueues an order for polling. Returns immediately.
    /// </summary>
    public void Enqueue(PollingRequest request) => _queue.Writer.TryWrite(request);

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
            activeTasks.Add(PollOrderAsync(req, cts));
        }

        // Drain: wait for all in-flight polls to finish before the host tears down.
        if (activeTasks.Count > 0)
        {
            await Task.WhenAll(activeTasks).ConfigureAwait(false);
        }
    }

    async Task PollOrderAsync(PollingRequest request, CancellationTokenSource cts)
    {
        // cts is disposed here, after PollAsync finishes, not in the caller.
        using (cts)
        {
            await PollAsync(request, cts.Token).ConfigureAwait(false);
        }
    }

    async Task PollAsync(PollingRequest request, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var status = await _client.GetStatusAsync(request.Settings, request.Token).ConfigureAwait(false);

            _logger.LogDebug("Teya Consumer Loans Polling Service - OrderId: {OrderId} Status: {Status}", request.OrderId, status);

            if (TerminalStatuses.Contains(status))
            {
                _logger.LogInformation("Teya Consumer Loans Polling Service - Terminal status {Status} for OrderId: {OrderId}.", status, request.OrderId);
                break;
            }

            if (string.Equals(status, SuccessStatus, StringComparison.OrdinalIgnoreCase))
            {
                await MarkAsPaid(request).ConfigureAwait(false);
                break;
            }

            await Task.Delay(PollingInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    async Task MarkAsPaid(PollingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Settings.MerchantId);

        _logger.LogInformation("Teya Consumer Loans Response - Start. Token: {Token}", request.Token);

        var order = await _orderService.GetAsync(request.OrderId);
        if (order == null)
        {
            _logger.LogWarning("Teya Consumer Loans Response - Order not found. OrderId: {OrderId}", request.OrderId);
            return;
        }

        if (order.Paid)
        {
            _logger.LogInformation("Teya Consumer Loans Response - SUCCESS - Previously validated");
            return;
        }

        try
        {
            var validationRequest = new ValidateRequest
            {
                Token = request.Token,
                RedirectUrl = $"{request.RedirectUrl}&token={request.Token}",
                MerchantNumber = request.Settings.MerchantId,
            };
            var contractInfo = await _client.ValidateLoanAsync(request.Settings, validationRequest).ConfigureAwait(false);

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
            await _orderService.UpdateAsync(order).ConfigureAwait(false);

            using (_paymentExecutionContext.EnsureContext())
            {
                await Events.OnSuccessAsync(this, new SuccessEventArgs
                {
                    OrderStatus = order,
                }).ConfigureAwait(false);
            }

            _logger.LogInformation("Teya Consumer Loans Response - SUCCESS. OrderId: {OrderId}", order.UniqueId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Teya Consumer Loans Response - Failed. OrderId: {OrderId}", order.UniqueId);

            using var paymentContext = _paymentExecutionContext.EnsureContext();

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
