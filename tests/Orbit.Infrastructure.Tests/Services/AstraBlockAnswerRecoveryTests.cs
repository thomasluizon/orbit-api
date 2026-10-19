using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Goals.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Api.Extensions;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public class AstraBlockAnswerRecoveryTests
{
    [Fact]
    public async Task FailureCompensatesBothClaimAndQuota_ThenSuccessIsPersistedAndReplayed()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var db = factory.Context;
        db.Model.FindEntityType(typeof(IssuedAstraBlock))!.FindProperty(nameof(IssuedAstraBlock.ResponseJson))!
            .GetColumnType().Should().Be("text");
        var user = User.Create("Alex", "retry@example.com").Value;
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var block = IssuedAstraBlock.Create(user.Id, new("choice", "When?", [new("a", "Morning"), new("b", "Evening")], []));
        var store = new IssuedAstraBlockStore(db);
        await store.AddAsync(block, default);
        var work = new UnitOfWork(db, new DatabaseConnectionSettings());
        var date = new DateOnly(2026, 4, 3);
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(date);
        var config = Substitute.For<IAppConfigService>();
        config.GetAsync(AppConfigKeys.FreeAiMessagesPerDay, AppConstants.DefaultFreeAiMessages, Arg.Any<CancellationToken>()).Returns(5);
        config.GetAsync(AppConfigKeys.ProAiMessagesPerDay, AppConstants.DefaultProAiMessages, Arg.Any<CancellationToken>()).Returns(50);
        config.GetAsync(AppConfigKeys.ProAiMessagesPerDay, AppConstants.DefaultProAiMessages, Arg.Any<CancellationToken>()).Returns(50);
        var users = new GenericRepository<User>(db);
        var quota = new PayGateService(new GenericRepository<Habit>(db), users, config, dates);
        var service = new AstraBlockAnswerService(store, work, quota, Substitute.For<IAgentOperationExecutor>(),
            users, Substitute.For<IProductAnalytics>(), Substitute.For<IFeatureFlagService>(), Substitute.For<IPendingAgentOperationStore>());
        var request = new ProcessUserChatCommand(user.Id, "Evening", BlockAnswer: new(block.Id, ["b"]));

        var failure = await service.ExecuteAsync(request,
            _ => Task.FromResult(Result.Failure<ChatResponse>(ErrorMessages.AiUnavailable)), default);
        failure.IsFailure.Should().BeTrue();
        (await store.GetAsync(block.Id, user.Id, default))!.AnsweredAtUtc.Should().BeNull();
        (await db.Users.AsNoTracking().SingleAsync(item => item.Id == user.Id)).GetAiMessagesUsedToday(date).Should().Be(0);

        var reply = new ChatResponse("Evening works.", [], Parts: [new ChatTextPart("Evening works.")]);
        var success = await service.ExecuteAsync(request, _ => Task.FromResult(Result.Success(reply)), default);
        success.IsSuccess.Should().BeTrue();
        var stored = await store.GetAsync(block.Id, user.Id, default);
        stored!.MatchesAnswer(request.BlockAnswer!).Should().BeTrue();
        stored.ResponseJson.Should().NotBeNull();
        stored.ReservedAllowanceDate.Should().Be(date);
        var replay = await service.ExecuteAsync(request, _ => throw new InvalidOperationException("Replay must not call AI"), default);
        replay.Value.Should().BeEquivalentTo(reply);
        (await db.Users.AsNoTracking().SingleAsync(item => item.Id == user.Id)).GetAiMessagesUsedToday(date).Should().Be(1);
    }
    [Fact]
    public void ReplayColumns_UsePostgresTextMappings()
    {
        using var db = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused").Options);
        var model = db.Model.FindEntityType(typeof(IssuedAstraBlock))!;
        foreach (var name in new[] { nameof(IssuedAstraBlock.ResponseJson), nameof(IssuedAstraBlock.CommittedResponseJson) })
        {
            var property = model.FindProperty(name)!;
            property.GetColumnType().Should().Be("text");
            property.GetRelationalTypeMapping().StoreType.Should().Be("text");
            property.GetRelationalTypeMapping().DbType.Should().Be(System.Data.DbType.String);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbandonedClaim_RecoversOnce_WithoutRepeatingCommittedActions(bool committed)
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var db = factory.Context;
        var user = User.Create("Alex", "stale@example.com").Value;
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var users = new GenericRepository<User>(db);
        var date = new DateOnly(2026, 4, 3);
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(date);
        var config = Substitute.For<IAppConfigService>();
        config.GetAsync(AppConfigKeys.FreeAiMessagesPerDay, AppConstants.DefaultFreeAiMessages, Arg.Any<CancellationToken>()).Returns(5);
        config.GetAsync(AppConfigKeys.ProAiMessagesPerDay, AppConstants.DefaultProAiMessages, Arg.Any<CancellationToken>()).Returns(50);
        var quota = new PayGateService(new GenericRepository<Habit>(db), users, config, dates);
        var store = new IssuedAstraBlockStore(db);
        var work = new UnitOfWork(db, new DatabaseConnectionSettings());
        var block = IssuedAstraBlock.Create(user.Id, new("choice", "When?", [new("a", "Morning"), new("b", "Evening")], []));
        await store.AddAsync(block, default);
        var lifetime = TimeSpan.FromSeconds(new AiSettings().NetworkTimeoutSeconds + new DatabaseConnectionSettings().TransactionTimeoutSeconds);
        var claim = TimeProvider.System.GetUtcNow().UtcDateTime - lifetime - TimeSpan.FromSeconds(1);
        var request = new ProcessUserChatCommand(user.Id, "Evening", BlockAnswer: new(block.Id, ["b"]));
        (await store.MarkAnsweredAsync(block.Id, user.Id, claim, default)).Should().BeTrue();
        (await quota.TryConsumeAiMessage(user.Id, work, default)).IsSuccess.Should().BeTrue();
        await store.RecordAnswerAsync(block.Id, user.Id, claim, IssuedAstraBlock.SerializeAnswer(request.BlockAnswer!), date, null, default);
        var reply = new ChatResponse("Stored reply", committed ? [new("CreateHabit", ActionStatus.Success, EntityName: "Walk")] : []);
        if (committed)
            (await store.CheckpointAnswerAsync(block.Id, user.Id, claim,
                SerializeResponse(reply), default)).Should().BeTrue();
        var service = new AstraBlockAnswerService(store, work, quota, Substitute.For<IAgentOperationExecutor>(),
            users, Substitute.For<IProductAnalytics>(), Substitute.For<IFeatureFlagService>(), Substitute.For<IPendingAgentOperationStore>(),
            claimLifetime: lifetime);
        var calls = 0;
        Task<Result<ChatResponse>> Produce(PreparedAstraBlockAnswer _)
        {
            calls++;
            return Task.FromResult(Result.Success(reply));
        }

        var first = await service.ExecuteAsync(request, Produce, default);
        var second = await service.ExecuteAsync(request, Produce, default);

        first.Value.Should().BeEquivalentTo(reply);
        second.Value.Should().BeEquivalentTo(reply);
        calls.Should().Be(committed ? 0 : 1);
        (await db.Users.AsNoTracking().SingleAsync(item => item.Id == user.Id)).GetAiMessagesUsedToday(date).Should().Be(1);
        (await service.ExecuteAsync(request with { BlockAnswer = new(block.Id, ["a"]) }, Produce, default))
            .ErrorCode.Should().Be(ErrorCodes.AstraBlockAnswered);
        (await store.ReleaseAnswerAsync(block.Id, user.Id, claim, default)).Should().BeFalse();
    }

    [Theory]
    [InlineData(false, "available")]
    [InlineData(true, "available")]
    [InlineData(false, "unavailable")]
    [InlineData(true, "unavailable")]
    [InlineData(false, "throws")]
    [InlineData(true, "throws")]
    [InlineData(false, "missing")]
    [InlineData(true, "missing")]
    public async Task ReplayPickList_ReplaysOnlyTheCurrentPendingRevision(bool revise, string refresh)
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var db = factory.Context;
        var user = User.Create("Alex", "revision@example.com").Value;
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 4, 3));
        var previewer = new PendingOperationChangePreviewer(new GenericRepository<Habit>(db),
            new GenericRepository<Goal>(db), new GenericRepository<Tag>(db), dates, new AiToolRegistry([]));
        var pending = new PendingAgentOperationStore(db, Options.Create(new AgentPlatformSettings()));
        const string originalArgs = """{"habits":[{"title":"Walk","frequency_unit":"Day","preview_item_id":"a"},{"title":"Read","frequency_unit":"Day","preview_item_id":"b"}]}""";
        var capability = new AgentCatalogService().GetCapabilityByChatTool("bulk_create_habits")!
            with
        { ConfirmationRequirement = AgentConfirmationRequirement.FreshConfirmation };
        var held = pending.Create(user.Id, capability, "bulk_create_habits", originalArgs, "Choose",
            AgentOperationFingerprint.Compute("bulk_create_habits", originalArgs), AgentExecutionSurface.Chat);
        var original = await previewer.PreviewAsync(user.Id, "bulk_create_habits", JsonSerializer.Deserialize<JsonElement>(originalArgs));
        pending.Revise(user.Id, held.Id, AgentOperationFingerprint.Compute("bulk_create_habits", originalArgs),
            originalArgs, AgentOperationFingerprint.Compute("bulk_create_habits", originalArgs), original!.PreviewFingerprint!).Should().BeTrue();
        var response = new ChatResponse("Nothing has been saved.", [], PendingOperations:
            [held with { Items = original.Items, Changes = original.Changes,
                ChangeTargetCount = original.ChangeTargetCount, PreviewFingerprint = original.PreviewFingerprint }]);
        var store = new IssuedAstraBlockStore(db);
        var block = IssuedAstraBlock.Create(user.Id, new("pickList", "Choose", [],
            [new("a", "Walk", JsonSerializer.SerializeToElement(new { title = "Walk" }))], true, Action: "create_habits"));
        await store.AddAsync(block, default);
        var claim = block.CreatedAtUtc;
        var request = new ProcessUserChatCommand(user.Id, "Walk", BlockAnswer: new(block.Id, ["a"]));
        await store.MarkAnsweredAsync(block.Id, user.Id, claim, default);
        await store.RecordAnswerAsync(block.Id, user.Id, claim, IssuedAstraBlock.SerializeAnswer(request.BlockAnswer!), null,
            SerializeResponse(response), default);
        const string revisedArgs = """{"habits":[{"title":"Evening walk","frequency_unit":"Day","preview_item_id":"a"}]}""";
        var expected = original;
        if (revise)
        {
            expected = await previewer.PreviewAsync(user.Id, "bulk_create_habits", JsonSerializer.Deserialize<JsonElement>(revisedArgs));
            pending.Revise(user.Id, held.Id, AgentOperationFingerprint.Compute("bulk_create_habits", originalArgs),
                revisedArgs, AgentOperationFingerprint.Compute("bulk_create_habits", revisedArgs), expected!.PreviewFingerprint!).Should().BeTrue();
        }
        var replayPreviewer = Substitute.For<IPendingOperationChangePreviewer>();
        var refreshCalls = 0;
        replayPreviewer.PreviewAsync(user.Id, "bulk_create_habits", Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if ((refresh is "unavailable" or "throws") && ++refreshCalls == 1)
                    return refresh == "throws"
                        ? Task.FromException<PendingOperationChangePreview?>(new InvalidOperationException("Preview unavailable"))
                        : Task.FromResult<PendingOperationChangePreview?>(null);
                return previewer.PreviewAsync(user.Id, "bulk_create_habits", call.Arg<JsonElement>(), call.Arg<CancellationToken>());
            });
        var executor = Substitute.For<IAgentOperationExecutor>();
        var quota = Substitute.For<IPayGateService>();
        var service = new AstraBlockAnswerService(store, new UnitOfWork(db, new DatabaseConnectionSettings()),
            quota, executor, new GenericRepository<User>(db),
            Substitute.For<IProductAnalytics>(), Substitute.For<IFeatureFlagService>(), pending,
            previewer: refresh == "missing" ? null : replayPreviewer);

        var replay = await service.ExecuteAsync(request, _ => throw new InvalidOperationException("No new execution"), default);

        var execution = pending.GetExecution(user.Id, held.Id)!;
        var revisions = new PendingOperationRevisionService(pending, previewer,
            Substitute.For<IValidator<RevisePendingOperationRequest>>(), new AiToolRegistry([]));
        (await revisions.CanApproveAsync(user.Id, execution, default)).Should().BeTrue();
        (await revisions.IsCurrentAsync(user.Id, execution, default)).Should().BeTrue();
        var approvalPreview = await previewer.PreviewAsync(user.Id, execution.OperationId, execution.Arguments);
        approvalPreview!.PreviewFingerprint.Should().Be(expected!.PreviewFingerprint);
        if (revise && refresh != "available")
        {
            replay.IsFailure.Should().BeTrue("a revised operation cannot fall back to an older approvable card");
            replay.ErrorCode.Should().Be(ErrorCodes.AstraBlockAnswered);
            replay.ToPayGateAwareResult(_ => new OkResult()).Should().BeOfType<ObjectResult>()
                .Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);
            (await store.GetAsync(block.Id, user.Id, default))!.ResponseJson.Should().Be(SerializeResponse(response));
            await executor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
            service = new AstraBlockAnswerService(store, new UnitOfWork(db, new DatabaseConnectionSettings()),
                quota, executor, new GenericRepository<User>(db),
                Substitute.For<IProductAnalytics>(), Substitute.For<IFeatureFlagService>(), pending, previewer: replayPreviewer);
            replay = await service.ExecuteAsync(request, _ => throw new InvalidOperationException("No new execution"), default);
        }

        replay.IsSuccess.Should().BeTrue();
        var current = replay.Value.PendingOperations.Should().ContainSingle().Subject;
        current.Id.Should().Be(held.Id);
        JsonSerializer.Serialize(current.Items).Should().Be(JsonSerializer.Serialize(expected.Items));
        current.PreviewFingerprint.Should().Be(approvalPreview.PreviewFingerprint);
        if (!revise && refresh != "available")
            JsonSerializer.Serialize(current).Should().Be(JsonSerializer.Serialize(response.PendingOperations![0]));
        await quota.DidNotReceiveWithAnyArgs().TryConsumeAiMessage(default, default!, default);
        await executor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);

        var controller = new AiController(new AgentCatalogService(), Substitute.For<IAgentPolicyEvaluator>(),
            new AgentPendingStores(pending, Substitute.For<IPendingClarificationStore>()),
            Substitute.For<IAgentStepUpService>(), Substitute.For<IAgentAuditService>(), executor, revisions,
            Substitute.For<IValidator<ResolveClarificationRequest>>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "Test"))
                }
            }
        };
        var confirmation = (await controller.ConfirmPendingOperation(current.Id, default))
            .Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<AiController.ConfirmPendingOperationResponse>().Subject;
        (await controller.ExecutePendingOperation(current.Id,
            new AiController.ExecutePendingOperationRequest(confirmation.ConfirmationToken), default))
            .Should().BeOfType<OkObjectResult>();
        await executor.Received(1).ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(operation =>
            operation.OperationId == execution.OperationId
            && operation.Arguments.GetRawText() == execution.Arguments.GetRawText()
            && operation.ConfirmationToken == confirmation.ConfirmationToken), Arg.Any<CancellationToken>());
        (await service.ExecuteAsync(request, _ => throw new InvalidOperationException("No new execution"), default))
            .Value.PendingOperations.Should().BeEmpty();
    }

    [Fact]
    public async Task FailedMutationAfterSave_RollsBackBeforeReleasingTheClaim()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var db = factory.Context;
        var user = User.Create("Alex", "rollback@example.com").Value;
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var users = new GenericRepository<User>(db);
        var habits = new GenericRepository<Habit>(db);
        var goals = new GenericRepository<Goal>(db);
        var tags = new GenericRepository<Tag>(db);
        var dates = Substitute.For<IUserDateService>();
        var today = new DateOnly(2026, 4, 3);
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(today);
        var quota = Substitute.For<IPayGateService>();
        quota.CanSendAiMessage(user.Id, Arg.Any<CancellationToken>()).Returns(Result.Success());
        quota.TryConsumeAiMessage(user.Id, Arg.Any<IUnitOfWork>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        quota.RefundAiMessage(user.Id, today, Arg.Any<IUnitOfWork>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        quota.CanCreateHabits(user.Id, 1, Arg.Any<CancellationToken>()).Returns(Result.Success());
        var work = new UnitOfWork(db, new DatabaseConnectionSettings());
        var tool = new CreateHabitTool(habits, tags, goals, dates, quota, work);
        var store = new IssuedAstraBlockStore(db);
        var block = IssuedAstraBlock.Create(user.Id, new("choice", "When?", [new("a", "Morning"), new("b", "Evening")], []));
        await store.AddAsync(block, default);
        var executor = Substitute.For<IAgentOperationExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var request = call.Arg<AgentExecuteOperationRequest>();
                var result = await tool.ExecuteAsync(request.Arguments, user.Id, call.Arg<CancellationToken>());
                result.Success.Should().BeTrue();
                (await db.Habits.CountAsync()).Should().Be(1);
                return new AgentExecuteOperationResponse(new("create_habit", "create_habit", AgentRiskClass.Low,
                    AgentConfirmationRequirement.None, AgentOperationStatus.Failed, PolicyReason: "failure_after_save"));
            });
        var ai = Substitute.For<IAiIntentService>();
        ai.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse
            {
                ToolCalls = [new("create_habit", "write", JsonSerializer.Deserialize<JsonElement>(
                    """{"title":"Walk","frequency_unit":"Day"}"""))]
            }));
        var prompt = Substitute.For<ISystemPromptBuilder>();
        prompt.BuildStatic(Arg.Any<PromptBuildRequest>()).Returns("static");
        prompt.BuildDynamic(Arg.Any<PromptBuildRequest>()).Returns("dynamic");
        var flags = Substitute.For<IFeatureFlagService>();
        flags.GetEnabledKeysForUserAsync(user.Id, Arg.Any<CancellationToken>()).Returns(Array.Empty<string>());
        var syncer = Substitute.For<IGoalProgressReadSyncer>();
        syncer.ComputeFreshValuesAsync(user.Id, today, Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, int>());
        var handler = new ProcessUserChatCommandHandler(
            new(habits, goals, users, tags,
                new GenericRepository<ChecklistTemplate>(db), flags),
            new(ai, new AiToolRegistry([tool]), prompt, new AgentCatalogService()),
            new(dates, Substitute.For<IUserStreakService>(), quota, work, Substitute.For<IServiceScopeFactory>(),
                executor, Substitute.For<IPendingClarificationStore>(), syncer, Substitute.For<IGamificationService>(),
                Substitute.For<IMediator>(), Substitute.For<IProductAnalytics>(), Substitute.For<IHabitScheduleLogReader>(),
                store, Substitute.For<IPendingAgentOperationStore>()),
            NullLogger<ProcessUserChatCommandHandler>.Instance, Substitute.For<IAppConfigService>(), Orbit.Infrastructure.Tests.ModerationTestDouble.Clean());

        var response = await handler.Handle(new(user.Id, "Evening", BlockAnswer: new(block.Id, ["b"])), default);

        response.IsFailure.Should().BeTrue();
        (await db.Habits.CountAsync()).Should().Be(0);
        (await store.GetAsync(block.Id, user.Id, default))!.AnsweredAtUtc.Should().BeNull();
    }

    private static string SerializeResponse(ChatResponse response) =>
        JsonDocument.Parse(ChatStreamEvent.Final(response).ToJson()).RootElement.GetProperty("response").GetRawText();

}
