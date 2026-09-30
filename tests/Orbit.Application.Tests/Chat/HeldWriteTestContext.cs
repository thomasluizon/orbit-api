using System.Linq.Expressions;
using System.Text.Json;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.ApiKeys.Commands;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Services;
using Orbit.Application.Calendar.Commands;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Chat.Validators;
using Orbit.Application.ChecklistTemplates.Commands;
using Orbit.Application.Common;
using Orbit.Application.Goals.Commands;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Validators;
using Orbit.Application.Notifications.Commands;
using Orbit.Application.Profile.Commands;
using Orbit.Application.Subscriptions.Commands;
using Orbit.Application.Subscriptions;
using Orbit.Application.Tags.Commands;
using Orbit.Application.Support.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat;

internal sealed class HeldWriteTestContext : IDisposable
{
    private readonly ServiceProvider _services;
    public Guid UserId { get; } = Guid.NewGuid();
    public DateOnly Today { get; } = new(2026, 9, 30);
    public IMediator Mediator { get; } = Substitute.For<IMediator>();
    public IUnitOfWork UnitOfWork { get; } = Substitute.For<IUnitOfWork>();
    public IUserDateService Dates { get; } = Substitute.For<IUserDateService>();
    public List<Entity> Records { get; } = [];
    public List<Entity> Added { get; } = [];
    public List<object> Commands { get; } = [];
    public Habit Habit { get; }
    public Goal Goal { get; }
    public Tag Tag { get; }
    public Habit ForeignHabit { get; }
    public Goal ForeignGoal { get; }
    public string DeletionCode { get; }
    public EmailChallengeService Challenges => _services.GetRequiredService<EmailChallengeService>();

