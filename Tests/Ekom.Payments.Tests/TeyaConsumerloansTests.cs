using Ekom.Models.Payments;
using Ekom.Payments.TeyaConsumerloans;
using Ekom.Payments.TeyaConsumerloans.Models;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Newtonsoft.Json;
using System.Net;
using System.Threading.Channels;
using Xunit;
using LoanEvents = Ekom.Payments.TeyaConsumerloans.Events;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Ekom.Payments.Tests;

/// <summary>
/// Teya Consumer Loans is monitored by polling. The outcome is recorded on the Ekom order's payment, and a
/// payment with nothing recorded since its request is what gets picked up again after a restart.
/// </summary>
public sealed class TeyaConsumerloansTests
{
    [Theory]
    [InlineData(5, 5, 10)]
    [InlineData(2, 10, 12)]
    public void MonitoringCoversBothTokenAndProgressWindowsPlusThirtySecondBuffer(int token, int progress, int expected)
    {
        Assert.Equal(TimeSpan.FromMinutes(expected) + TimeSpan.FromSeconds(30), TeyaConsumerloansPollingService.GetPollingTimeout(
            new TeyaConsumerloansSettings { TokenValidMinutes = token, ProgressValidMinutes = progress }));
    }

    [Fact]
    public async Task LoanCanCompleteAfterTokenStartWindowHasElapsed()
    {
        await using var f = await Fixture.CreateAsync();
        var polling = f.PollAsync();
        await f.Clock.WaitForPollDelayAsync();
        f.Http.Status = "SUCCESS";
        f.Clock.Advance(TimeSpan.FromMinutes(6));
        await polling.WaitAsync(TimeSpan.FromSeconds(10));

        var capture = Assert.Single(f.Payments.Recorded);
        Assert.Equal(PaymentTransactionType.Captured, capture.Type);
        Assert.Equal(f.Request.PaymentId, capture.PaymentId);
        Assert.Equal("1234", capture.TransactionId);
        Assert.Equal("5678", capture.AuthorizationCode);
        Assert.Equal(1, f.Http.Validations);
        Assert.Contains(f.Activity, x => x.Message.Contains("INPROGRESS -> SUCCESS", StringComparison.Ordinal));
        Assert.Contains(f.Activity, x => x.IsSuccess);
        Assert.All(f.Activity, x => Assert.Equal(f.Request.PaymentId, x.PaymentId));
        Assert.All(f.Activity, x => Assert.Equal(f.OrderId, x.PaymentSettings.OrderUniqueId));
        Assert.DoesNotContain(f.Activity, x => x.Message.Contains(f.Request.Token, StringComparison.Ordinal));
        Assert.Equal($"{f.Request.RedirectUrl}&token={f.Request.Token}", f.Http.RedirectUrl);
    }

    [Theory]
    [InlineData("CANCELED", PaymentTransactionType.Cancelled)]
    [InlineData("FAILED", PaymentTransactionType.Failed)]
    [InlineData("TOKENEXPIRED", PaymentTransactionType.Failed)]
    [InlineData("PROGRESSEXPIRED", PaymentTransactionType.Failed)]
    public async Task TerminalStatusIsRecordedWithoutValidatingOrCapturingThePayment(string status, PaymentTransactionType expected)
    {
        await using var f = await Fixture.CreateAsync();
        f.Http.Status = status;
        await f.PollAsync();
        var outcome = Assert.Single(f.Payments.Recorded);
        Assert.Equal(expected, outcome.Type);
        Assert.False(f.Captured);
        Assert.Equal(0, f.Http.Validations);
        Assert.Contains(f.Activity, x => x.Message.EndsWith($"-> {status}.", StringComparison.Ordinal) && x.Level == LogLevel.Warning);
    }

