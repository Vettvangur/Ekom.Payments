using Ekom.Models.Payments;
using Ekom.Payments.TeyaConsumerloans.Models;
using Ekom.Services;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
    // Where the loan token is kept in the provider data stored with the payment.
    const string TokenProperty = "teyaToken";

    // How legacy payment rows were marked for monitoring before payments were recorded on the Ekom order.
    internal const string RecoveryKey = "teyaConsumerloansPolling";
    internal const string RecoveryPending = "pending";

    /// <summary>
    /// After a restart, unfinished loans requested within this period are checked again.
    /// Older ones were already checked or reviewed.
    /// </summary>
    internal static readonly TimeSpan RecoveryLookback = TimeSpan.FromDays(7);

    internal CancellationToken ApplicationStarted { get; set; }

    readonly Channel<PollingRequest> _queue = Channel.CreateUnbounded<PollingRequest>(
        new UnboundedChannelOptions { SingleReader = true });

    readonly ILogger<TeyaConsumerloansPollingService> _logger;
    readonly IDatabaseFactory _dbFac;
    readonly IOrderService _legacyOrders;
    readonly IOrderPayments _payments;
    readonly PaymentsConfiguration _settings;
    readonly IMailService _mailSvc;
    readonly IHttpContextAccessor _httpContextAccessor;
    readonly IPaymentExecutionContext _paymentExecutionContext;
    readonly TeyaConsumerloansClient _client;

    public TeyaConsumerloansPollingService(
        ILogger<TeyaConsumerloansPollingService> logger,
        IHttpClientFactory httpClientFactory,
        IDatabaseFactory dbFac,
        IOrderService legacyOrders,
        IOrderPayments payments,
        PaymentsConfiguration settings,
        IMailService mailSvc,
        IHttpContextAccessor httpContextAccessor,
        IPaymentExecutionContext paymentExecutionContext)
    {
        _logger = logger;
        _dbFac = dbFac;
        _legacyOrders = legacyOrders;
        _payments = payments;
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

    /// <summary>
    /// Monitoring that resumes after a restart needs the exact absolute URL used when requesting the token.
    /// </summary>
    internal static void EnableRecovery(PaymentSettings settings, Uri redirectUrl)
    {
        settings.SuccessUrl = redirectUrl;
    }

    /// <summary>The provider data stored with a payment: the loan settings and the loan token.</summary>
    internal static JObject StoredPayment(TeyaConsumerloansSettings settings, string token)
    {
        var json = JObject.FromObject(settings);
        json[Ekom.Payments.Providers.PaymentRowGuard.ProviderPropertyName] = Payment._ppNodeName;
        json[TokenProperty] = token;
        return json;
    }

    /// <summary>
    /// The loans to pick up again after a restart: Teya payments that were requested and have had nothing
    /// recorded since. A loan whose outcome or review note was recorded is not monitored again.
    /// </summary>
    internal async Task<IReadOnlyList<PollingRequest>> GetRecoveryRequestsAsync(CancellationToken ct = default, TimeProvider? timeProvider = null)
    {
        await ImportLegacyAttemptsAsync(ct).ConfigureAwait(false);

        var since = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime - RecoveryLookback;
        var payments = await _payments.GetOpenPaymentsAsync(Payment._ppNodeName, since, ct).ConfigureAwait(false);
        var requests = new List<PollingRequest>();
        foreach (var payment in payments)
        {
            try
            {
                var stored = string.IsNullOrWhiteSpace(payment.ProviderData) ? null : JObject.Parse(payment.ProviderData);
                // Payments copied from legacy rows kept the token as the provider's lookup key.
                var token = stored?[TokenProperty]?.Value<string>() ?? payment.ProviderReference;
                var settings = stored?.ToObject<TeyaConsumerloansSettings>();
                var redirectUrl = payment.GetPaymentSettings()?.SuccessUrl;
                if (settings?.ApiBaseUrl == null || string.IsNullOrWhiteSpace(token) || redirectUrl == null)
                {
                    // Not a Teya loan, or not enough left of it to ask Teya about.
                    continue;
                }

                requests.Add(new PollingRequest
                {
                    PaymentId = payment.PaymentId,
                    Token = token,
                    RedirectUrl = redirectUrl,
                    Settings = settings,
                    // Never grant a fresh window after a restart.
                    RecoveryDeadlineUtc = new DateTimeOffset(DateTime.SpecifyKind(payment.CreatedUtc, DateTimeKind.Utc)) + GetPollingTimeout(settings),
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Teya Consumer Loans recovery could not read a payment. " + PaymentLog.Template,
                    payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);
            }
        }
        return requests;
    }

    /// <summary>
    /// Loans started before payments were recorded on the Ekom order only exist as legacy rows marked for
    /// monitoring. They are copied to Ekom so a loan in progress during the upgrade is still followed up.
    /// </summary>
    async Task ImportLegacyAttemptsAsync(CancellationToken ct)
    {
        try
        {
            List<OrderStatus> rows;
            await using (var db = _dbFac.GetDatabase())
            {
                rows = await db.OrderStatus
                    .Where(x => !x.Paid && x.EkomPaymentSettingsData.Contains(RecoveryKey))
                    .ToListAsync(ct).ConfigureAwait(false);
            }

            foreach (var row in rows)
            {
                try
                {
                    if (row.EkomPaymentSettings.OrderCustomData.TryGetValue(RecoveryKey, out var state) && state == RecoveryPending)
                    {
                        await LegacyPaymentBridge.GetOrImportAsync(_payments, _legacyOrders, row.UniqueId).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Teya Consumer Loans recovery could not copy legacy payment {PaymentId}.", row.UniqueId);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A site that never used the legacy payment table has nothing to copy.
            _logger.LogDebug(ex, "Teya Consumer Loans recovery found no legacy payments to copy.");
        }
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
            _logger.LogError(ex, "Teya Consumer Loans polling failed. PaymentId: {PaymentId}", request.PaymentId);
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
                _logger.LogWarning(ex, "Teya Consumer Loans status check failed; retrying. PaymentId: {PaymentId}", request.PaymentId);
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
                await RecordOutcomeAsync(request, status).ConfigureAwait(false);
                break;
            }

            if (string.Equals(status, SuccessStatus, StringComparison.OrdinalIgnoreCase))
            {
                // Do not retry validation or success callbacks as part of status
                // polling. Their existing error handling remains unchanged.
                await MarkAsPaid(request).ConfigureAwait(false);
                break;
            }

            if (checkOnce)
            {
                await ActivityAsync(request, $"Startup recovery: overdue loan is still {status}; manual review is required. Automatic recovery finished.", LogLevel.Warning).ConfigureAwait(false);
                await RecordReviewNoteAsync(request, status).ConfigureAwait(false);
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

    /// <summary>
    /// Teya's final answer that the loan did not go through. Recording it also ends monitoring of the payment.
    /// </summary>
    async Task RecordOutcomeAsync(PollingRequest request, string status)
    {
        try
        {
            var cancelled = string.Equals(status, "CANCELED", StringComparison.OrdinalIgnoreCase);
            await _payments.RecordAsync(new PaymentTransaction
            {
                PaymentId = request.PaymentId,
                Type = cancelled ? PaymentTransactionType.Cancelled : PaymentTransactionType.Failed,
                PaymentMethod = "Teya Consumer Loans",
                Message = cancelled ? "The loan application was cancelled" : $"The loan application ended with Teya status {status}",
                Details = [new PaymentDetail("status", "Teya loan status", status)],
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Teya Consumer Loans outcome could not be recorded. PaymentId: {PaymentId}; a later restart checks the loan again.", request.PaymentId);
        }
    }

    /// <summary>
    /// The loan was still undecided when its monitoring window had passed. The note tells staff to check it
    /// with Teya, and ends automatic monitoring of the payment without calling it paid or failed.
    /// </summary>
    async Task RecordReviewNoteAsync(PollingRequest request, string status)
    {
        try
        {
            await _payments.RecordAsync(new PaymentTransaction
            {
                PaymentId = request.PaymentId,
                Type = PaymentTransactionType.Note,
                PaymentMethod = "Teya Consumer Loans",
                Message = $"Loan monitoring ended while the loan was still {status}. Check the loan with Teya before the order is handled.",
                IdempotencyKey = $"{request.PaymentId:N}:monitoring-ended",
                Details = [new PaymentDetail("status", "Teya loan status", status)],
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Teya Consumer Loans review note could not be recorded. PaymentId: {PaymentId}; a later restart checks the loan again.", request.PaymentId);
        }
    }

    async Task MarkAsPaid(PollingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Settings.MerchantId);

        var payment = await _payments.GetAsync(request.PaymentId).ConfigureAwait(false);
        if (payment == null)
        {
            _logger.LogWarning("Teya Consumer Loans Response - Payment not found. PaymentId: {PaymentId}", request.PaymentId);
            return;
        }

        using var scope = PaymentLog.BeginScope(_logger, payment);

        var orderPayments = await _payments.GetForOrderAsync(payment.OrderUniqueId).ConfigureAwait(false);
        if (orderPayments.Attempts.Any(x => x.PaymentId == payment.PaymentId && x.CapturedUtc != null))
        {
            _logger.LogInformation("Teya Consumer Loans Response - SUCCESS - Previously validated. " + PaymentLog.Template,
                payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);
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

            await ActivityAsync(request, "Loan validated; recording the payment and completing the Ekom order.", success: true).ConfigureAwait(false);

            // Recording the capture completes the order's checkout, which needs an Umbraco context.
            using (_paymentExecutionContext.EnsureContext())
            {
                await _payments.RecordAsync(new PaymentTransaction
                {
                    PaymentId = payment.PaymentId,
                    Type = PaymentTransactionType.Captured,
                    Amount = payment.Amount,
                    TransactionId = contractInfo.ContractNumber,
                    AuthorizationCode = contractInfo.AuthorizationNumber,
                    PaymentMethod = "Teya Consumer Loans",
                    IdempotencyKey = LegacyPaymentBridge.CaptureKey(payment.PaymentId),
                    Details = [new PaymentDetail("contractNumber", "Loan contract number", contractInfo.ContractNumber, true)],
                    // The borrower's national id is not kept with the payment.
                    RawData = JsonConvert.SerializeObject(new { contractInfo.ContractNumber, contractInfo.AuthorizationNumber }),
                }).ConfigureAwait(false);
            }

            _logger.LogInformation("Teya Consumer Loans Response - SUCCESS. " + PaymentLog.Template,
                payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);
            await ActivityAsync(request, "Payment recorded on the Ekom order.").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Teya Consumer Loans Response - Failed. " + PaymentLog.Template,
                payment.OrderNumber, payment.OrderUniqueId, payment.PaymentId);

            if (_settings.SendEmailAlerts)
            {
                using var paymentContext = _paymentExecutionContext.EnsureContext();

                await _mailSvc.SendAsync(new MailMessage
                {
                    Subject = $"Teya Consumer Loans Response - Failed. Order {payment.OrderNumber}, payment {payment.PaymentId}",
                    Body = $"<p>Teya Consumer Loans Response - Failed<p><br />{_httpContextAccessor.HttpContext?.Request.GetDisplayUrl()}<br />{ex}",
                    IsBodyHtml = true,
                }).ConfigureAwait(false);
            }

            throw;
        }
    }

    async Task ActivityAsync(PollingRequest request, string message, LogLevel level = LogLevel.Information, bool success = false)
    {
        _logger.Log(level, "Teya Consumer Loans - PaymentId: {PaymentId}; {Activity}", request.PaymentId, message);
        try
        {
            var payment = await _payments.GetAsync(request.PaymentId).ConfigureAwait(false);
            var paymentSettings = payment?.GetPaymentSettings();
            if (paymentSettings != null)
            {
                await Events.OnActivityAsync(this, new LoanActivityEventArgs
                {
                    PaymentId = request.PaymentId,
                    PaymentSettings = paymentSettings,
                    Message = message,
                    Level = level,
                    IsSuccess = success,
                }).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Teya Consumer Loans activity log failed. PaymentId: {PaymentId}", request.PaymentId);
        }
    }
}