    public HeldWriteTestContext()
    {
        var user = User.Create("Reader", "reader@example.com").Value;
        UserId = user.Id;
        Habit = Habit.Create(new HabitCreateParams(UserId, "Read", FrequencyUnit.Day, 1,
            Today.AddDays(-4))).Value;
        Goal = Goal.Create(UserId, "Books", 20m, "books").Value;
        Tag = Tag.Create(UserId, "Study", "#7c3aed").Value;
        var foreignUser = Guid.NewGuid();
        ForeignHabit = Habit.Create(new HabitCreateParams(foreignUser, "Foreign", FrequencyUnit.Day, 1, Today)).Value;
        ForeignGoal = Goal.Create(foreignUser, "Foreign", 20m, "books").Value;
        Records.AddRange([Habit, Goal, Tag, user, ForeignHabit, ForeignGoal]);
        Dates.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
        Dates.GetUserWeekStartDayAsync(UserId, Arg.Any<CancellationToken>()).Returns(0);
        var config = Substitute.For<IAppConfigService>();
        config.GetAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<int>(1));
        UnitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<int>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task<int>>>(0)(call.ArgAt<CancellationToken>(1)));
        UnitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<ToolResult>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task<ToolResult>>>(0)(call.ArgAt<CancellationToken>(1)));
        UnitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result<ToolResult>>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task<Result<ToolResult>>>>(0)(call.ArgAt<CancellationToken>(1)));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton(Mediator);
        services.AddSingleton(UnitOfWork);
        services.AddSingleton(Dates);
        services.AddSingleton(config);
        var payGate = Substitute.For<IPayGateService>();
        payGate.CanCreateHabits(UserId, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        services.AddSingleton(payGate);
        services.AddSingleton(Substitute.For<IGoalCompletionService>());
        services.AddSingleton(Substitute.For<IHabitEmojiInferenceService>());
        services.AddSingleton(new BulkHabitReplayPlanner(Substitute.For<IIdempotencyContext>(),
            Substitute.For<IIdempotencyStore>(), UnitOfWork));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<EmailChallengeService>();
        RegisterRepository<Habit>(services);
        RegisterRepository<HabitLog>(services);
        RegisterRepository<Goal>(services);
        RegisterRepository<GoalProgressLog>(services);
        RegisterRepository<Tag>(services);
        RegisterRepository<User>(services);
        RegisterRepository<ChecklistTemplate>(services);
        RegisterRepository<Notification>(services);
        RegisterRepository<PushSubscription>(services);
        RegisterRepository<ApiKey>(services);
        RegisterRepository<UserFact>(services);
        RegisterRepository<GoogleCalendarSyncSuggestion>(services);
        services.AddValidatorsFromAssemblyContaining<CreateHabitCommandValidator>();
        _services = services.BuildServiceProvider();
        var handler = new CheckChatCommandQueryHandler(_services);
        Mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => handler.Handle(call.ArgAt<CheckChatCommandQuery>(0), call.ArgAt<CancellationToken>(1)));
        DeletionCode = _services.GetRequiredService<EmailChallengeService>()
            .Issue(EmailChallengeOperation.AccountDeletion, user.Email).Value;
        SetupWrite<CreateTagCommand, Guid>(Guid.NewGuid());
        SetupWrite<CreateChecklistTemplateCommand, Guid>(Guid.NewGuid());
        SetupWrite<CreateSubHabitCommand, Guid>(Guid.NewGuid());
        SetupWrite<BulkCreateHabitsCommand, BulkCreateResult>(new BulkCreateResult([new(0, BulkItemStatus.Success)]));
        SetupWrite<BulkUpdateHabitsCommand, BulkHabitMutationResult>(new(1, 1, 0, false));
        SetupWrite<BulkLogHabitsCommand, BulkLogResult>(new([new(0, BulkItemStatus.Success, Habit.Id)]));
        SetupWrite<BulkSkipHabitsCommand, BulkSkipResult>(new([new(0, BulkItemStatus.Success, Habit.Id)]));
        SetupWrite<LogHabitCommand, LogHabitResponse>(new(Guid.NewGuid(), true, 1));
        SetupWrite<CreateCheckoutCommand, CheckoutResponse>(new("https://example.com/checkout"));
        SetupWrite<CreateApiKeyCommand, CreateApiKeyResponse>(new(Guid.NewGuid(), "Key", "secret", "prefix", [], false, null, DateTime.MinValue));
        SetupWrite<ConfirmAccountDeletionCommand, DateTime>(DateTime.MinValue);
        SetupWrite<UpdateTagCommand>();
        SetupWrite<UpdateChecklistCommand>();
        SetupWrite<ReorderHabitsCommand>();
        SetupWrite<ReorderGoalsCommand>();
        SetupWrite<SetTimezoneCommand>();
        SetupWrite<SetLanguageCommand>();
        SetupWrite<SetWeekStartDayCommand>();
        SetupWrite<SetClockFormatCommand>();
        SetupWrite<SetThemePreferenceCommand>();
        SetupWrite<SetAiMemoryCommand>();
        SetupWrite<SetAiSummaryCommand>();
        SetupWrite<SetCalendarAutoSyncCommand>();
        SetupWrite<SubscribePushCommand>();
        SetupWrite<UnsubscribePushCommand>();
        SetupWrite<SendSupportCommand>();
    }

    public IGenericRepository<T> Repository<T>() where T : Entity => _services.GetRequiredService<IGenericRepository<T>>();

    private void RegisterRepository<T>(IServiceCollection services) where T : Entity
    {
        var repository = Substitute.For<IGenericRepository<T>>();
        IReadOnlyList<T> Select(Expression<Func<T, bool>> predicate) => Records.OfType<T>().Where(predicate.Compile()).ToList();
        repository.FindAsync(Arg.Any<Expression<Func<T, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => Select(call.ArgAt<Expression<Func<T, bool>>>(0)));
        repository.FindAsync(Arg.Any<Expression<Func<T, bool>>>(),
                Arg.Any<Func<IQueryable<T>, IQueryable<T>>>(), Arg.Any<CancellationToken>())
            .Returns(call => Select(call.ArgAt<Expression<Func<T, bool>>>(0)));
        repository.FindTrackedAsync(Arg.Any<Expression<Func<T, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => Select(call.ArgAt<Expression<Func<T, bool>>>(0)));
        repository.FindTrackedAsync(Arg.Any<Expression<Func<T, bool>>>(),
                Arg.Any<Func<IQueryable<T>, IQueryable<T>>>(), Arg.Any<CancellationToken>())
            .Returns(call => Select(call.ArgAt<Expression<Func<T, bool>>>(0)));
        repository.FindOneTrackedAsync(Arg.Any<Expression<Func<T, bool>>>(),
                Arg.Any<Func<IQueryable<T>, IQueryable<T>>>(), Arg.Any<CancellationToken>())
            .Returns(call => Select(call.ArgAt<Expression<Func<T, bool>>>(0)).FirstOrDefault());
        repository.AnyAsync(Arg.Any<Expression<Func<T, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => Select(call.ArgAt<Expression<Func<T, bool>>>(0)).Count > 0);
        repository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => Records.OfType<T>().FirstOrDefault(record => record.Id == call.ArgAt<Guid>(0)));
        repository.AddAsync(Arg.Any<T>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            Added.Add(call.ArgAt<T>(0));
            return Task.CompletedTask;
        });
        services.AddSingleton(repository);
    }

    private void SetupWrite<TCommand, TValue>(TValue value) where TCommand : class, IRequest<Result<TValue>> =>
        Mediator.Send(Arg.Any<TCommand>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            Commands.Add(call.ArgAt<TCommand>(0));
            return Result.Success(value);
        });

    private void SetupWrite<TCommand>() where TCommand : class, IRequest<Result> =>
        Mediator.Send(Arg.Any<TCommand>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            Commands.Add(call.ArgAt<TCommand>(0));
            return Result.Success();
        });

    public IAiTool Tool(string name)
    {
        var typeName = string.Concat(name.Split('_').Select(part => char.ToUpperInvariant(part[0]) + part[1..])) + "Tool";
        var type = typeof(IAiTool).Assembly.GetTypes().Single(type => type.Name == typeName);
        return (IAiTool)ActivatorUtilities.CreateInstance(_services, type);
    }

    public string Expand(string json) => json.Replace("$habit", Habit.Id.ToString())
        .Replace("$goal", Goal.Id.ToString()).Replace("$tag", Tag.Id.ToString())
        .Replace("$code", DeletionCode).Replace("$foreign", json.Contains("goal_id", StringComparison.Ordinal)
            ? ForeignGoal.Id.ToString() : ForeignHabit.Id.ToString());

    public async Task<(PendingOperationRevisionResult Result, JsonElement Arguments)> ReviseAsync(
        IAiTool tool, string argumentsJson, string editsJson)
    {
        var original = JsonSerializer.Deserialize<JsonElement>(Expand(argumentsJson));
        var current = original;
        var pendingId = Guid.NewGuid();
        var store = Substitute.For<IPendingAgentOperationStore>();
        store.GetExecution(UserId, pendingId).Returns(_ => new PendingAgentOperationExecution(
            pendingId, tool.Name, tool.Name, current, AgentExecutionSurface.Chat,
            AgentConfirmationRequirement.FreshConfirmation));
        store.Revise(UserId, pendingId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(call =>
            {
                current = JsonSerializer.Deserialize<JsonElement>(call.ArgAt<string>(3));
                return true;
            });
        var registry = new AiToolRegistry([tool]);
        var previewer = new PendingOperationChangePreviewer(Repository<Habit>(), Repository<Goal>(),
            Repository<Tag>(), Dates, registry);
        var preview = await previewer.PreviewAsync(UserId, tool.Name, original);
        var service = new PendingOperationRevisionService(store, previewer,
            new RevisePendingOperationRequestValidator(), registry);
        var edits = JsonSerializer.Deserialize<JsonElement>(Expand(editsJson));
        var result = await service.ReviseAsync(UserId, pendingId,
            new RevisePendingOperationRequest(preview!.PreviewFingerprint!,
                [new(preview.Items![0].ItemId, edits)]), CancellationToken.None);
        return (result, current);
    }

    public void Dispose() => _services.Dispose();
}
