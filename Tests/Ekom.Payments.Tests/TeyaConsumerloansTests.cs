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
        var completions = 0;
        Task Complete(object _, SuccessEventArgs e) { completions++; return Task.CompletedTask; }
        Events.SuccessAsync += Complete;
        try
        {
            var polling = f.PollAsync();
            await f.Clock.WaitForPollDelayAsync();
            f.Http.Status = "SUCCESS";
            f.Clock.Advance(TimeSpan.FromMinutes(6));
            await polling.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True((await f.Orders.GetAsync(f.Request.OrderId))!.Paid);
            Assert.Equal(1, f.Http.Validations);
            Assert.Equal(1, completions);
            Assert.Contains(f.Activity, x => x.Message.Contains("INPROGRESS -> SUCCESS", StringComparison.Ordinal));
            Assert.Contains(f.Activity, x => x.IsSuccess);
            Assert.All(f.Activity, x => Assert.Equal(f.Request.OrderId, x.PaymentId));
            Assert.All(f.Activity, x => Assert.Equal(f.OrderId, x.PaymentSettings.OrderUniqueId));
            Assert.DoesNotContain(f.Activity, x => x.Message.Contains(f.Request.Token, StringComparison.Ordinal));
            Assert.Equal($"{f.Request.RedirectUrl}&token={f.Request.Token}", f.Http.RedirectUrl);
        }
        finally
        {
            Events.SuccessAsync -= Complete;
        }
    }

    [Theory]
    [InlineData("CANCELED")]
    [InlineData("FAILED")]
    [InlineData("TOKENEXPIRED")]
    [InlineData("PROGRESSEXPIRED")]
    public async Task TerminalStatusStopsWithoutValidatingOrMarkingPaymentPaid(string status)
    {
        await using var f = await Fixture.CreateAsync();
        f.Http.Status = status;
        await f.PollAsync();
        Assert.False((await f.Orders.GetAsync(f.Request.OrderId))!.Paid);
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
        Assert.True((await f.Orders.GetAsync(f.Request.OrderId))!.Paid);
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
    }

    [Fact]
    public async Task TimeoutIsLoggedWithoutTreatingTheOrderAsPaid()
    {
        await using var f = await Fixture.CreateAsync();
        var polling = f.PollAsync();
        await f.Clock.WaitForPollDelayAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(30));
        await polling.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False((await f.Orders.GetAsync(f.Request.OrderId))!.Paid);
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
    public async Task ValidationFailureUsesExistingErrorEventAndIsNotAutomaticallyRetried()
    {
        await using var f = await Fixture.CreateAsync();
        f.Http.Status = "SUCCESS";
        f.Http.FailValidation = true;
        var errors = 0;
        Task Error(object _, ErrorEventArgs e) { errors++; return Task.CompletedTask; }
        Events.ErrorAsync += Error;
        try
        {
            await f.PollAsync();
            Assert.Equal(1, f.Http.Validations);
            Assert.Equal(1, errors);
            Assert.False((await f.Orders.GetAsync(f.Request.OrderId))!.Paid);
            Assert.Contains(f.Activity, x => x.Level == LogLevel.Error);
        }
        finally
        {
            Events.ErrorAsync -= Error;
        }
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
    public async Task CompletionCallbackFailureIsReportedWithoutRevalidatingOrReplayingIt()
    {
        await using var f = await Fixture.CreateAsync();
        f.Http.Status = "SUCCESS";
        var completions = 0;
        Task Complete(object _, SuccessEventArgs e)
        {
            completions++;
            throw new InvalidOperationException("Simulated checkout failure");
        }
        Events.SuccessAsync += Complete;
        try
        {
            await f.PollAsync();
            Assert.Equal(1, f.Http.Validations);
            Assert.Equal(1, completions);
            Assert.True((await f.Orders.GetAsync(f.Request.OrderId))!.Paid);
            Assert.Contains(f.Activity, x => x.Level == LogLevel.Error);
        }
        finally
        {
            Events.SuccessAsync -= Complete;
        }
    }

    [Fact]
    public async Task PreviouslyPaidAttemptIsNotValidatedAgain()
    {
        await using var f = await Fixture.CreateAsync();
        var order = (await f.Orders.GetAsync(f.Request.OrderId))!;
        order.Paid = true;
        await f.Orders.UpdateAsync(order);
        f.Http.Status = "SUCCESS";
        await f.PollAsync();
        Assert.Equal(0, f.Http.Validations);
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
            Assert.True((await f.Orders.GetAsync(f.Request.OrderId))!.Paid);
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
        await f.SetAgeAsync(TimeSpan.FromMinutes(6));
        var request = Assert.Single(await f.Service.GetRecoveryRequestsAsync());
        Assert.Equal(f.Clock.GetUtcNow().AddMinutes(4).AddSeconds(30), request.RecoveryDeadlineUtc);
        var polling = f.Service.PollOrderAsync(request, CancellationToken.None, f.Clock);
        await f.Clock.WaitForPollDelayAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(30));
        await polling.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(f.Activity, x => x.Message.Contains("270 seconds remaining", StringComparison.Ordinal));
        Assert.Contains(f.Activity, x => x.Message.Contains("timed out", StringComparison.Ordinal));
        Assert.Equal(TeyaConsumerloansPollingService.RecoveryPending, await f.RecoveryStateAsync());
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
        Assert.True((await f.Orders.GetAsync(f.Request.OrderId))!.Paid);
        Assert.Equal(1, f.Http.Validations);
        Assert.DoesNotContain(f.Activity, x => x.Message.Contains("timed out", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RestartDuringBufferResumesPollingForOnlyTheRemainingBuffer()
    {
        await using var f = await Fixture.CreateAsync();
        await f.SetAgeAsync(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(15));
        var request = Assert.Single(await f.Service.GetRecoveryRequestsAsync());
        Assert.Equal(f.Clock.GetUtcNow().AddSeconds(15), request.RecoveryDeadlineUtc);
        var polling = f.Service.PollOrderAsync(request, CancellationToken.None, f.Clock);
        await f.Clock.WaitForPollDelayAsync();
        f.Clock.Advance(TimeSpan.FromSeconds(15));
        await polling.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(f.Activity, x => x.Message.Contains("15 seconds remaining", StringComparison.Ordinal));
        Assert.Contains(f.Activity, x => x.Message.Contains("timed out", StringComparison.Ordinal));
        Assert.DoesNotContain(f.Activity, x => x.Message.Contains("checking the loan status once", StringComparison.Ordinal));
        Assert.Equal(TeyaConsumerloansPollingService.RecoveryPending, await f.RecoveryStateAsync());
    }

    [Fact]
    public async Task RecoveryDeadlineIsRecheckedWhenAnAttemptWaitedInTheQueue()
    {
        await using var f = await Fixture.CreateAsync();
        await f.SetAgeAsync(TimeSpan.FromMinutes(6));
        var request = Assert.Single(await f.Service.GetRecoveryRequestsAsync());
        f.Clock.Advance(TimeSpan.FromMinutes(5));
        await f.Service.PollOrderAsync(request, CancellationToken.None, f.Clock);
        Assert.Equal(1, f.Http.StatusChecks);
        Assert.Equal(0, f.Http.Validations);
        Assert.Contains(f.Activity, x => x.Message.Contains("manual review", StringComparison.Ordinal));
        Assert.Empty(await f.Service.GetRecoveryRequestsAsync());
    }

    [Fact]
    public async Task LoanCompletedWhileSiteWasDownIsValidatedOnceAfterItsWindow()
    {
        await using var f = await Fixture.CreateAsync();
        await f.SetAgeAsync(TimeSpan.FromMinutes(30));
        f.Http.Status = "SUCCESS";
        var request = Assert.Single(await f.Service.GetRecoveryRequestsAsync());
        await f.Service.PollOrderAsync(request, CancellationToken.None, f.Clock);
        Assert.Equal(1, f.Http.StatusChecks);
        Assert.Equal(1, f.Http.Validations);
        Assert.Equal($"{f.Request.RedirectUrl}&token={f.Request.Token}", f.Http.RedirectUrl);
        Assert.True((await f.Orders.GetAsync(f.Request.OrderId))!.Paid);
        Assert.Equal(TeyaConsumerloansPollingService.RecoveryFinished, await f.RecoveryStateAsync());
        Assert.Empty(await f.Service.GetRecoveryRequestsAsync());
    }

    [Theory]
    [InlineData("CANCELED")]
    [InlineData("FAILED")]
    [InlineData("TOKENEXPIRED")]
    [InlineData("PROGRESSEXPIRED")]
    public async Task OverdueTerminalAttemptIsCheckedOnceAndNotRecoveredAgain(string status)
    {
        await using var f = await Fixture.CreateAsync();
        await f.SetAgeAsync(TimeSpan.FromMinutes(30));
        f.Http.Status = status;
        var request = Assert.Single(await f.Service.GetRecoveryRequestsAsync());
        await f.Service.PollOrderAsync(request, CancellationToken.None, f.Clock);
        Assert.Equal(1, f.Http.StatusChecks);
        Assert.Equal(0, f.Http.Validations);
        Assert.False((await f.Orders.GetAsync(f.Request.OrderId))!.Paid);
        Assert.Equal(TeyaConsumerloansPollingService.RecoveryFinished, await f.RecoveryStateAsync());
        Assert.Empty(await f.Service.GetRecoveryRequestsAsync());
    }

    [Theory]
    [InlineData("CREATED")]
    [InlineData("INPROGRESS")]
    [InlineData("PENDING")]
    public async Task OverdueNonFinalAttemptGetsOneCheckAndAManualReviewMessage(string status)
    {
        await using var f = await Fixture.CreateAsync();
        await f.SetAgeAsync(TimeSpan.FromMinutes(30));
        f.Http.Status = status;
        var request = Assert.Single(await f.Service.GetRecoveryRequestsAsync());
        await f.Service.PollOrderAsync(request, CancellationToken.None, f.Clock);
        Assert.Equal(1, f.Http.StatusChecks);
        Assert.Equal(0, f.Http.Validations);
        Assert.False((await f.Orders.GetAsync(f.Request.OrderId))!.Paid);
        Assert.Contains(f.Activity, x => x.Level == LogLevel.Warning && x.Message.Contains("manual review", StringComparison.Ordinal));
        Assert.Equal(TeyaConsumerloansPollingService.RecoveryFinished, await f.RecoveryStateAsync());
        Assert.Empty(await f.Service.GetRecoveryRequestsAsync());
    }

    [Theory]
    [InlineData("503")]
    [InlineData("network")]
    [InlineData("timeout")]
    public async Task FailedOverdueCheckIsNotRetriedImmediatelyButRemainsAvailableForNextRestart(string failure)
    {
        await using var f = await Fixture.CreateAsync();
        await f.SetAgeAsync(TimeSpan.FromMinutes(30));
        f.Http.StatusFailure = failure;
        var request = Assert.Single(await f.Service.GetRecoveryRequestsAsync());
        await f.Service.PollOrderAsync(request, CancellationToken.None, f.Clock);
        Assert.Equal(1, f.Http.StatusChecks);
        Assert.Equal(0, f.Http.Validations);
        Assert.Equal(TeyaConsumerloansPollingService.RecoveryPending, await f.RecoveryStateAsync());
        var retry = Assert.Single(await f.Service.GetRecoveryRequestsAsync());
        f.Http.StatusFailure = null;
        f.Http.Status = "SUCCESS";
        await f.Service.PollOrderAsync(retry, CancellationToken.None, f.Clock);
        Assert.Equal(2, f.Http.StatusChecks);
        Assert.True((await f.Orders.GetAsync(f.Request.OrderId))!.Paid);
    }

    [Fact]
    public async Task RecoveryDoesNotImportUnmarkedHistoricalAttempts()
    {
        await using var f = await Fixture.CreateAsync(marked: false);
        await f.SetAgeAsync(TimeSpan.FromMinutes(30));
        Assert.Empty(await f.Service.GetRecoveryRequestsAsync());
    }

    [Fact]
    public async Task RecoverySkipsPaidAttemptsEvenWhenSuccessCallbackPreviouslyFailed()
    {
        await using var f = await Fixture.CreateAsync();
        f.Http.Status = "SUCCESS";
        Task Fail(object _, SuccessEventArgs e) => throw new InvalidOperationException("Simulated callback failure");
        Events.SuccessAsync += Fail;
        try
        {
            await f.PollAsync();
        }
        finally
        {
            Events.SuccessAsync -= Fail;
        }
        Assert.True((await f.Orders.GetAsync(f.Request.OrderId))!.Paid);
        Assert.Empty(await f.Service.GetRecoveryRequestsAsync());
    }

    [Fact]
    public async Task AnInvalidRecoveryRecordDoesNotPreventOtherAttemptsFromResuming()
    {
        await using var f = await Fixture.CreateAsync();
        await f.SetAgeAsync(TimeSpan.FromMinutes(30));
        var original = (await f.Orders.GetAsync(f.Request.OrderId))!;
        var invalid = await f.Orders.InsertAsync(100, original.EkomPaymentSettings, new { }, "invalid-token", new DefaultHttpContext());
        var request = Assert.Single(await f.Service.GetRecoveryRequestsAsync());
        Assert.Equal(original.UniqueId, request.OrderId);
        Assert.NotEqual(invalid.UniqueId, request.OrderId);
    }

    [Fact]
    public async Task RecoveryChecksEveryAttemptNotJustTheNewestForAnOrder()
    {
        await using var f = await Fixture.CreateAsync();
        await f.SetAgeAsync(TimeSpan.FromMinutes(30));
        var original = (await f.Orders.GetAsync(f.Request.OrderId))!;
        var nextToken = Guid.NewGuid().ToString("N");
        var later = await f.Orders.InsertAsync(100, original.EkomPaymentSettings, f.Request.Settings, nextToken, new DefaultHttpContext());
        later = (await f.Orders.GetAsync(later.UniqueId))!;
        later.Date = original.Date.AddMinutes(1);
        await f.Orders.UpdateAsync(later);
        f.Http.StatusByToken[f.Request.Token] = "SUCCESS";
        f.Http.StatusByToken[nextToken] = "PROGRESSEXPIRED";
        var requests = await f.Service.GetRecoveryRequestsAsync();
        Assert.Equal(2, requests.Count);
        foreach (var request in requests)
        {
            await f.Service.PollOrderAsync(request, CancellationToken.None, f.Clock);
        }
        Assert.True((await f.Orders.GetAsync(original.UniqueId))!.Paid);
        Assert.False((await f.Orders.GetAsync(later.UniqueId))!.Paid);
        Assert.Equal(1, f.Http.Validations);
        Assert.Empty(await f.Service.GetRecoveryRequestsAsync());
    }

    [Fact]
    public async Task StartupRecoveryWaitsUntilApplicationEventSubscribersAreReady()
    {
        await using var f = await Fixture.CreateAsync();
        await f.SetAgeAsync(TimeSpan.FromMinutes(30));
        using var ready = new CancellationTokenSource();
        f.Service.ApplicationStarted = ready.Token;
        f.Http.Status = "SUCCESS";
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task Complete(object _, SuccessEventArgs e) { delivered.TrySetResult(); return Task.CompletedTask; }
        Events.SuccessAsync += Complete;
        try
        {
            await f.Service.StartAsync(CancellationToken.None);
            Assert.Equal(0, f.Http.StatusChecks);
            ready.Cancel();
            await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await f.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            Events.SuccessAsync -= Complete;
        }
        Assert.Equal(1, f.Http.StatusChecks);
        Assert.True((await f.Orders.GetAsync(f.Request.OrderId))!.Paid);
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
        Assert.Equal(TeyaConsumerloansPollingService.RecoveryPending, settings.OrderCustomData[TeyaConsumerloansPollingService.RecoveryKey]);
    }

    sealed class Fixture : IAsyncDisposable, IDatabaseFactory
    {
        readonly string _path = Path.Combine(Path.GetTempPath(), $"teya-test-{Guid.NewGuid():N}.sqlite");
        readonly PaymentContext _context = new();
        public Guid OrderId { get; } = Guid.NewGuid();
        public Clock Clock { get; } = new();
        public HttpStub Http { get; } = new();
        public List<LoanActivityEventArgs> Activity { get; } = [];
        public IOrderService Orders { get; private set; } = null!;
        public TeyaConsumerloansPollingService Service { get; private set; } = null!;
        public PollingRequest Request { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync(bool marked = true)
        {
            var f = new Fixture();
            f.Orders = new OrderService(new PaymentsConfiguration(), f);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:umbracoDbDSN_ProviderName"] = "Microsoft.Data.Sqlite",
            }).Build();
            new EnsureTablesExist(f, configuration, NullLogger<EnsureTablesExist>.Instance).Create();
            f.Service = new TeyaConsumerloansPollingService(
                NullLogger<TeyaConsumerloansPollingService>.Instance, f.Http, f, f.Orders,
                new PaymentsConfiguration(), null!, new HttpContextAccessor(), f._context);

            var payment = new PaymentSettings
            {
                OrderUniqueId = f.OrderId, OrderNumber = "123", OrderName = "Order 123",
                Orders = [], Currency = "ISK", Store = "test", Language = "is-IS",
                SuccessUrl = new Uri($"https://shop.example/receipt?orderId={f.OrderId}"),
                CancelUrl = new Uri("https://shop.example/cancel"), PaymentProviderName = "Teya",
            };
            payment.OrderCustomData["ekomOrderUniqueId"] = f.OrderId.ToString();
            if (marked)
            {
                TeyaConsumerloansPollingService.EnableRecovery(payment, payment.SuccessUrl);
            }
            var settings = new TeyaConsumerloansSettings
            {
                ApiBaseUrl = new Uri("https://teya.example/"), Username = "test", Password = "not-a-secret",
                MerchantId = "1234567", TokenValidMinutes = 5, ProgressValidMinutes = 5,
            };
            var token = Guid.NewGuid().ToString("N");
            var order = await f.Orders.InsertAsync(100, payment, settings, token, new DefaultHttpContext());
            f.Request = new PollingRequest { OrderId = order.UniqueId, Token = token, Settings = settings, RedirectUrl = payment.SuccessUrl };
            LoanEvents.ActivityAsync += f.LogAsync;
            return f;
        }

        public Task PollAsync(CancellationToken ct = default) => Service.PollOrderAsync(Request, ct, Clock);
        public async Task SetAgeAsync(TimeSpan age)
        {
            var order = (await Orders.GetAsync(Request.OrderId))!;
            order.Date = Clock.GetLocalNow().DateTime - age;
            await Orders.UpdateAsync(order);
        }

        public async Task<string> RecoveryStateAsync()
        {
            var order = (await Orders.GetAsync(Request.OrderId))!;
            return order.EkomPaymentSettings.OrderCustomData[TeyaConsumerloansPollingService.RecoveryKey];
        }
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
