using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Chat;
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

namespace Orbit.Application.Tests.Chat;

public sealed class AstraApprovalPathTests : IDisposable
{
    private readonly OrbitDbContext _db = new(new DbContextOptionsBuilder<OrbitDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly Guid _userId;
    private readonly IUserDateService _dates = Substitute.For<IUserDateService>();
    private readonly DateOnly _today = new(2026, 10, 19);

    public AstraApprovalPathTests()
    {
        var user = User.Create("Alex", "alex@example.com").Value;
        user.GrantLifetimePro();
        _userId = user.Id;
        _db.Users.Add(user);
        _dates.GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(_today);
        _db.SaveChanges();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RoundThree_GoalLink_RealApprovalChecksDependency(bool changeLinked)
    {
        var linked = AddHabit("Ler 10 minutos");
        var unrelated = AddHabit("Beber água");
        var unit = new UnitOfWork(_db, new DatabaseConnectionSettings());
        var tool = new CreateGoalTool(Orbit.Tests.Shared.GoalToolCommandMediator.Create(new GenericRepository<Goal>(_db), unit, habits: new GenericRepository<Habit>(_db), configureTransactions: false));
        var (executor, store, revisions) = Build(tool);
        var args = JsonSerializer.SerializeToElement(new
        {
            title = "Ler 12 livros",
            target_value = 12,
            unit = "livros",
            deadline = "2026-12-31",
            habit_ids = new[] { linked.Id }
        });
        var held = await executor.ExecuteAsync(new(_userId, tool.Name, args, AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt, IncludeChangePreview: true));
        held.PendingOperation.Should().NotBeNull(JsonSerializer.Serialize(held));
        held.PendingOperation!.Items!.Single().Fields.Should().Contain(field =>
            field.Field == "habit_ids" && field.NewValue!.Contains("Ler 10 minutos"));
        _db.Goals.Should().BeEmpty();
        (changeLinked ? linked : unrelated).PostponeTo(_today.AddDays(1));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        var pending = store.GetExecution(_userId, held.PendingOperation!.Id)!;
        (await revisions.IsCurrentAsync(_userId, pending, default)).Should().Be(!changeLinked);
        if (changeLinked)
            return;
        var confirmation = store.Confirm(_userId, held.PendingOperation!.Id)!;
        var approved = await executor.ExecuteAsync(new(_userId, tool.Name, pending.Arguments,
            AgentExecutionSurface.Chat, AgentAuthMethod.Jwt, ConfirmationToken: confirmation.ConfirmationToken));
        approved.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        _db.ChangeTracker.Clear();
        (await _db.Goals.Include(goal => goal.Habits).SingleAsync()).Habits.Should().ContainSingle().Which.Id.Should().Be(linked.Id);
    }

    [Fact]
    public async Task RoundThree_Breakdown_RealApprovalCreatesChildrenUnderVisibleParent()
    {
        var parent = AddHabit("Academia");
        var unrelated = AddHabit("Beber água");
        var habits = new GenericRepository<Habit>(_db);
        var tags = new GenericRepository<Tag>(_db);
        var unit = new UnitOfWork(_db, new DatabaseConnectionSettings());
        var payGate = Substitute.For<IPayGateService>();
        var config = Substitute.For<IAppConfigService>();
        config.GetAsync(AppConfigKeys.MaxHabitDepth, AppConstants.MaxHabitDepth, Arg.Any<CancellationToken>()).Returns(AppConstants.MaxHabitDepth);
        var handler = new CreateSubHabitCommandHandler(habits, tags, _dates, unit, config, payGate, _cache);
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<CreateSubHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => handler.Handle(call.Arg<CreateSubHabitCommand>(), call.Arg<CancellationToken>()));
        var tool = new BulkCreateHabitsTool(mediator);
        var (executor, store, revisions) = Build(tool);
        var args = JsonSerializer.SerializeToElement(new
        {
            parent_habit_id = parent.Id,
            habits = new[] { new { title = "Aquecimento" }, new { title = "Treino" }, new { title = "Alongamento" } }
        });
        var held = await executor.ExecuteAsync(new(_userId, tool.Name, args, AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt, IncludeChangePreview: true));
        held.PendingOperation.Should().NotBeNull(JsonSerializer.Serialize(held));
        var preview = held.PendingOperation!;
        preview.Items.Should().HaveCount(3).And.OnlyContain(item => item.Fields.Any(field =>
            field.Field == "parent_habit_id" && field.NewValue == "Academia"));
        _db.Habits.Should().HaveCount(2);
        unrelated.PostponeTo(_today.AddDays(1));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        var pending = store.GetExecution(_userId, preview.Id)!;
        (await revisions.IsCurrentAsync(_userId, pending, default)).Should().BeTrue();
        (await revisions.CanApproveAsync(_userId, pending, default)).Should().BeTrue();
        var confirmation = store.Confirm(_userId, preview.Id)!;
        var approved = await executor.ExecuteAsync(new(_userId, tool.Name, pending.Arguments,
            AgentExecutionSurface.Chat, AgentAuthMethod.Jwt, ConfirmationToken: confirmation.ConfirmationToken));
        approved.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        _db.ChangeTracker.Clear();
        (await _db.Habits.Where(habit => habit.ParentHabitId == parent.Id).Select(habit => habit.Title).ToListAsync())
            .Should().BeEquivalentTo("Aquecimento", "Treino", "Alongamento");
    }

    [Theory]
    [InlineData("create_goal")]
    [InlineData("assign_tags")]
    public async Task RoundThree_DeletedDependency_NeverOffersUnboundApproval(string operationId)
    {
        var habit = AddHabit("Academia");
        habit.SoftDelete();
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        IAiTool tool = operationId == "create_goal"
            ? new CreateGoalTool(Orbit.Tests.Shared.GoalToolCommandMediator.Create(new GenericRepository<Goal>(_db), new UnitOfWork(_db, new DatabaseConnectionSettings()), habits: new GenericRepository<Habit>(_db), configureTransactions: false))
            : new AssignTagsTool(new GenericRepository<Habit>(_db), new GenericRepository<Tag>(_db), new UnitOfWork(_db, new DatabaseConnectionSettings()));
        var (executor, store, _) = Build(tool);
        var args = operationId == "create_goal"
            ? JsonSerializer.SerializeToElement(new { title = "Goal", habit_ids = new[] { habit.Id } })
            : JsonSerializer.SerializeToElement(new { habit_ids = new[] { habit.Id }, tag_names = new[] { "Saúde" } });

        var result = await executor.ExecuteAsync(new(_userId, operationId, args, AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt, IncludeChangePreview: true));

        result.PendingOperation.Should().BeNull();
        store.GetPendingHabitCreations(_userId).Should().BeEmpty();
        _db.PendingAgentOperations.Should().OnlyContain(item => item.ConsumedAtUtc != null);
    }

    [Fact]
    public async Task RoundThree_DirectApproval_RejectsStaleDependency()
    {
        var linked = AddHabit("Ler 10 minutos");
        var tool = new CreateGoalTool(Orbit.Tests.Shared.GoalToolCommandMediator.Create(new GenericRepository<Goal>(_db), new UnitOfWork(_db, new DatabaseConnectionSettings()), habits: new GenericRepository<Habit>(_db), configureTransactions: false));
        var (executor, store, _) = Build(tool);
        var args = JsonSerializer.SerializeToElement(new { title = "Ler livros", habit_ids = new[] { linked.Id } });
        var held = await executor.ExecuteAsync(new(_userId, tool.Name, args, AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt, IncludeChangePreview: true));
        linked.PostponeTo(_today.AddDays(1));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        var confirmation = store.Confirm(_userId, held.PendingOperation!.Id)!;

        var approved = await executor.ExecuteAsync(new(_userId, tool.Name, args, AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt, ConfirmationToken: confirmation.ConfirmationToken));

        approved.Operation.Status.Should().NotBe(AgentOperationStatus.Succeeded);
        _db.Goals.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingFingerprint_RefusesWritesAndPreservesReadOnlyConfirmation(bool readOnly)
    {
        var tool = Substitute.For<IAiTool>();
        tool.Name.Returns("test_operation");
        tool.IsReadOnly.Returns(readOnly);
        var registry = new AiToolRegistry([tool]);
        var revisions = new PendingOperationRevisionService(Substitute.For<IPendingAgentOperationStore>(),
            Substitute.For<IPendingOperationChangePreviewer>(), new RevisePendingOperationRequestValidator(), registry);
        var execution = new PendingAgentOperationExecution(Guid.NewGuid(), "test", tool.Name,
            JsonSerializer.SerializeToElement(new { }), AgentExecutionSurface.Chat, AgentConfirmationRequirement.FreshConfirmation);

        (await revisions.IsCurrentAsync(_userId, execution, default)).Should().Be(readOnly);
    }

    private Habit AddHabit(string title)
    {
        var habit = Habit.Create(new(_userId, title, FrequencyUnit.Day, 1, _today)).Value;
        _db.Habits.Add(habit);
        _db.SaveChanges();
        return habit;
    }

    private (AgentOperationExecutor, PendingAgentOperationStore, PendingOperationRevisionService) Build(IAiTool tool)
    {
        var registry = new AiToolRegistry([tool]);
        var catalog = new AgentCatalogService([tool]);
        foreach (var key in catalog.GetCapabilities().SelectMany(capability => capability.FeatureFlagKeys ?? []).Distinct())
            _db.AppFeatureFlags.Add(AppFeatureFlag.Create(key, true, null, key));
        _db.SaveChanges();
        var settings = Options.Create(new AgentPlatformSettings());
        var store = new PendingAgentOperationStore(_db, settings);
        var bridge = Substitute.For<IAgentStepUpAuthorizationBridge>();
        bridge.GetRequiredConfirmationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((AgentConfirmationRequirement?)null);
        var ownership = Substitute.For<IAgentTargetOwnershipService>();
        ownership.GetDenialReasonAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        var policy = new AgentPolicyEvaluator(_db, catalog, store, bridge, settings, new FeatureFlagService(_db, _cache));
        var previewer = new PendingOperationChangePreviewer(new GenericRepository<Habit>(_db),
            new GenericRepository<Goal>(_db), new GenericRepository<Tag>(_db), _dates, registry);
        var unit = Substitute.For<IUnitOfWork>();
        unit.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<AgentExecuteOperationResponse>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task<AgentExecuteOperationResponse>>>()(call.Arg<CancellationToken>()));
        var executor = new AgentOperationExecutor(catalog, policy, Substitute.For<IAgentAuditService>(),
            ownership, bridge, registry, previewer, store, unit,
            NullLogger<AgentOperationExecutor>.Instance);
        return (executor, store, new PendingOperationRevisionService(store, previewer,
            new RevisePendingOperationRequestValidator(), registry));
    }

    public void Dispose()
    {
        _cache.Dispose();
        _db.Dispose();
    }
}