    [Theory]
    [InlineData("503")]
    [InlineData("429")]
    [InlineData("408")]
    [InlineData("network")]
    [InlineData("timeout")]
    public async Task TemporaryStatusFailureIsRetriedWithinTheSameWindow(string failure)
    {
        await using var f = await Fixture.CreateAsync();
        f.Http.StatusFailure = failure;
        var polling = f.PollAsync();
        await f.Clock.WaitForPollDelayAsync();
        f.Http.StatusFailure = null;
        f.Http.Status = "SUCCESS";
        f.Clock.Advance(TeyaConsumerloansPollingService.PollingInterval);
        await polling.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(f.Captured);
        Assert.Equal(1, f.Http.Validations);
        Assert.Contains(f.Activity, x => x.Message.StartsWith("Temporary status check failure", StringComparison.Ordinal));
        Assert.Contains(f.Activity, x => x.Message == "Payment status checks resumed.");
    }

    [Fact]
    public async Task AuthenticationFailureIsReportedWithoutRetrying()
    {
        await using var f = await Fixture.CreateAsync();
        f.Http.StatusFailure = "403";
        await f.PollAsync();
        Assert.Equal(1, f.Http.StatusChecks);
        Assert.Equal(0, f.Http.Validations);
        Assert.Contains(f.Activity, x => x.Level == LogLevel.Error);
        Assert.Empty(f.Payments.Recorded);
    }

