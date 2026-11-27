using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Behaviors;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Chat.Validators;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
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

/// <summary>
/// Drives each bind route and the real execute route over one SQLite database: a preview bound in one client context
/// approves and revises from another client context on the first try, and each stale approval returns 409.
/// </summary>
public sealed partial class FirstTryPreviewTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 3, 20);
    private readonly IEncryptionService _encryption = new EncryptionService(
        Options.Create(new EncryptionSettings { Key = Convert.ToBase64String(new byte[32]) }), NullLogger<EncryptionService>.Instance);
    private readonly SqliteOrbitDbContextFactory _factory;
    private readonly ServiceProvider _provider;
    private readonly PendingAgentOperationStore _store;
    private readonly PendingClarificationStore _clarifications;
    private readonly PendingOperationChangePreviewer _previewer;
    private readonly PendingOperationRevisionService _revisions;
    private readonly RacingExecutor _executor;
    private readonly AiToolRegistry _registry;
    private readonly HabitSuggestionService _suggestions;
    private readonly AccountChangeSetService _changeSets;
    private readonly AiController _controller;
    private readonly IConversationPlanStore _plans = Substitute.For<IConversationPlanStore>();
    private readonly IAgentStepUpAuthorizationBridge _bridge = Substitute.For<IAgentStepUpAuthorizationBridge>();
    private readonly Guid _userId;
    private readonly Guid _conversationId = Guid.NewGuid();

    public FirstTryPreviewTests()
    {
        _factory = new SqliteOrbitDbContextFactory(_encryption);
        var context = _factory.Context;
        var user = User.Create("Alex", "alex@example.com").Value;
        user.GrantLifetimePro();
        _userId = user.Id;
        context.Users.Add(user);
        context.SaveChanges();
        var settings = Options.Create(new AgentPlatformSettings());
        _store = new PendingAgentOperationStore(context, settings);
        _clarifications = new PendingClarificationStore(context);
        var bridge = _bridge;
        bridge.GetRequiredConfirmationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((AgentConfirmationRequirement?)null);
        var work = new UnitOfWork(context, new DatabaseConnectionSettings());
        var payGate = Substitute.For<IPayGateService>();
        payGate.CanCreateHabits(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Today);
        dates.GetUserTimeZoneAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(TimeZoneInfo.Utc);
        dates.GetUserWeekStartDayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(1);
        var config = Substitute.For<IAppConfigService>();
        config.GetAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(call => call.ArgAt<int>(1));
        var habits = new GenericRepository<Habit>(context);
        var goals = new GenericRepository<Goal>(context);
        var tags = new GenericRepository<Tag>(context);
        _provider = new ServiceCollection()
            .AddLogging()
            .AddMemoryCache()
            .AddMediatR(cfg =>
            {
                cfg.RegisterServicesFromAssemblyContaining<BulkCreateHabitsCommand>();
                cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
            })
            .AddSingleton(Substitute.For<IUserStreakService>())
            .AddSingleton<IGenericRepository<Habit>>(habits)
            .AddSingleton<IGenericRepository<HabitLog>>(new GenericRepository<HabitLog>(context))
            .AddSingleton<IGenericRepository<SentReminder>>(new GenericRepository<SentReminder>(context))
            .AddSingleton<IGenericRepository<Notification>>(new GenericRepository<Notification>(context))
            .AddSingleton<IGenericRepository<Goal>>(goals)
            .AddSingleton<IGenericRepository<Tag>>(tags)
            .AddSingleton<IGenericRepository<User>>(new GenericRepository<User>(context))
            .AddSingleton(Substitute.For<Orbit.Application.Goals.Services.IGoalCompletionService>())
            .AddSingleton(Substitute.For<IGamificationService>())
            .AddSingleton<CreateHabitRepositories>()
            .AddSingleton(new BulkCreateHabitsRepositories(habits, new GenericRepository<GoogleCalendarSyncSuggestion>(context), tags))
            .AddSingleton(payGate)
            .AddSingleton(dates)
            .AddSingleton(config)
            .AddSingleton<IUnitOfWork>(work)
            .BuildServiceProvider();
        var mediator = _provider.GetRequiredService<IMediator>();
        _registry = new AiToolRegistry([
            new CreateHabitTool(dates, mediator), new BulkCreateHabitsTool(mediator), new UpdateHabitTool(habits, dates, mediator),
            new BulkUpdateHabitsTool(mediator), new BulkDeleteHabitsTool(mediator, habits),
            new BulkSkipHabitsTool(mediator, habits, dates,
                new BulkHabitReplayPlanner(Substitute.For<IIdempotencyContext>(), Substitute.For<IIdempotencyStore>(), work))]);
        var catalog = new AgentCatalogService(_registry.GetAll());
        var cache = new MemoryCache(new MemoryCacheOptions());
        var policy = new AgentPolicyEvaluator(context, catalog, _store, bridge,
            new FeatureFlagService(context, cache, Substitute.For<IHostEnvironment>(), new ConfigurationBuilder().Build()));
        var ownership = Substitute.For<IAgentTargetOwnershipService>();
        ownership.GetDenialReasonAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _previewer = new PendingOperationChangePreviewer(habits, goals, tags, dates, _registry);
        _executor = new RacingExecutor(new AgentOperationExecutor(catalog, policy, Substitute.For<IAgentAuditService>(),
            ownership, bridge, _registry, _previewer, _store, work, NullLogger<AgentOperationExecutor>.Instance, plans: _plans));
        _revisions = new PendingOperationRevisionService(_store, _previewer, new RevisePendingOperationRequestValidator(), _registry);
        _suggestions = new HabitSuggestionService(habits, goals, dates, Substitute.For<IHabitDraftGenerator>());
        _changeSets = new AccountChangeSetService(_store, _previewer, catalog, _registry);
        var validator = Substitute.For<IValidator<ResolveClarificationRequest>>();
        validator.ValidateAsync(Arg.Any<ResolveClarificationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new FluentValidation.Results.ValidationResult());
        _controller = new AiController(catalog, policy, new AgentPendingStores(_store, _clarifications),
            Substitute.For<IAgentStepUpService>(), Substitute.For<IAgentAuditService>(), _executor, _revisions, validator)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, _userId.ToString())], "Test"))
                }
            }
        };
    }

    private AgentClientContext OfferClient(string locale = "en") => new(Locale: locale, SupportsParts: true,
        SupportsPendingOperationChanges: true, SupportsPendingOperationPlacement: true, SupportsProactiveOffers: true,
        ConversationId: _conversationId);

    private static readonly AgentClientContext PlainClient = new(Locale: "en", SupportsParts: true,
        SupportsPendingOperationChanges: true);

    private async Task<IActionResult> ApproveAsync(Guid pendingId, AgentClientContext client)
    {
        var confirmation = _store.Confirm(_userId, pendingId);
        confirmation.Should().NotBeNull();
        return await _controller.ExecutePendingOperation(pendingId,
            new AiController.ExecutePendingOperationRequest(confirmation!.ConfirmationToken, client), default);
    }

    private static AgentExecuteOperationResponse Succeeded(IActionResult result)
    {
        var response = result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<AgentExecuteOperationResponse>().Subject;
        response.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, JsonSerializer.Serialize(response));
        response.PendingOperation.Should().BeNull();
        return response;
    }

    private static void StalePreview(IActionResult result) =>
        JsonSerializer.Serialize(result.Should().BeOfType<ConflictObjectResult>().Which.Value)
            .Should().Be(JsonSerializer.Serialize(new { error = "stale_preview" }));

    private int PendingRows()
    {
        _factory.Context.ChangeTracker.Clear();
        return _factory.Context.PendingAgentOperations.Count();
    }

    private Habit AddHabit(string title, string? emoji = null)
    {
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, _userId, title, FrequencyUnit.Day, 1, Today,
            Emoji: emoji)).Value;
        _factory.Context.Habits.Add(habit);
        _factory.Context.SaveChanges();
        _factory.Context.ChangeTracker.Clear();
        return habit;
    }

    private HabitSuggestionDraft Draft(string title, Guid? existing = null, TimeOnly? time = null)
    {
        var start = time ?? new TimeOnly(20, 0);
        return new(Guid.NewGuid(), "📖", title, "Habit", FrequencyUnit.Day, 1, [], Today, start, start.AddMinutes(30), null, [],
            "Fits the day.", existing);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _factory.Dispose();
    }

    /// <summary>Runs a hook after the controller checks and before the executor, to force a race.</summary>
    private sealed class RacingExecutor(IAgentOperationExecutor inner) : IAgentOperationExecutor
    {
        public Func<Task>? BeforeNextExecute { get; set; }

        public async Task<AgentExecuteOperationResponse> ExecuteAsync(AgentExecuteOperationRequest request,
            CancellationToken cancellationToken = default)
        {
            if (BeforeNextExecute is { } hook)
            {
                BeforeNextExecute = null;
                await hook();
            }
            return await inner.ExecuteAsync(request, cancellationToken);
        }
    }
}
