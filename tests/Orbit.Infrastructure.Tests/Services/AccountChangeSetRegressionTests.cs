using System.Linq.Expressions;
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
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Support.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class AccountChangeSetRegressionTests
{
    [Fact]
    public async Task BulkCreation_DeniedSlipAlertEntitlement_DoesNotTrackHabits()
    {
        var userId = Guid.NewGuid();
        var habits = Substitute.For<IGenericRepository<Habit>>();
        var gate = Substitute.For<IPayGateService>();
        var dates = Substitute.For<IUserDateService>();
        var work = Substitute.For<IUnitOfWork>();
        gate.CanCreateHabits(userId, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        gate.CanUseSlipAlerts(userId, Arg.Any<CancellationToken>()).Returns(Result.Failure("Pro required", "PAY_GATE"));
        dates.GetUserTodayAsync(userId, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 4, 3));
        var mediator = Orbit.Tests.Shared.HabitToolCommandMediator.Create(habits,
            Substitute.For<IGenericRepository<Tag>>(), Substitute.For<IGenericRepository<Goal>>(), dates, gate, work);
        var tool = new BulkCreateHabitsTool(mediator);
        var args = JsonSerializer.SerializeToElement(new
        {
            habits = new[] {
            new { title = "Smoking", frequency_unit = "Day", is_bad_habit = true, slip_alert_enabled = true }
        }
        });

        (await tool.CheckArgumentsAsync(args, userId, default)).IsSuccess.Should().BeTrue();
        var result = await tool.ExecuteAsync(args, userId, default);
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("PAY_GATE");
        await habits.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
        await gate.Received().CanUseSlipAlerts(userId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReusedHabit_PreviewsReplacedAssociationsBeforeUpdating()
    {
        var userId = Guid.NewGuid();
        var habit = Habit.Create(new HabitCreateParams(userId, "Run", FrequencyUnit.Day, 1, new DateOnly(2026, 4, 3))).Value;
        var oldTag = Tag.Create(userId, "Old", "#7c3aed").Value;
        var newTag = Tag.Create(userId, "New", "#7c3aed").Value;
        habit.AddTag(oldTag);
        var habits = Substitute.For<IGenericRepository<Habit>>();
        habits.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>()).Returns(new[] { habit });
        habits.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(), Arg.Any<CancellationToken>()).Returns(new[] { habit });
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(userId, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 4, 3));
        var mediator = Substitute.For<IMediator>();
        var tags = Substitute.For<IGenericRepository<Tag>>();
        tags.FindTrackedAsync(Arg.Any<Expression<Func<Tag, bool>>>(), Arg.Any<CancellationToken>()).Returns(new[] { newTag });
        tags.FindAsync(Arg.Any<Expression<Func<Tag, bool>>>(), Arg.Any<CancellationToken>()).Returns(new[] { newTag });
        habits.FindOneTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>()).Returns(habit);
        var work = Substitute.For<IUnitOfWork>();
        work.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result<string>>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task<Result<string>>>>(0)(call.ArgAt<CancellationToken>(1)));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var config = Substitute.For<IAppConfigService>();
        config.GetAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(call => call.ArgAt<int>(1));
        var handler = new UpdateHabitCommandHandler(habits, Substitute.For<IGenericRepository<SentReminder>>(),
            Substitute.For<IGenericRepository<Goal>>(), Substitute.For<IPayGateService>(), Substitute.For<IGoalCompletionService>(),
            dates, work, cache, tags, config);
        UpdateHabitFieldsCommand? sent = null;
        mediator.Send(Arg.Do<UpdateHabitFieldsCommand>(command => sent = command), Arg.Any<CancellationToken>())
            .Returns(call => handler.Handle(call.Arg<UpdateHabitFieldsCommand>(), call.Arg<CancellationToken>()));
        var tool = new BulkCreateHabitsTool(mediator);
        var previewer = new PendingOperationChangePreviewer(habits, Substitute.For<IGenericRepository<Goal>>(),
            tags, dates, new AiToolRegistry(new[] { tool }));
        var args = JsonSerializer.SerializeToElement(new
        {
            habits = new[] {
            new { title = "Run", habit_id = habit.Id, frequency_unit = "Day", tag_ids = new[] { newTag.Id } }
        }
        });

        var preview = await previewer.PreviewAsync(userId, tool.Name, args);
        preview.Value!.Items!.Single().Fields.Single(field => field.Field == "tag_ids").OldValue.Should().Contain("Old");
        preview.Value.Items!.Single().Fields.Single(field => field.Field == "tag_ids").NewValue.Should().Contain("New");
        (await tool.ExecuteAsync(args, userId, default)).Success.Should().BeTrue();
        sent!.TagIds.Should().Equal(newTag.Id);
        habit.Tags.Should().NotContain(oldTag);
    }

    [Theory]
    [InlineData("support")]
    [InlineData("habit")]
    [InlineData("bulk_habit")]
    public async Task CompoundApproval_RejectsExternalEffectsBeforeExecutionAndRetry(string effectKind)
    {
        using var db = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var user = User.Create("Alex", "alex@example.com").Value;
        db.Users.Add(user);
        db.SaveChanges();
        var email = Substitute.For<IEmailService>();
        var supportHandler = new SendSupportCommandHandler(email);
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        mediator.Send(Arg.Any<SendSupportCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => supportHandler.Handle(call.Arg<SendSupportCommand>(), call.Arg<CancellationToken>()));
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 4, 3));
        IAiTool effect = effectKind switch
        {
            "habit" => new CreateHabitTool(dates, mediator),
            "bulk_habit" => new BulkCreateHabitsTool(mediator),
            _ => new SendSupportRequestTool(mediator)
        };
        var failure = new ProbeTool("create_tag", false);
        var registry = new AiToolRegistry(new IAiTool[] { effect, failure });
        var catalog = new AgentCatalogService(registry.GetAll());
        var settings = Options.Create(new AgentPlatformSettings { ShadowModeEnabled = false });
        var store = new PendingAgentOperationStore(db, settings);
        var bridge = Substitute.For<IAgentStepUpAuthorizationBridge>();
        bridge.GetRequiredConfirmationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((AgentConfirmationRequirement?)null);
        var previewer = new PendingOperationChangePreviewer(Substitute.For<IGenericRepository<Habit>>(),
            Substitute.For<IGenericRepository<Goal>>(), Substitute.For<IGenericRepository<Tag>>(), dates, registry);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var ownership = Substitute.For<IAgentTargetOwnershipService>();
        ownership.GetDenialReasonAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        var executor = new AgentOperationExecutor(catalog,
            new AgentPolicyEvaluator(db, catalog, store, bridge, settings, new FeatureFlagService(db, cache)),
            Substitute.For<IAgentAuditService>(), ownership, bridge,
            registry, previewer, store, new UnitOfWork(db, new DatabaseConnectionSettings()),
            NullLogger<AgentOperationExecutor>.Instance);
        var args = effectKind switch
        {
            "habit" => JsonSerializer.SerializeToElement(new { title = "Run", frequency_unit = "Day" }),
            "bulk_habit" => JsonSerializer.SerializeToElement(new { habits = new[] { new { title = "Run", frequency_unit = "Day", tag_ids = Array.Empty<Guid>() } } }),
            _ => JsonSerializer.SerializeToElement(new { name = "Example", email = "alex@example.com", subject = "Help", message = "Please help" })
        };
        var members = new List<PendingAgentOperationExecution>();
        foreach (var tool in new IAiTool[] { effect, failure })
        {
            var held = await executor.ExecuteAsync(new(user.Id, tool.Name, args, AgentExecutionSurface.Chat, AgentAuthMethod.Jwt));
            held.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation, held.Operation.PolicyReason);
            members.Add(store.GetExecution(user.Id, held.PendingOperation!.Id)!);
        }
        var combinedArgs = AccountChangeSet.BuildArguments(members);
        var combinedPreview = (await previewer.PreviewAsync(user.Id, AccountChangeSet.OperationName, combinedArgs)).Value!;
        var fingerprint = AgentOperationFingerprint.Compute(AccountChangeSet.OperationName, combinedArgs.GetRawText());
        var pending = store.Create(user.Id, catalog.GetCapability(AccountChangeSet.CapabilityId)!,
            AccountChangeSet.OperationName, combinedArgs.GetRawText(), "Review", fingerprint, AgentExecutionSurface.Chat);
        store.Revise(user.Id, pending.Id, fingerprint, combinedArgs.GetRawText(), fingerprint, combinedPreview.PreviewFingerprint!);
        var confirmation = store.Confirm(user.Id, pending.Id)!;

        var result = await executor.ExecuteAsync(new(user.Id, AccountChangeSet.OperationName, combinedArgs,
            AgentExecutionSurface.Chat, AgentAuthMethod.Jwt, ConfirmationToken: confirmation.ConfirmationToken));
        result.Operation.Status.Should().NotBe(AgentOperationStatus.Succeeded);
        result.Operation.PolicyReason.Should().Be("unsupported_by_policy");
        var retry = await executor.ExecuteAsync(new(user.Id, AccountChangeSet.OperationName, combinedArgs,
            AgentExecutionSurface.Chat, AgentAuthMethod.Jwt, ConfirmationToken: confirmation.ConfirmationToken));
        retry.Operation.Status.Should().NotBe(AgentOperationStatus.Succeeded);
        await email.DidNotReceive().SendSupportEmailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        failure.Executions.Should().Be(0);
        await mediator.DidNotReceive().Send(Arg.Any<CreateHabitCommand>(), Arg.Any<CancellationToken>());
    }

    private sealed class ProbeTool(string name, bool succeeds) : IAiTool, ITransactionalAiTool
    {
        public bool SupportsAtomicExecution(JsonElement arguments) => true;
        public string Name => name;
        public string Description => name;
        public int Executions { get; private set; }
        public object GetParameterSchema() => new { type = "object", properties = new { name = new { type = "string" } } };
        public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
        {
            Executions++;
            return Task.FromResult(new ToolResult(succeeds, Error: succeeds ? null : "failure"));
        }
    }
}