    [Fact]
    public async Task TimeoutIsLoggedWithoutRecordingAnOutcome()
    {
        await using var f = await Fixture.CreateAsync();
        var polling = f.PollAsync();
        await f.Clock.WaitForPollDelayAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(30));
        await polling.WaitAsync(TimeSpan.FromSeconds(10));
        // Nothing is known yet: the payment stays open and is checked once more after the next restart.
        Assert.Empty(f.Payments.Recorded);
        Assert.Equal(0, f.Http.Validations);
        Assert.Contains(f.Activity, x => x.Level == LogLevel.Warning && x.Message.Contains("timed out", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ShutdownCancelsAnInFlightStatusRequestWithoutRetrying()
    {
        await using var f = await Fixture.CreateAsync();
        using var stopping = new CancellationTokenSource();
        f.Http.WaitForCancellation = true;
        var polling = f.PollAsync(stopping.Token);
        await f.Http.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        stopping.Cancel();
        await polling.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, f.Http.StatusChecks);
        Assert.Equal(0, f.Http.Validations);
        Assert.Contains(f.Activity, x => x.Message.Contains("shutting down", StringComparison.Ordinal));
        Assert.DoesNotContain(f.Activity, x => x.Message.StartsWith("Temporary status check failure", StringComparison.Ordinal));
        Assert.Single(await f.RecoveryAsync());
    }

    [Fact]
    public async Task LogsStatusChangesRatherThanEveryPoll()
    {
        await using var f = await Fixture.CreateAsync();
        using var stopping = new CancellationTokenSource();
        var polling = f.PollAsync(stopping.Token);
        for (var i = 0; i < 3; i++)
        {
            await f.Clock.WaitForPollDelayAsync();
            f.Clock.Advance(TeyaConsumerloansPollingService.PollingInterval);
        }
        await f.Clock.WaitForPollDelayAsync();
        stopping.Cancel();
        await polling.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(f.Activity, x => x.Message.StartsWith("Loan status changed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidationFailureIsReportedAndNotAutomaticallyRetried()
    {
        await using var f = await Fixture.CreateAsync();
        f.Http.Status = "SUCCESS";
        f.Http.FailValidation = true;

        await f.PollAsync();

        Assert.Equal(1, f.Http.Validations);
        Assert.Empty(f.Payments.Recorded);
        Assert.Contains(f.Activity, x => x.Level == LogLevel.Error);
    }

    [Fact]
    public async Task RepeatedStatusFailuresDoNotExtendTimeoutOrFloodOrderActivity()
    {
        await using var f = await Fixture.CreateAsync();
        f.Http.StatusFailure = "503";
        var polling = f.PollAsync();
        await f.Clock.WaitForPollDelayAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(6));
        await f.Clock.WaitForPollDelayAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(30));
        await polling.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, f.Http.Validations);
        Assert.Single(f.Activity, x => x.Message.StartsWith("Temporary status check failure", StringComparison.Ordinal));
        Assert.Contains(f.Activity, x => x.Message.Contains("timed out", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckoutCompletionFailureIsReportedWithoutRevalidatingOrCapturingAgain()
    {
        await using var f = await Fixture.CreateAsync();
        f.Http.Status = "SUCCESS";
        // Ekom stores the capture, then completing the order's checkout fails.
        f.Payments.FailCheckoutCompletion = true;

        await f.PollAsync();

        Assert.Equal(1, f.Http.Validations);
        Assert.Single(f.Payments.Captures(f.Request.PaymentId));
        Assert.Contains(f.Activity, x => x.Level == LogLevel.Error);
        // The capture is on record, so the loan is not validated again after a restart.
        Assert.Empty(await f.RecoveryAsync());
    }

    [Fact]
    public async Task PreviouslyCapturedPaymentIsNotValidatedAgain()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Payments.RecordAsync(new Ekom.Services.PaymentTransaction { PaymentId = f.Request.PaymentId, Type = PaymentTransactionType.Captured });
        f.Http.Status = "SUCCESS";
        await f.PollAsync();
        Assert.Equal(0, f.Http.Validations);
        Assert.Single(f.Payments.Captures(f.Request.PaymentId));
    }

    [Fact]
    public async Task ActivityLoggingFailureDoesNotBlockPaymentCompletion()
    {
        await using var f = await Fixture.CreateAsync();
        Task Fail(object _, LoanActivityEventArgs e) => throw new InvalidOperationException("Activity unavailable");
        LoanEvents.ActivityAsync += Fail;
        try
        {
            f.Http.Status = "SUCCESS";
            await f.PollAsync();
            Assert.True(f.Captured);
        }
        finally
        {
            LoanEvents.ActivityAsync -= Fail;
        }
    }

    [Fact]
    public async Task RestartResumesOnlyTheRemainingOriginalWindow()
    {
        await using var f = await Fixture.CreateAsync();
        f.SetAge(TimeSpan.FromMinutes(6));
        var request = Assert.Single(await f.RecoveryAsync());
        Assert.Equal(f.Clock.GetUtcNow().AddMinutes(4).AddSeconds(30), request.RecoveryDeadlineUtc);
        var polling = f.Service.PollOrderAsync(request, CancellationToken.None, f.Clock);
        await f.Clock.WaitForPollDelayAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(30));
        await polling.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(f.Activity, x => x.Message.Contains("270 seconds remaining", StringComparison.Ordinal));
        Assert.Contains(f.Activity, x => x.Message.Contains("timed out", StringComparison.Ordinal));
        // Still undecided: it gets its single overdue check after the next restart.
        Assert.Single(await f.RecoveryAsync());
    }

    [Fact]
    public async Task LoanCanCompleteDuringTheThirtySecondBuffer()
    {
        await using var f = await Fixture.CreateAsync();
        var polling = f.PollAsync();
        await f.Clock.WaitForPollDelayAsync();
        f.Http.Status = "SUCCESS";
        f.Clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(15));
        await polling.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(f.Captured);
        Assert.Equal(1, f.Http.Validations);
        Assert.DoesNotContain(f.Activity, x => x.Message.Contains("timed out", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RestartDuringBufferResumesPollingForOnlyTheRemainingBuffer()
    {
        await using var f = await Fixture.CreateAsync();
        f.SetAge(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(15));
        var request = Assert.Single(await f.RecoveryAsync());
        Assert.Equal(f.Clock.GetUtcNow().AddSeconds(15), request.RecoveryDeadlineUtc);
        var polling = f.Service.PollOrderAsync(request, CancellationToken.None, f.Clock);
        await f.Clock.WaitForPollDelayAsync();
        f.Clock.Advance(TimeSpan.FromSeconds(15));
        await polling.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(f.Activity, x => x.Message.Contains("15 seconds remaining", StringComparison.Ordinal));
        Assert.Contains(f.Activity, x => x.Message.Contains("timed out", StringComparison.Ordinal));
        Assert.DoesNotContain(f.Activity, x => x.Message.Contains("checking the loan status once", StringComparison.Ordinal));
        Assert.Single(await f.RecoveryAsync());
    }

    [Fact]
    public async Task RecoveryDeadlineIsRecheckedWhenAnAttemptWaitedInTheQueue()
    {
        await using var f = await Fixture.CreateAsync();
        f.SetAge(TimeSpan.FromMinutes(6));
        var request = Assert.Single(await f.RecoveryAsync());
        f.Clock.Advance(TimeSpan.FromMinutes(5));
        await f.Service.PollOrderAsync(request, CancellationToken.None, f.Clock);
        Assert.Equal(1, f.Http.StatusChecks);
        Assert.Equal(0, f.Http.Validations);
        Assert.Contains(f.Activity, x => x.Message.Contains("manual review", StringComparison.Ordinal));
        Assert.Empty(await f.RecoveryAsync());
    }

    [Fact]
    public async Task LoanCompletedWhileSiteWasDownIsValidatedOnceAfterItsWindow()
    {
        await using var f = await Fixture.CreateAsync();
        f.SetAge(TimeSpan.FromMinutes(30));
        f.Http.Status = "SUCCESS";
        var request = Assert.Single(await f.RecoveryAsync());
        await f.Service.PollOrderAsync(request, CancellationToken.None, f.Clock);
        Assert.Equal(1, f.Http.StatusChecks);
        Assert.Equal(1, f.Http.Validations);
        Assert.Equal($"{f.Request.RedirectUrl}&token={f.Request.Token}", f.Http.RedirectUrl);
        Assert.True(f.Captured);
        Assert.Empty(await f.RecoveryAsync());
    }

    [Theory]
    [InlineData("CANCELED")]
    [InlineData("FAILED")]
    [InlineData("TOKENEXPIRED")]
    [InlineData("PROGRESSEXPIRED")]
    public async Task OverdueTerminalAttemptIsCheckedOnceAndNotRecoveredAgain(string status)
    {
        await using var f = await Fixture.CreateAsync();
        f.SetAge(TimeSpan.FromMinutes(30));
        f.Http.Status = status;
        var request = Assert.Single(await f.RecoveryAsync());
        await f.Service.PollOrderAsync(request, CancellationToken.None, f.Clock);
        Assert.Equal(1, f.Http.StatusChecks);
        Assert.Equal(0, f.Http.Validations);
        Assert.False(f.Captured);
        Assert.Single(f.Payments.Recorded);
        Assert.Empty(await f.RecoveryAsync());
    }

    [Theory]
    [InlineData("CREATED")]
    [InlineData("INPROGRESS")]
    [InlineData("PENDING")]
    public async Task OverdueNonFinalAttemptGetsOneCheckAndANoteAskingForManualReview(string status)
    {
        await using var f = await Fixture.CreateAsync();
        f.SetAge(TimeSpan.FromMinutes(30));
        f.Http.Status = status;
        var request = Assert.Single(await f.RecoveryAsync());
        await f.Service.PollOrderAsync(request, CancellationToken.None, f.Clock);
        Assert.Equal(1, f.Http.StatusChecks);
        Assert.Equal(0, f.Http.Validations);
        // Neither paid nor failed: a note on the payment tells staff to check the loan with Teya.
        var note = Assert.Single(f.Payments.Recorded);
        Assert.Equal(PaymentTransactionType.Note, note.Type);
        Assert.Contains(status, note.Message!, StringComparison.Ordinal);
        Assert.Contains(f.Activity, x => x.Level == LogLevel.Warning && x.Message.Contains("manual review", StringComparison.Ordinal));
        Assert.Empty(await f.RecoveryAsync());
    }

    [Theory]
    [InlineData("503")]
    [InlineData("network")]
    [InlineData("timeout")]
    public async Task FailedOverdueCheckIsNotRetriedImmediatelyButRemainsAvailableForNextRestart(string failure)
    {
        await using var f = await Fixture.CreateAsync();
        f.SetAge(TimeSpan.FromMinutes(30));
        f.Http.StatusFailure = failure;
        var request = Assert.Single(await f.RecoveryAsync());
        await f.Service.PollOrderAsync(request, CancellationToken.None, f.Clock);
        Assert.Equal(1, f.Http.StatusChecks);
        Assert.Equal(0, f.Http.Validations);
        var retry = Assert.Single(await f.RecoveryAsync());
        f.Http.StatusFailure = null;
        f.Http.Status = "SUCCESS";
        await f.Service.PollOrderAsync(retry, CancellationToken.None, f.Clock);
        Assert.Equal(2, f.Http.StatusChecks);
        Assert.True(f.Captured);
    }

    [Fact]
    public async Task RecoveryLeavesAttemptsOlderThanAWeekAlone()
    {
        await using var f = await Fixture.CreateAsync();
        f.SetAge(TeyaConsumerloansPollingService.RecoveryLookback + TimeSpan.FromMinutes(1));
        Assert.Empty(await f.RecoveryAsync());
    }

    [Fact]
    public async Task APaymentThatIsNotAReadableTeyaLoanDoesNotPreventOtherAttemptsFromResuming()
    {
        await using var f = await Fixture.CreateAsync();
        f.SetAge(TimeSpan.FromMinutes(30));
        var invalid = await f.Payments.CreateAsync(f.PaymentSettings(), "teyaConsumerloans", 100m, new { }, createdUtc: f.Clock.GetUtcNow().UtcDateTime);
        var unreadable = await f.Payments.CreateAsync(f.PaymentSettings(), "teyaConsumerloans", 100m, createdUtc: f.Clock.GetUtcNow().UtcDateTime);
        unreadable.ProviderData = "not json";

        var request = Assert.Single(await f.RecoveryAsync());

        Assert.Equal(f.Request.PaymentId, request.PaymentId);
        Assert.NotEqual(invalid.PaymentId, request.PaymentId);
    }

    [Fact]
    public async Task RecoveryChecksEveryAttemptNotJustTheNewestForAnOrder()
    {
        await using var f = await Fixture.CreateAsync();
        f.SetAge(TimeSpan.FromMinutes(30));
        var nextToken = Guid.NewGuid().ToString("N");
        var later = await f.Payments.CreateAsync(f.PaymentSettings(), "teyaConsumerloans", 100m,
            TeyaConsumerloansPollingService.StoredPayment(f.Request.Settings, nextToken),
            createdUtc: f.Clock.GetUtcNow().UtcDateTime - TimeSpan.FromMinutes(29));
        f.Http.StatusByToken[f.Request.Token] = "SUCCESS";
        f.Http.StatusByToken[nextToken] = "PROGRESSEXPIRED";
        var requests = await f.RecoveryAsync();
        Assert.Equal(2, requests.Count);
        foreach (var request in requests)
        {
            await f.Service.PollOrderAsync(request, CancellationToken.None, f.Clock);
        }
        Assert.True(f.Captured);
        Assert.Empty(f.Payments.Captures(later.PaymentId));
        Assert.Contains(f.Payments.Recorded, x => x.PaymentId == later.PaymentId && x.Type == PaymentTransactionType.Failed);
        Assert.Equal(1, f.Http.Validations);
        Assert.Empty(await f.RecoveryAsync());
    }

    [Fact]
    public async Task StartupRecoveryWaitsUntilTheApplicationHasStarted()
    {
        await using var f = await Fixture.CreateAsync();
        // The hosted service runs on the system clock.
        f.Payments.Payments[0].CreatedUtc = DateTime.UtcNow - TimeSpan.FromMinutes(30);
        using var ready = new CancellationTokenSource();
        f.Service.ApplicationStarted = ready.Token;
        f.Http.Status = "SUCCESS";
        try
        {
            await f.Service.StartAsync(CancellationToken.None);
            Assert.Equal(0, f.Http.StatusChecks);
            ready.Cancel();
            for (var i = 0; i < 200 && !f.Captured; i++)
            {
                await Task.Delay(50);
            }
        }
        finally
        {
            await f.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(1, f.Http.StatusChecks);
        Assert.True(f.Captured);
    }

    [Fact]
    public async Task LoanStartedBeforeTheUpgradeIsCopiedFromItsLegacyRowAndResumed()
    {
        await using var f = await Fixture.CreateAsync();
        var token = Guid.NewGuid().ToString("N");
        var marked = f.PaymentSettings();
        marked.OrderCustomData[TeyaConsumerloansPollingService.RecoveryKey] = TeyaConsumerloansPollingService.RecoveryPending;
        var legacy = await f.LegacyOrders.InsertAsync(100, marked, f.Request.Settings, token, new DefaultHttpContext());
        // A legacy row that was never marked for monitoring is history, not a loan in progress.
        await f.LegacyOrders.InsertAsync(100, f.PaymentSettings(), f.Request.Settings, "old-token", new DefaultHttpContext());

        var requests = await f.RecoveryAsync();

        var resumed = Assert.Single(requests, x => x.PaymentId == legacy.UniqueId);
        Assert.Equal(token, resumed.Token);
        Assert.Equal(marked.SuccessUrl, resumed.RedirectUrl);
        Assert.Equal(2, f.Payments.Payments.Count);
        Assert.Equal(f.OrderId, f.Payments.Payments.Single(x => x.PaymentId == legacy.UniqueId).OrderUniqueId);
    }

    [Fact]
    public void RecoverySavesTheAbsoluteRedirectAndRetainsExistingOrderMetadata()
    {
        var settings = new PaymentSettings
        {
            OrderUniqueId = Guid.NewGuid(), OrderNumber = "123", SuccessUrl = new Uri("/receipt", UriKind.Relative),
        };
        settings.OrderCustomData["ekomPaymentAttemptId"] = "existing-attempt";
        var redirectUrl = new Uri("https://shop.example/receipt?orderId=123");
        TeyaConsumerloansPollingService.EnableRecovery(settings, redirectUrl);
        Assert.Equal(redirectUrl, settings.SuccessUrl);
        Assert.Equal("existing-attempt", settings.OrderCustomData["ekomPaymentAttemptId"]);
    }

    [Fact]
    public void StoredPaymentKeepsTheLoanSettingsTheTokenAndTheProvider()
    {
        var stored = TeyaConsumerloansPollingService.StoredPayment(
            new TeyaConsumerloansSettings { MerchantId = "1234567", TokenValidMinutes = 5 }, "loan-token");

        Assert.Equal("loan-token", stored["teyaToken"]!.ToString());
        Assert.Equal("teyaConsumerloans", stored["ekomPaymentProvider"]!.ToString());
        Assert.Equal("1234567", stored.ToObject<TeyaConsumerloansSettings>()!.MerchantId);
    }

    sealed class Fixture : IAsyncDisposable, IDatabaseFactory
    {
        readonly string _path = Path.Combine(Path.GetTempPath(), $"teya-test-{Guid.NewGuid():N}.sqlite");
        readonly PaymentContext _context = new();
        public Guid OrderId { get; } = Guid.NewGuid();
        public Clock Clock { get; } = new();
        public HttpStub Http { get; } = new();
        public List<LoanActivityEventArgs> Activity { get; } = [];
        public InMemoryOrderPayments Payments { get; } = new();

        /// <summary>The payment rows Teya used before payments were recorded on the Ekom order.</summary>
        public IOrderService LegacyOrders { get; private set; } = null!;
        public TeyaConsumerloansPollingService Service { get; private set; } = null!;
        public PollingRequest Request { get; private set; } = null!;
        public bool Captured => Payments.Captures(Request.PaymentId).Any();

        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture();
            f.LegacyOrders = new OrderService(new PaymentsConfiguration(), f);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:umbracoDbDSN_ProviderName"] = "Microsoft.Data.Sqlite",
            }).Build();
            new EnsureTablesExist(f, configuration, NullLogger<EnsureTablesExist>.Instance).Create();
            f.Service = new TeyaConsumerloansPollingService(
                NullLogger<TeyaConsumerloansPollingService>.Instance, f.Http, f, f.LegacyOrders, f.Payments,
                new PaymentsConfiguration { SendEmailAlerts = false }, null!, new HttpContextAccessor(), f._context);

            var payment = f.PaymentSettings();
            var settings = new TeyaConsumerloansSettings
            {
                ApiBaseUrl = new Uri("https://teya.example/"), Username = "test", Password = "not-a-secret",
                MerchantId = "1234567", TokenValidMinutes = 5, ProgressValidMinutes = 5,
            };
            var token = Guid.NewGuid().ToString("N");
            var created = await f.Payments.CreateAsync(payment, "teyaConsumerloans", 100m,
                TeyaConsumerloansPollingService.StoredPayment(settings, token), createdUtc: f.Clock.GetUtcNow().UtcDateTime);
            f.Request = new PollingRequest { PaymentId = created.PaymentId, Token = token, Settings = settings, RedirectUrl = payment.SuccessUrl };
            LoanEvents.ActivityAsync += f.LogAsync;
            return f;
        }

        public PaymentSettings PaymentSettings()
        {
            var payment = new PaymentSettings
            {
                OrderUniqueId = OrderId, OrderNumber = "123", OrderName = "Order 123",
                Orders = [], Currency = "ISK", Store = "test", Language = "is-IS",
                SuccessUrl = new Uri($"https://shop.example/receipt?orderId={OrderId}"),
                CancelUrl = new Uri("https://shop.example/cancel"), PaymentProviderName = "Teya",
            };
            payment.OrderCustomData["ekomOrderUniqueId"] = OrderId.ToString();
            TeyaConsumerloansPollingService.EnableRecovery(payment, payment.SuccessUrl);
            return payment;
        }

        public Task PollAsync(CancellationToken ct = default) => Service.PollOrderAsync(Request, ct, Clock);

        /// <summary>The loans the service would pick up again after a restart at the test clock's time.</summary>
        public Task<IReadOnlyList<PollingRequest>> RecoveryAsync() => Service.GetRecoveryRequestsAsync(default, Clock);

        public void SetAge(TimeSpan age)
            => Payments.Payments.Single(x => x.PaymentId == Request.PaymentId).CreatedUtc = Clock.GetUtcNow().UtcDateTime - age;

        Task LogAsync(object sender, LoanActivityEventArgs args) { Activity.Add(args); return Task.CompletedTask; }
        public DbContext GetDatabase() => new(ProviderName.SQLiteMS, $"Data Source={_path};Pooling=False");
        public SqlConnection GetSqlConnection() => throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            LoanEvents.ActivityAsync -= LogAsync;
            Service.Dispose();
            Http.Dispose();
            _context.Dispose();
            File.Delete(_path);
            return ValueTask.CompletedTask;
        }
    }

    sealed class Clock : FakeTimeProvider
    {
        readonly Channel<bool> _delays = Channel.CreateUnbounded<bool>();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            if (dueTime == TeyaConsumerloansPollingService.PollingInterval)
            {
                _delays.Writer.TryWrite(true);
            }
            return timer;
        }

        public async Task WaitForPollDelayAsync()
        {
            await _delays.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    sealed class PaymentContext : IPaymentExecutionContext, IDisposable
    {
        public IDisposable EnsureContext() => this;
        public void Dispose() { }
    }

    sealed class HttpStub : HttpMessageHandler, IHttpClientFactory
    {
        public string Status { get; set; } = "INPROGRESS";
        public Dictionary<string, string> StatusByToken { get; } = [];
        public string? StatusFailure { get; set; }
        public bool WaitForCancellation { get; set; }
        public bool FailValidation { get; set; }
        public int Validations;
        public int StatusChecks;
        public string? RedirectUrl { get; private set; }
        public TaskCompletionSource RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Put)
            {
                Validations++;
                if (FailValidation)
                {
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }
                var json = JsonConvert.DeserializeObject<Dictionary<string, string>>(await request.Content!.ReadAsStringAsync(cancellationToken))!;
                RedirectUrl = json["RedirectUrl"];
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"contractNumber\":\"1234\",\"authorizationNumber\":\"5678\"}", System.Text.Encoding.UTF8, "application/json"),
                };
            }
            StatusChecks++;
            RequestStarted.TrySetResult();
            if (WaitForCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            if (StatusFailure == "network")
            {
                throw new HttpRequestException("Simulated connection failure");
            }
            if (StatusFailure == "timeout")
            {
                throw new TaskCanceledException("Simulated HTTP client timeout");
            }
            if (StatusFailure != null)
            {
                return new HttpResponseMessage((HttpStatusCode)int.Parse(StatusFailure, System.Globalization.CultureInfo.InvariantCulture));
            }
            var token = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri!.Query)["token"].ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(StatusByToken.TryGetValue(token, out var status) ? status : Status),
            };
        }
    }
}
