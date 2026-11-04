using Ekom.Payments.TeyaConsumerloans.Models;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Globalization;
using System.Net;
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
/// New attempts are marked in their saved payment settings for one-time startup recovery.
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
    internal const string RecoveryKey = "teyaConsumerloansPolling";
    internal const string RecoveryPending = "pending";
    internal const string RecoveryFinished = "finished";

    internal CancellationToken ApplicationStarted { get; set; }

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

        try
        {
            if (ApplicationStarted.CanBeCanceled)
            {
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = ApplicationStarted.Register(() => ready.TrySetResult());
                await ready.Task.WaitAsync(stoppingToken).ConfigureAwait(false);
            }

            try
            {
                foreach (var request in await GetRecoveryRequestsAsync(stoppingToken).ConfigureAwait(false))
                {
                    Enqueue(request);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A failed startup scan must not stop polling for new payments.
                _logger.LogError(ex, "Teya Consumer Loans startup recovery failed; new payments will still be monitored.");
            }

            await foreach (var req in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                activeTasks.RemoveAll(t => t.IsCompleted);
                activeTasks.Add(PollOrderAsync(req, stoppingToken));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await Task.WhenAll(activeTasks).ConfigureAwait(false);
        }
    }

    internal static TimeSpan GetPollingTimeout(TeyaConsumerloansSettings settings)
    {
        // TokenValidMinutes allows opening the URL. ProgressValidMinutes allows
        // completing the loan after opening it, so both windows must be covered
        var operationalBuffer = TimeSpan.FromSeconds(30);
        return TimeSpan.FromMinutes(settings.TokenValidMinutes!.Value + settings.ProgressValidMinutes!.Value) + operationalBuffer;
    }

    internal static void EnableRecovery(PaymentSettings settings, Uri redirectUrl)
    {
        settings.OrderCustomData[RecoveryKey] = RecoveryPending;
        // Recovery needs the exact absolute URL used when requesting the token.
        settings.SuccessUrl = redirectUrl;
    }

    internal async Task<IReadOnlyList<PollingRequest>> GetRecoveryRequestsAsync(CancellationToken ct = default)
    {
        await using var db = _dbFac.GetDatabase();
        var orders = await db.OrderStatus
            .Where(x => !x.Paid && x.EkomPaymentSettingsData.Contains(RecoveryKey))
            .ToListAsync(ct).ConfigureAwait(false);
        var requests = new List<PollingRequest>();
        foreach (var order in orders)
        {
            try
            {
                var paymentSettings = order.EkomPaymentSettings;
                if (!paymentSettings.OrderCustomData.TryGetValue(RecoveryKey, out var state) || state != RecoveryPending)
                {
                    continue;
                }
                var settings = JsonConvert.DeserializeObject<TeyaConsumerloansSettings>(order.EkomPaymentProviderData!)!;
                requests.Add(new PollingRequest
                {
                    OrderId = order.UniqueId,
                    Token = order.CustomData!,
                    RedirectUrl = paymentSettings.SuccessUrl,
                    Settings = settings,
                    // OrderService stores DateTime.Now. Convert from that same local
                    // time convention; never grant a fresh window after a restart.
                    RecoveryDeadlineUtc = new DateTimeOffset(order.Date).ToUniversalTime() + GetPollingTimeout(settings),
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Teya Consumer Loans recovery could not read payment attempt {OrderId}.", order.UniqueId);
            }
        }
        return requests;
    }

    internal async Task PollOrderAsync(PollingRequest request, CancellationToken stoppingToken, TimeProvider? timeProvider = null)
    {
        timeProvider ??= TimeProvider.System;
        var remaining = request.RecoveryDeadlineUtc - timeProvider.GetUtcNow() ?? GetPollingTimeout(request.Settings);
        var checkOnce = request.RecoveryDeadlineUtc.HasValue && remaining <= TimeSpan.Zero;
        using var timeout = checkOnce ? new CancellationTokenSource() : new CancellationTokenSource(remaining, timeProvider);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeout.Token);

        try
        {
            var message = request.RecoveryDeadlineUtc.HasValue
                ? checkOnce ? "Startup recovery: monitoring window elapsed; checking the loan status once."
                    : $"Startup recovery: monitoring resumed with {Math.Ceiling(remaining.TotalSeconds)} seconds remaining in the original window."
                : "Loan application started; monitoring payment status.";
            await ActivityAsync(request, message).ConfigureAwait(false);
            await PollAsync(request, timeProvider, cts.Token, checkOnce).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await ActivityAsync(request, "Payment monitoring stopped because the application is shutting down; check the loan status with Teya.", LogLevel.Warning).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            await ActivityAsync(request, "Payment monitoring timed out without a final status; check the loan status with Teya before retrying payment.", LogLevel.Warning).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Teya Consumer Loans polling failed. OrderId: {OrderId}", request.OrderId);
            await ActivityAsync(request, $"Payment monitoring or completion failed ({ex.GetType().Name}); check the payment before retrying.", LogLevel.Error).ConfigureAwait(false);
        }
    }

    async Task PollAsync(PollingRequest request, TimeProvider timeProvider, CancellationToken cancellationToken, bool checkOnce)
    {
        string? lastStatus = null;
        var statusCheckFailed = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string status;
            try
            {
                status = await _client.GetStatusAsync(request.Settings, request.Token, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!checkOnce && !cancellationToken.IsCancellationRequested &&
                (ex is HttpRequestException httpError && IsTransient(httpError) || ex is OperationCanceledException))
            {
                _logger.LogWarning(ex, "Teya Consumer Loans status check failed; retrying. OrderId: {OrderId}", request.OrderId);
                if (!statusCheckFailed)
                {
                    await ActivityAsync(request, "Temporary status check failure; retrying within the payment monitoring window.", LogLevel.Warning).ConfigureAwait(false);
                    statusCheckFailed = true;
                }
                await Task.Delay(PollingInterval, timeProvider, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (statusCheckFailed)
            {
                await ActivityAsync(request, "Payment status checks resumed.").ConfigureAwait(false);
                statusCheckFailed = false;
            }

            if (!string.Equals(status, lastStatus, StringComparison.OrdinalIgnoreCase))
            {
                await ActivityAsync(request, $"Loan status changed: {lastStatus ?? "not checked"} -> {status}.",
                    TerminalStatuses.Contains(status) ? LogLevel.Warning : LogLevel.Information).ConfigureAwait(false);
                lastStatus = status;
            }

            if (TerminalStatuses.Contains(status))
            {
                await FinishRecoveryAsync(request).ConfigureAwait(false);
                break;
            }

            if (string.Equals(status, SuccessStatus, StringComparison.OrdinalIgnoreCase))
            {
                // Do not retry validation or success callbacks as part of status
                // polling. Their existing error handling remains unchanged.
                await MarkAsPaid(request).ConfigureAwait(false);
                await FinishRecoveryAsync(request).ConfigureAwait(false);
                break;
            }

            if (checkOnce)
            {
                await ActivityAsync(request, $"Startup recovery: overdue loan is still {status}; manual review is required. Automatic recovery finished.", LogLevel.Warning).ConfigureAwait(false);
                await FinishRecoveryAsync(request).ConfigureAwait(false);
                break;
            }

            await Task.Delay(PollingInterval, timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    static bool IsTransient(HttpRequestException exception)
    {
        return exception.StatusCode == null
            || exception.StatusCode == HttpStatusCode.RequestTimeout
            || exception.StatusCode == HttpStatusCode.TooManyRequests
            || (int)exception.StatusCode >= 500;
    }

    async Task FinishRecoveryAsync(PollingRequest request)
    {
        try
        {
            var order = await _orderService.GetAsync(request.OrderId).ConfigureAwait(false);
            if (order == null)
            {
                return;
            }
            var settings = order.EkomPaymentSettings;
            if (!settings.OrderCustomData.ContainsKey(RecoveryKey))
            {
                return;
            }
            settings.OrderCustomData[RecoveryKey] = RecoveryFinished;
            await using var db = _dbFac.GetDatabase();
            // Only update the recovery metadata, never overwrite Paid or other
            // payment columns with an older snapshot of the attempt.
            await db.OrderStatus.Where(x => x.UniqueId == request.OrderId)
                .Set(x => x.EkomPaymentSettingsData, JsonConvert.SerializeObject(settings))
                .UpdateAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Teya Consumer Loans recovery state could not be saved. OrderId: {OrderId}; a later restart may check it again.", request.OrderId);
        }
    }

    async Task MarkAsPaid(PollingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Settings.MerchantId);

        _logger.LogInformation("Teya Consumer Loans Response - Start. OrderId: {OrderId}", request.OrderId);

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
            await ActivityAsync(request, "Loan validated; payment recorded. Completing the Ekom order.", success: true).ConfigureAwait(false);

            using (_paymentExecutionContext.EnsureContext())
            {
                await Events.OnSuccessAsync(this, new SuccessEventArgs
                {
                    OrderStatus = order,
                }).ConfigureAwait(false);
            }

            _logger.LogInformation("Teya Consumer Loans Response - SUCCESS. OrderId: {OrderId}", order.UniqueId);
            await ActivityAsync(request, "Payment success notification delivered to Ekom.").ConfigureAwait(false);
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

    async Task ActivityAsync(PollingRequest request, string message, LogLevel level = LogLevel.Information, bool success = false)
    {
        _logger.Log(level, "Teya Consumer Loans - OrderId: {OrderId}; {Activity}", request.OrderId, message);
        try
        {
            var order = await _orderService.GetAsync(request.OrderId).ConfigureAwait(false);
            if (order != null)
            {
                await Events.OnActivityAsync(this, new LoanActivityEventArgs
                {
                    PaymentId = request.OrderId,
                    PaymentSettings = order.EkomPaymentSettings,
                    Message = message,
                    Level = level,
                    IsSuccess = success,
                }).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Teya Consumer Loans activity log failed. OrderId: {OrderId}", request.OrderId);
        }
    }
}
