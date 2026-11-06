#pragma warning disable OPENAI001

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using FluentAssertions;
using Hangfire;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using OpenAI.Responses;
using Orbit.Api.Hosting;
using Orbit.Api.Idempotency;
using Orbit.Application.Calendar.Services;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Common;
using Orbit.Application.Chat.Models;
using Orbit.Application.Proactive.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.BackgroundJobs;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.BackgroundJobs;
using Orbit.Infrastructure.Tests.Controllers;
using Orbit.Infrastructure.Tests.Persistence;
using Orbit.Infrastructure.Tests.Services;

namespace Orbit.Infrastructure.Tests.AI;

/// <summary>
/// A caller that cancels its own token (a superseded daily summary refresh, a closed chat stream) did not
/// see an AI failure, so the usage recorder and the logs must not report one. A completion deadline that
/// expires while the provider is still working is a real failure and keeps its failed record and warnings,
/// also when the caller owns it (the selector, chat turn, proactive user and scheduled job budgets).
/// </summary>
public class AiCallerCancellationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SelectorBudget = Timeout / 2;

    [Theory]
    [InlineData("text")]
    [InlineData("json")]
    [InlineData("budget-text")]
    [InlineData("budget-json")]
    [InlineData("chat")]
    [InlineData("stream")]
    [InlineData("selector")]
    public async Task CallerCancellation_DuringProviderCall_RecordsCancelledWithoutWarning(string kind)
    {
        var harness = new Harness();
        using var caller = new CancellationTokenSource();
        var call = harness.Start(kind, caller.Token);
        await harness.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await caller.CancelAsync();

        var thrown = await Record.ExceptionAsync(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
        thrown.Should().BeAssignableTo<OperationCanceledException>("the caller still observes its own cancellation");
        var telemetry = harness.SingleTelemetry();
        telemetry.IsError.Should().BeFalse("a caller cancellation is not an AI failure");
        telemetry.IsCancelled.Should().BeTrue();
        telemetry.HasUsage.Should().BeFalse();
        harness.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Information,
            "a superseded call must not reach the production logs as a failure");
    }

    [Theory]
    [InlineData("text")]
    [InlineData("json")]
    [InlineData("budget-text")]
    [InlineData("budget-json")]
    [InlineData("chat")]
    [InlineData("stream")]
    public async Task CompletionDeadline_DuringProviderCall_RecordsFailedWithWarning(string kind)
    {
        var harness = new Harness();
        using var caller = new CancellationTokenSource();
        var call = harness.Start(kind, caller.Token);
        await harness.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        harness.Clock.Advance(Timeout);

        var thrown = await Record.ExceptionAsync(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
        thrown.Should().BeAssignableTo<OperationCanceledException>();
        caller.IsCancellationRequested.Should().BeFalse();
        var telemetry = harness.SingleTelemetry();
        telemetry.IsError.Should().BeTrue("a deadline that expires during the provider call is a failure");
        telemetry.IsCancelled.Should().BeFalse();
        telemetry.HasUsage.Should().BeFalse();
        harness.Entries.Should().Contain(entry => entry.Level == LogLevel.Warning
            && entry.Message.StartsWith("AI attempt 1 failed (", StringComparison.Ordinal));
        if (kind is not ("text" or "json"))
            harness.Entries.Should().Contain(entry => entry.Level == LogLevel.Warning
                && entry.Message == "AI completion failed at completion with cancelled");
    }

    [Fact]
    public async Task SelectorBudget_ExpiresOnTheCompletionClock_RecordsFailedWithWarning()
    {
        var harness = new Harness();
        using var caller = new CancellationTokenSource();
        var call = harness.Start("selector", caller.Token);
        await harness.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        harness.Clock.Advance(SelectorBudget);

        var thrown = await Record.ExceptionAsync(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
        thrown.Should().BeAssignableTo<OperationCanceledException>("the caller budget is the deadline of the completion scope");
        caller.IsCancellationRequested.Should().BeFalse();
        AssertDeadlineFailure(harness);
    }

    [Fact]
    public async Task TurnDeadline_ExpiresOnTheCompletionClock_RecordsFailedWithWarning()
    {
        var harness = new Harness();
        var userId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        using var services = new ChatTurnStreamTestServices(mediator, userId);
        services.Provider.GetRequiredService<IOptions<ShutdownDrainOptions>>().Value.Timeout = SelectorBudget;
        mediator.Send(Arg.Any<ProcessUserChatCommand>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await harness.Start("stream", call.Arg<CancellationToken>());
            return Result.Success(new ChatResponse("Reply", []));
        });
        var stream = ActivatorUtilities.CreateInstance<ChatTurnStream>(services.Provider,
            new CollectingLogger<ChatTurnStream>(harness.Entries), (TimeProvider)harness.Clock);
        var controller = ChatTurnStreamTests.CreateController(services, userId, "turn-deadline");
        var run = stream.RunAsync(new ProcessUserChatCommand(userId, "Hello"), controller.HttpContext, "turn-deadline", default);
        await harness.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        harness.Clock.Advance(SelectorBudget);

        await run.WaitAsync(TimeSpan.FromSeconds(5));
        var terminal = ChatTurnStreamTests.Events(controller).Last();
        terminal.GetProperty("code").GetString().Should().Be("CHAT_TURN_INTERRUPTED");
        AssertDeadlineFailure(harness);
        harness.Entries.Should().Contain(entry => entry.Level == LogLevel.Warning
            && entry.Message == $"Chat turn {terminal.GetProperty("turnId").GetGuid()} interrupted by its deadline");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProactiveUserBudget_ExpiryRecordsFailed_JobCancellationRecordsCancelled(bool budgetExpires)
    {
        var harness = new Harness();
        var today = new DateOnly(2030, 10, 14);
        using var factory = new SqliteOrbitDbContextFactory(Path.Combine(Path.GetTempPath(), $"proactive-budget-{Guid.NewGuid():N}.db"));
        using (var seed = factory.CreateContext())
        {
            var user = User.Create("Budget user", "budget@example.com").Value;
            user.GrantLifetimePro();
            user.SetTimeZone("UTC");
            seed.Add(user);
            seed.Add(Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, user.Id, "Read", FrequencyUnit.Day, 1, today)).Value);
            await seed.SaveChangesAsync();
        }
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(Arg.Any<string?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(today);
        var flags = Substitute.For<IFeatureFlagService>();
        flags.GetEnabledKeysForUsersAsync(Arg.Any<IReadOnlyCollection<User>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyCollection<User>>().ToDictionary(user => user.Id, _ => (IReadOnlyList<string>)Array.Empty<string>()));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BackgroundServices:ProactiveUserTimeoutSeconds"] = budgetExpires ? "1" : "600"
        }).Build();
        using var provider = new ServiceCollection().AddLogging().AddScoped(_ => factory.CreateContext())
            .AddSingleton<IConfiguration>(configuration).AddSingleton(dates).AddSingleton(flags)
            .AddSingleton<IProactiveCheckinMessageService>(new AiProactiveCheckinMessageService(harness.Budgeted,
                new CollectingLogger<AiProactiveCheckinMessageService>(harness.Entries)))
            .AddSingleton(Substitute.For<IBackgroundJobClient>()).AddSingleton(Substitute.For<ICalendarEventFetcher>())
            .AddSingleton(Substitute.For<IGoogleTokenService>()).AddSingleton(Substitute.For<IProductAnalytics>())
            .AddSingleton<TimeProvider>(new ProactiveEngineServiceTests.FixedClock(today.ToDateTime(new TimeOnly(19, 0), DateTimeKind.Utc)))
            .AddScoped<IProactiveEngine, ProactiveEngineService>().BuildServiceProvider();
        using var job = new CancellationTokenSource();
        using var scope = provider.CreateScope();
        var run = scope.ServiceProvider.GetRequiredService<IProactiveEngine>().EvaluateAsync(job.Token);
        await harness.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        if (!budgetExpires)
            await job.CancelAsync();

        var thrown = await Record.ExceptionAsync(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
        if (budgetExpires)
        {
            thrown.Should().BeNull("the engine ends the expired user and finishes the run");
            AssertDeadlineFailure(harness);
            return;
        }
        thrown.Should().BeAssignableTo<OperationCanceledException>();
        var telemetry = harness.SingleTelemetry();
        telemetry.IsError.Should().BeFalse("a cancelled job is not an AI failure");
        telemetry.IsCancelled.Should().BeTrue();
        harness.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Information);
    }

    [Fact]
    public async Task ScheduledJobTimeout_ExpiresDuringProviderCall_RecordsFailedWithWarning()
    {
        var harness = new Harness();
        var jobClock = new CompletionTestClock();
        var job = new AiJob(harness);
        var health = new BackgroundServiceHealthCheck(Substitute.For<IServiceScopeFactory>(), [job], TimeProvider.System,
            new CollectingLogger<BackgroundServiceHealthCheck>(harness.Entries));
        var runner = new ScheduledJobRunner([job], new TestScheduledJobLeaseProvider(), health,
            new ConfigurationBuilder().Build(), jobClock);
        var run = runner.RunAsync(job.Name, default);
        await harness.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        jobClock.Advance(TimeSpan.FromMinutes(120));

        var thrown = await Record.ExceptionAsync(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        thrown.Should().BeAssignableTo<OperationCanceledException>();
        AssertDeadlineFailure(harness);
    }

    private static void AssertDeadlineFailure(Harness harness)
    {
        var telemetry = harness.SingleTelemetry();
        telemetry.IsError.Should().BeTrue("a deadline that the caller owns is still a failure");
        telemetry.IsCancelled.Should().BeFalse();
        telemetry.HasUsage.Should().BeFalse();
        harness.Entries.Should().Contain(entry => entry.Level == LogLevel.Warning
            && entry.Message.StartsWith("AI attempt 1 failed (", StringComparison.Ordinal));
        harness.Entries.Should().Contain(entry => entry.Level == LogLevel.Warning
            && entry.Message == "AI completion failed at completion with cancelled");
    }

    [Theory]
    [InlineData("caller", true)]
    [InlineData("outer-deadline", false)]
    [InlineData("inner-deadline", false)]
    public void NestedScope_ClassifiesTheCancellationSource(string source, bool callerCancelled)
    {
        var clock = new CompletionTestClock();
        using var caller = new CancellationTokenSource();
        using var outer = new AiCompletionClient.CompletionCancellation(Timeout, clock, caller.Token);
        clock.Advance(Timeout / 2);
        using var inner = new AiCompletionClient.CompletionCancellation(
            source == "inner-deadline" ? Timeout / 4 : Timeout, clock, outer.Token);
        AiCompletionClient.CompletionCancellation.CurrentCallerCancelled.Should().BeFalse();

        if (source == "caller")
            caller.Cancel();
        else
            clock.Advance(source == "outer-deadline" ? Timeout / 2 : Timeout / 4);

        inner.Token.IsCancellationRequested.Should().BeTrue();
        AiCompletionClient.CompletionCancellation.CurrentCallerCancelled.Should().Be(callerCancelled);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BoundDeadline_ReadsItsExpiryAsADeadlineAndRestoresTheEnclosingBinding(bool bound)
    {
        var clock = new CompletionTestClock();
        using var turn = new CancellationTokenSource();
        var binding = bound ? AiCallDeadline.Bind(turn.Token) : null;
        using (var scope = new AiCompletionClient.CompletionCancellation(Timeout, clock, turn.Token))
        {
            turn.Cancel();
            scope.Token.IsCancellationRequested.Should().BeTrue();
            AiCompletionClient.CompletionCancellation.CurrentCallerCancelled.Should().Be(!bound);
        }

        AiCallDeadline.Current.Should().BeSameAs(binding);
        binding?.Dispose();
        AiCallDeadline.Current.Should().BeNull();
    }

    [Fact]
    public void Scope_OnUnrelatedToken_UsesItsOwnCallerAndRestoresThePreviousScope()
    {
        var clock = new CompletionTestClock();
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        using var outer = new AiCompletionClient.CompletionCancellation(Timeout, clock, first.Token);
        using (new AiCompletionClient.CompletionCancellation(Timeout, clock, second.Token))
        {
            first.Cancel();
            AiCompletionClient.CompletionCancellation.CurrentCallerCancelled.Should().BeFalse(
                "the inner scope belongs to a different caller");
        }

        AiCompletionClient.CompletionCancellation.CurrentCallerCancelled.Should().BeTrue();
    }

    private sealed class Harness
    {
        private readonly AiCompletionClient _client;
        private readonly BudgetedAiCompletionClient _budgeted;
        private readonly AiIntentService _intent;

        internal Harness()
        {
            var responses = new ResponsesClient(new ApiKeyCredential("test-key"), new ResponsesClientOptions
            {
                Endpoint = new Uri("https://orbit.test/v1"),
                RetryPolicy = new AiRetryLoggingPolicy(2, new CollectingLogger<AiRetryLoggingPolicy>(Entries)),
                Transport = new HttpClientPipelineTransport(new HttpClient(Handler))
            });
            _client = new AiCompletionClient(responses, new CollectingLogger<AiCompletionClient>(Entries), Usage,
                Timeout, timeProvider: Clock);
            _budgeted = AiBudgetTestDouble.Wrap(_client, new CollectingLogger<BudgetedAiCompletionClient>(Entries));
            _intent = new AiIntentService(_budgeted, Usage, new CollectingLogger<AiIntentService>(Entries));
        }

        internal BudgetedAiCompletionClient Budgeted => _budgeted;
        internal BlockingHandler Handler { get; } = new();
        internal CompletionTestClock Clock { get; } = new();
        internal IAiUsageRecorder Usage { get; } = Substitute.For<IAiUsageRecorder>();
        internal List<(LogLevel Level, string Message)> Entries { get; } = [];

        internal Task Start(string kind, CancellationToken token) => kind switch
        {
            "text" => _client.CompleteTextAsync("system", "user", purpose: "daily_summary", cancellationToken: token),
            "json" => _client.CompleteJsonAsync<Probe>("system", "user", cancellationToken: token),
            "budget-text" => _budgeted.CompleteTextAsync("system", "user", purpose: "daily_summary", cancellationToken: token),
            "budget-json" => _budgeted.CompleteJsonAsync<Probe>("system", "user", cancellationToken: token),
            "selector" => _intent.SendWithToolsAsync(new AiToolRequest("user", "system", [])
            {
                Purpose = "offer_selection",
                Timeout = SelectorBudget
            }, cancellationToken: token),
            _ => _intent.SendWithToolsAsync(new AiToolRequest("user", "system", []),
                kind == "stream" ? _ => Task.CompletedTask : null, cancellationToken: token)
        };

        internal AiCallTelemetry SingleTelemetry()
        {
            var arguments = Usage.ReceivedCalls().Should().ContainSingle().Which.GetArguments();
            arguments.Skip(2).Take(4).Should().OnlyContain(value => (long)value! == 0);
            return (AiCallTelemetry)arguments[8]!;
        }
    }

    private sealed class AiJob(Harness harness) : IScheduledJob
    {
        public string Name => "ai-job";
        public string CronExpression => "* * * * *";
        public Task RunAsync(CancellationToken cancellationToken) => harness.Start("budget-json", cancellationToken);
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
            throw new UnreachableException();
        }
    }

    private sealed class CollectingLogger<T>(List<(LogLevel Level, string Message)> entries) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (entries)
                entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private sealed record Probe(string Value);
}
