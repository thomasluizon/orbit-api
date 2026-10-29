using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Caching.Memory;
using System.Text.Json;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Behaviors;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Validators;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Validators;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public sealed partial class BulkCreateAgentOperationTests : IDisposable
{
    private readonly SqliteOrbitDbContextFactory _factory = new();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IAgentAuditService _audit = Substitute.For<IAgentAuditService>();
    private readonly PendingAgentOperationStore _pending;
    private readonly AgentOperationExecutor _executor;
    private readonly ServiceProvider _provider;
    private readonly PendingOperationChangePreviewer _previewer;
    private readonly AiToolRegistry _registry;
    private readonly Guid _userId;
    private AgentAuditEntry? _lastAudit;
    private readonly RecordingLogger _logger = new();
    private readonly AfterWriteFailureBehavior _afterWriteFailure = new();
    private readonly RescheduleFailureBehavior _rescheduleFailure = new();

    public BulkCreateAgentOperationTests()
    {
        var context = _factory.Context;
        var user = User.Create("Alex", "alex@example.com").Value;
        _userId = user.Id;
        context.Users.Add(user);
        context.SaveChanges();

        var settings = Options.Create(new AgentPlatformSettings());
        _pending = new PendingAgentOperationStore(context, settings);
        var bridge = Substitute.For<IAgentStepUpAuthorizationBridge>();
        bridge.GetRequiredConfirmationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((AgentConfirmationRequirement?)null);
        var work = new UnitOfWork(context, new DatabaseConnectionSettings());
        _payGate.CanCreateHabits(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new DateOnly(2026, 3, 20));
        dates.GetUserWeekStartDayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(1);

        var config = Substitute.For<IAppConfigService>();
        config.GetAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<int>(1));
        _provider = new ServiceCollection()
            .AddLogging()
            .AddMemoryCache()
            .AddMediatR(cfg =>
            {
                cfg.RegisterServicesFromAssemblyContaining<BulkCreateHabitsCommand>();
                cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
            })
            .AddSingleton<IValidator<BulkCreateHabitsCommand>, BulkCreateHabitsCommandValidator>()
            .AddSingleton<IValidator<BulkSkipHabitsCommand>, BulkSkipHabitsCommandValidator>()
            .AddSingleton<IValidator<BulkUpdateHabitsCommand>, BulkUpdateHabitsCommandValidator>()
            .AddSingleton<IPipelineBehavior<BulkCreateHabitsCommand, Result<BulkCreateResult>>>(_afterWriteFailure)
            .AddSingleton<IPipelineBehavior<BulkUpdateHabitsCommand, Result<BulkHabitMutationResult>>>(_rescheduleFailure)
            .AddSingleton<IGenericRepository<Habit>>(new GenericRepository<Habit>(context))
            .AddSingleton<IGenericRepository<HabitLog>>(new GenericRepository<HabitLog>(context))
            .AddSingleton<IGenericRepository<SentReminder>>(new GenericRepository<SentReminder>(context))
            .AddSingleton<IGenericRepository<Goal>>(new GenericRepository<Goal>(context))
            .AddSingleton<IGenericRepository<Tag>>(new GenericRepository<Tag>(context))
            .AddSingleton(Substitute.For<Orbit.Application.Goals.Services.IGoalCompletionService>())
            .AddSingleton(Substitute.For<IGamificationService>())
            .AddSingleton<CreateHabitRepositories>()
            .AddSingleton(new BulkCreateHabitsRepositories(
                new GenericRepository<Habit>(context),
                new GenericRepository<GoogleCalendarSyncSuggestion>(context),
                new GenericRepository<Tag>(context)))
            .AddSingleton(_payGate)
            .AddSingleton(dates)
            .AddSingleton(config)
            .AddSingleton<IUnitOfWork>(work)
            .BuildServiceProvider();

        var tool = new BulkCreateHabitsTool(_provider.GetRequiredService<IMediator>());
        var skip = new BulkSkipHabitsTool(_provider.GetRequiredService<IMediator>(),
            _provider.GetRequiredService<IGenericRepository<Habit>>(), dates,
            new BulkHabitReplayPlanner(Substitute.For<IIdempotencyContext>(), Substitute.For<IIdempotencyStore>(), work));
        var reschedule = new BulkRescheduleHabitsTool(_provider.GetRequiredService<IMediator>());
        _registry = new AiToolRegistry([tool, skip, reschedule]);
        var catalog = new AgentCatalogService(_registry.GetAll());
        var policy = new AgentPolicyEvaluator(context, catalog, _pending, bridge, settings, new FeatureFlagService(context, new MemoryCache(new MemoryCacheOptions()), Substitute.For<IHostEnvironment>(), new ConfigurationBuilder().Build()));
        _audit.RecordAsync(Arg.Any<AgentAuditEntry>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _lastAudit = call.Arg<AgentAuditEntry>();
                return Task.CompletedTask;
            });
        var ownership = Substitute.For<IAgentTargetOwnershipService>();
        ownership.GetDenialReasonAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _previewer = new PendingOperationChangePreviewer(new GenericRepository<Habit>(context),
            new GenericRepository<Goal>(context), new GenericRepository<Tag>(context), dates, _registry);
        _executor = new AgentOperationExecutor(
            catalog, policy, _audit, ownership, bridge,
            _registry,
            _previewer,
            _pending,
            work, _logger);
    }

    [Fact]
    public async Task ConfirmedDailyItemsWithoutQuantity_CreateEveryItemWithQuantityOne()
    {
        var arguments = JsonSerializer.SerializeToElement(new
        {
            habits = Enumerable.Range(1, 12).Select(index => new
            {
                title = $"Daily habit {index}",
                frequency_unit = "day"
            })
        });
        var request = await ConfirmAsync(arguments);

        var response = await _executor.ExecuteAsync(request);

        response.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, "audit error: {0}", _lastAudit?.Error);
        using var verify = _factory.CreateContext();
        var habits = verify.Habits.ToList();
        habits.Should().HaveCount(12);
        habits.Should().AllSatisfy(habit =>
        {
            habit.FrequencyUnit.Should().Be(FrequencyUnit.Day);
            habit.FrequencyQuantity.Should().Be(1);
        });
        verify.PendingAgentOperations.Single().ConsumedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task ValidationFailure_LeavesPendingOperationApprovable()
    {
        var request = await ConfirmAsync(JsonSerializer.SerializeToElement(new
        {
            habits = new[] { new { title = "Read", frequency_unit = "day", frequency_quantity = 0 } }
        }));

        var response = await _executor.ExecuteAsync(request);

        response.Operation.Status.Should().Be(AgentOperationStatus.Failed);
        _lastAudit!.Error.Should().Contain("FrequencyQuantity");
        AssertApprovable();
        var pendingId = _factory.Context.PendingAgentOperations.Single().Id;
        _pending.Confirm(_userId, pendingId).Should().NotBeNull();
    }

    [Fact]
    public async Task FailureBeforeWrite_LogsSafeExceptionAndAllowsSuccessfulRetry()
    {
        var request = await ConfirmAsync(JsonSerializer.SerializeToElement(new
        {
            habits = new[] { new { title = "Private habit text", frequency_unit = "day" } }
        }));
        _payGate.CanCreateHabits(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Result>(new InvalidOperationException("Private habit text")));

        var response = await _executor.ExecuteAsync(request);

        response.Operation.Status.Should().Be(AgentOperationStatus.Failed);
        _lastAudit!.Error.Should().Be("Private habit text");
        var entry = _logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Error);
        entry.Message.Should().Contain(request.OperationId).And.Contain(request.CorrelationId);
        entry.Exception.Should().NotBeNull();
        entry.Exception!.ToString().Should().Contain(nameof(InvalidOperationException)).And.NotContain("Private habit text");
        entry.Message.Should().NotContain("Private habit text").And.NotContain(_userId.ToString());
        AssertApprovable();

        _payGate.CanCreateHabits(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var retry = await _executor.ExecuteAsync(request);

        retry.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        using var verify = _factory.CreateContext();
        verify.Habits.Should().ContainSingle().Which.FrequencyQuantity.Should().Be(1);
        verify.PendingAgentOperations.Single().ConsumedAtUtc.Should().NotBeNull();
        var replay = await _executor.ExecuteAsync(request);
        replay.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation);
        verify.Habits.Should().ContainSingle();
    }

    [Fact]
    public async Task PayGateFailure_LeavesPendingOperationApprovable()
    {
        var request = await ConfirmAsync(JsonSerializer.SerializeToElement(new
        {
            habits = new[] { new { title = "Read", frequency_unit = "day" } }
        }));
        _payGate.CanCreateHabits(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure("Habit limit reached"));

        var response = await _executor.ExecuteAsync(request);

        response.Operation.Status.Should().Be(AgentOperationStatus.Denied);
        AssertApprovable();
    }

    [Fact]
    public async Task FailureAfterWrite_RollsBackHabitsAndConfirmationBeforeRetry()
    {
        var request = await ConfirmAsync(JsonSerializer.SerializeToElement(new
        {
            habits = new[] { new { title = "Read", frequency_unit = "day" } }
        }));
        _afterWriteFailure.Enabled = true;

        var response = await _executor.ExecuteAsync(request);

        response.Operation.Status.Should().Be(AgentOperationStatus.Failed);
        AssertApprovable();
        _afterWriteFailure.Enabled = false;
        var retry = await _executor.ExecuteAsync(request);
        retry.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        using var verify = _factory.CreateContext();
        verify.Habits.Should().ContainSingle();
        verify.PendingAgentOperations.Single().ConsumedAtUtc.Should().NotBeNull();
    }

    [Theory]
    [InlineData("Exame de colesterol", "Outros exames de sangue")]
    [InlineData("Cholesterol exam", "Other blood tests")]
    public async Task PartialFailure_LogsReasonAndOffersOnlyFailedItemForRetry(string first, string second)
    {
        var request = await ConfirmAsync(JsonSerializer.SerializeToElement(new
        {
            habits = new object[]
            {
                new { title = first, due_date = "2026-10-26", due_time = "15:50" },
                new { title = second, frequency_unit = "Day", due_date = "2026-10-30", end_date = "2026-10-29" }
            }
        }));

        var response = await _executor.ExecuteAsync(request);

        var results = response.Operation.Payload.Should().BeOfType<BulkCreateResult>().Subject;
        results.AppliedCount.Should().Be(1);
        results.Results[1].Title.Should().Be(second);
        results.Results[1].Error.Should().NotBeNullOrWhiteSpace();
        _logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Warning
            && entry.Message.Contains("bulk_create_habits") && entry.Message.Contains("END_DATE_BEFORE_START"));
        _logger.Entries.Should().NotContain(entry => entry.Message.Contains(first) || entry.Message.Contains(second));
        response.PendingOperation.Should().NotBeNull();
        response.PendingOperation!.Items.Should().ContainSingle().Which.EntityName.Should().Be(second);
        response.PendingOperation.Items![0].ValidationErrors.Should().Contain(error => error.ErrorCode == "END_DATE_BEFORE_START");
        var retry = _pending.GetExecution(_userId, response.PendingOperation.Id);
        retry.Should().NotBeNull();
        retry!.Arguments.GetProperty("habits").GetArrayLength().Should().Be(1);
        using var verify = _factory.CreateContext();
        verify.Habits.Should().ContainSingle().Which.Title.Should().Be(first);

        var revisions = new PendingOperationRevisionService(_pending, _previewer,
            new RevisePendingOperationRequestValidator(), _registry);
        var refreshed = await revisions.RefreshAsync(_userId, retry.PendingOperationId, CancellationToken.None);
        var revision = await revisions.ReviseAsync(_userId, retry.PendingOperationId,
            new RevisePendingOperationRequest(refreshed.Preview!.PreviewFingerprint!,
                [new RevisedPendingOperationItem(refreshed.Preview.Items![0].ItemId,
                    JsonSerializer.SerializeToElement(new { end_date = "2026-10-31" }))]), CancellationToken.None);
        revision.IsSuccess.Should().BeTrue();
        var confirmation = _pending.Confirm(_userId, retry.PendingOperationId)!;
        var retried = await _executor.ExecuteAsync(request with
        {
            Arguments = _pending.GetExecution(_userId, retry.PendingOperationId)!.Arguments,
            ConfirmationToken = confirmation.ConfirmationToken
        });
        retried.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        retried.PendingOperation.Should().BeNull();
        using var afterRetry = _factory.CreateContext();
        afterRetry.Habits.Should().HaveCount(2);
        afterRetry.Habits.Count(habit => habit.Title == first).Should().Be(1);
    }

    [Theory]
    [InlineData("Exame de colesterol", "Outros exames de sangue", "Não tomar multivitamínico", "Não ir ao Muay Thai")]
    [InlineData("Cholesterol exam", "Other blood tests", "Don't take multivitamins", "Don't go to Muay Thai")]
    public async Task ExamBatch_ApprovalPersistsOneTimeEventsAndBoundedPreparation(string first, string second, string vitamins, string training)
    {
        var arguments = JsonSerializer.SerializeToElement(new
        {
            habits = new object[]
            {
                new { title = first, frequency_unit = "OneTime", due_date = "2026-10-26", due_time = "15:50" },
                new { title = second, frequency_unit = "OneTime", due_date = "2026-10-30", due_time = "13:30" },
                new { title = vitamins, frequency_unit = "Day", due_date = "2026-10-27", end_date = "2026-10-30", is_bad_habit = false },
                new { title = training, frequency_unit = "Day", due_date = "2026-10-29", end_date = "2026-10-30", is_bad_habit = false }
            }
        });
        var request = await ConfirmAsync(arguments);
        var preview = await _previewer.PreviewAsync(_userId, request.OperationId, arguments);
        preview.Value!.Items.Should().HaveCount(4);
        preview.Value.Items![0].Fields.Should().Contain(field => field.Field == "due_date" && field.ValueType == "date");
        var response = await _executor.ExecuteAsync(request);
        response.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        using var verify = _factory.CreateContext();
        var saved = verify.Habits.OrderBy(habit => habit.Position).ToList();
        saved.Should().HaveCount(4);
        saved.Take(2).Should().AllSatisfy(habit => habit.FrequencyUnit.Should().BeNull());
        saved[0].NextDueDate.Should().Be(new DateOnly(2026, 10, 26));
        saved[0].DueTime.Should().Be(new TimeOnly(15, 50));
        saved[1].NextDueDate.Should().Be(new DateOnly(2026, 10, 30));
        saved[1].DueTime.Should().Be(new TimeOnly(13, 30));
        saved.Skip(2).Should().AllSatisfy(habit =>
        {
            habit.EndDate.Should().Be(new DateOnly(2026, 10, 30));
            habit.IsBadHabit.Should().BeFalse();
        });
    }

    [Fact]
    public async Task OneTimeChild_DoesNotInheritRecurringExamPreparationSchedule()
    {
        var arguments = JsonSerializer.SerializeToElement(new
        {
            habits = new[]
            {
                new
                {
                    title = "Exam preparation", frequency_unit = "Day", due_date = "2026-10-27", end_date = "2026-10-30",
                    sub_habits = new[] { new { title = "Blood test", frequency_unit = "OneTime", due_date = "2026-10-30", due_time = "13:30" } }
                }
            }
        });
        var request = await ConfirmAsync(arguments);
        (await _executor.ExecuteAsync(request)).Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        using var verify = _factory.CreateContext();
        var child = verify.Habits.Single(habit => habit.ParentHabitId != null);
        child.FrequencyUnit.Should().BeNull();
        child.FrequencyQuantity.Should().BeNull();
        child.EndDate.Should().BeNull();
        child.NextDueDate.Should().Be(new DateOnly(2026, 10, 30));
        child.DueTime.Should().Be(new TimeOnly(13, 30));
    }

    private void AssertApprovable()
    {
        using var verify = _factory.CreateContext();
        verify.Habits.Should().BeEmpty();
        var pending = verify.PendingAgentOperations.Single();
        pending.ConsumedAtUtc.Should().BeNull();
        _pending.GetExecution(_userId, pending.Id).Should().NotBeNull();
    }

    private async Task<AgentExecuteOperationRequest> ConfirmAsync(JsonElement arguments)
    {
        var request = new AgentExecuteOperationRequest(
            _userId, "bulk_create_habits", arguments, AgentExecutionSurface.Chat, AgentAuthMethod.Jwt,
            CorrelationId: "bulk-create-test");
        var preview = await _executor.ExecuteAsync(request);
        preview.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation, "audit error: {0}", _lastAudit?.Error);
        var confirmation = _pending.Confirm(_userId, preview.PendingOperation!.Id);
        confirmation.Should().NotBeNull();
        var execution = _pending.GetExecution(_userId, confirmation!.PendingOperationId);
        execution.Should().NotBeNull();
        return request with { Arguments = execution!.Arguments, ConfirmationToken = confirmation.ConfirmationToken };
    }

    public void Dispose()
    {
        _provider.Dispose();
        _factory.Dispose();
    }

    private sealed class RecordingLogger : ILogger<AgentOperationExecutor>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception), exception));
    }

    private sealed class AfterWriteFailureBehavior : IPipelineBehavior<BulkCreateHabitsCommand, Result<BulkCreateResult>>
    {
        public bool Enabled { get; set; }
        public string? FailOnTitle { get; set; }

        public async Task<Result<BulkCreateResult>> Handle(BulkCreateHabitsCommand request,
            RequestHandlerDelegate<Result<BulkCreateResult>> next, CancellationToken cancellationToken)
        {
            var result = await next(cancellationToken);
            if (Enabled && (FailOnTitle is null || request.Habits.Any(item => item.Title == FailOnTitle)))
                throw new InvalidOperationException("Failure after saving habits");
            return result;
        }
    }
}
