using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Domain.ValueObjects;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public class AstraPickListPreviewTests
{
    [Theory]
    [InlineData("create_habits", false, false)]
    [InlineData("log_habits", false, false)]
    [InlineData("skip_habits", false, false)]
    [InlineData("create_habits", true, false)]
    [InlineData("log_habits", true, false)]
    [InlineData("skip_habits", true, false)]
    [InlineData("create_habits", false, true)]
    [InlineData("create_habits", true, true)]
    public async Task Answer_UsesRealPendingOperationPathAndWritesOnlyTheChosenPreview(string action, bool shadowMode, bool failPreviewFirst)
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var db = factory.Context;
        var user = User.Create("Alex", "alex@example.com").Value;
        user.GrantLifetimePro();
        db.Users.Add(user);
        var today = new DateOnly(2026, 4, 3);
        var habits = Enumerable.Range(0, 4).Select(i => Habit.Create(new HabitCreateParams(user.Id, "Habit " + i, null, null, today)).Value).ToArray();
        db.Habits.AddRange(habits);
        await db.SaveChangesAsync();
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(today);
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var work = new UnitOfWork(db, new DatabaseConnectionSettings());
        var planner = new BulkHabitReplayPlanner(Substitute.For<IIdempotencyContext>(), Substitute.For<IIdempotencyStore>(), work);
        var repo = new GenericRepository<Habit>(db);
        var create = new BulkCreateHabitsTool(mediator);
        var log = new BulkLogHabitsTool(mediator, repo, dates, planner);
        var skip = new BulkSkipHabitsTool(mediator, repo, dates, planner);
        var ask = new AskTool(create, log, skip, dates);
        var arguments = JsonSerializer.SerializeToElement(new
        {
            form = "pickList",
            title = "Choose",
            action,
            items = habits.Select((habit, i) => new
            {
                id = i.ToString(),
                label = habit.Title,
                arguments = action == "create_habits" ? JsonSerializer.SerializeToElement(new { title = "New " + i })
                    : JsonSerializer.SerializeToElement(new { habit_id = habit.Id, date = "2026-04-03" })
            })
        });
        var issued = await ask.ExecuteAsync(arguments, user.Id, default);
        issued.Success.Should().BeTrue();
        var definition = (AstraBlockDefinition)issued.Payload!;
        var block = IssuedAstraBlock.Create(user.Id, definition);
        var store = new IssuedAstraBlockStore(db);
        await store.AddAsync(block, default);
        var tools = new AiToolRegistry([create, log, skip, ask]);
        var catalog = new AgentCatalogService(tools.GetAll());
        var existingFlags = await db.AppFeatureFlags.ToDictionaryAsync(flag => flag.Key);
        foreach (var key in catalog.GetCapabilities().SelectMany(capability => capability.FeatureFlagKeys ?? []).Distinct())
        {
            if (existingFlags.TryGetValue(key, out var flag))
                flag.SetEnabled(true);
            else
                db.AppFeatureFlags.Add(AppFeatureFlag.Create(key, true, null, key));
        }
        await db.SaveChangesAsync();
        var settings = Options.Create(new AgentPlatformSettings { ShadowModeEnabled = shadowMode });
        var bridge = Substitute.For<IAgentStepUpAuthorizationBridge>();
        var pending = new PendingAgentOperationStore(db, settings);
        using var flagCache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var policy = new AgentPolicyEvaluator(db, catalog, pending, bridge, settings, new FeatureFlagService(db, flagCache));
        var previewer = new PendingOperationChangePreviewer(repo, new GenericRepository<Goal>(db),
            new GenericRepository<Tag>(db), dates, tools);
        var ownership = Substitute.For<IAgentTargetOwnershipService>();
        ownership.GetDenialReasonAsync(Arg.Any<string>(), user.Id, Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        var executor = new AgentOperationExecutor(catalog, policy, Substitute.For<IAgentAuditService>(),
            ownership, bridge, tools, previewer, pending, work, NullLogger<AgentOperationExecutor>.Instance);
        AgentExecuteOperationResponse? executionResponse = null;
        var failPreview = failPreviewFirst;
        var recordingExecutor = Substitute.For<IAgentOperationExecutor>();
        recordingExecutor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                executionResponse = await executor.ExecuteAsync(call.Arg<AgentExecuteOperationRequest>(), default);
                if (!failPreview)
                    return executionResponse;
                failPreview = false;
                return executionResponse with { PendingOperation = null };
            });
        var quota = Substitute.For<IPayGateService>();
        quota.TryConsumeAiMessage(user.Id, Arg.Any<IUnitOfWork>(), Arg.Any<CancellationToken>()).Returns(Result.PayGateFailure("Daily limit"));
        var service = new AstraBlockAnswerService(store, work, quota, recordingExecutor, new GenericRepository<User>(db),
            Substitute.For<IProductAnalytics>(), Substitute.For<IFeatureFlagService>(), pending);
        if (failPreviewFirst)
        {
            var failed = await service.PrepareAsync(new(user.Id, "Habit 1, Habit 3", BlockAnswer: new(block.Id, ["3", "1"])), default);
            failed.ErrorCode.Should().Be(ErrorCodes.AstraBlockPreviewUnavailable);
            (await store.GetAsync(block.Id, user.Id, default))!.AnsweredAtUtc.Should().BeNull();
            (await db.PendingAgentOperations.CountAsync()).Should().Be(0);
        }
        var answer = await service.PrepareAsync(new ProcessUserChatCommand(user.Id, "Habit 1, Habit 3",
            BlockAnswer: new(block.Id, ["3", "1"])), default);
        answer.IsSuccess.Should().BeTrue(JsonSerializer.Serialize(executionResponse));
        answer.Value.Response!.PendingOperations.Should().ContainSingle().Which.Items.Should().HaveCount(2);
        var saved = await db.PendingAgentOperations.AsNoTracking().SingleAsync();
        var selected = JsonDocument.Parse(saved.ArgumentsJson).RootElement.GetProperty(action == "create_habits" ? "habits" : "revised_items");
        selected.EnumerateArray().Select(item => item.GetRawText()).Should().Equal(definition.Items[1].Arguments.GetRawText(), definition.Items[3].Arguments.GetRawText());
        (await db.Habits.CountAsync()).Should().Be(4);
        (await db.HabitLogs.CountAsync()).Should().Be(0);
        await quota.DidNotReceiveWithAnyArgs().TryConsumeAiMessage(default, default!, default);
        mediator.ReceivedCalls().Should().OnlyContain(call => call.GetArguments()[0] is CheckChatCommandQuery);
        var identicalReplay = await service.PrepareAsync(new(user.Id, "Habit 1, Habit 3", BlockAnswer: new(block.Id, ["1", "3"])), default);
        identicalReplay.IsSuccess.Should().BeTrue();
        var originalJson = Orbit.Application.Chat.Models.ChatStreamEvent.Final(answer.Value.Response!).ToJson();
        var replayJson = Orbit.Application.Chat.Models.ChatStreamEvent.Final(identicalReplay.Value.Response!).ToJson();
        System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(originalJson),
            System.Text.Json.Nodes.JsonNode.Parse(replayJson)).Should().BeTrue();
        (await db.PendingAgentOperations.CountAsync()).Should().Be(1);
        await recordingExecutor.Received(failPreviewFirst ? 2 : 1).ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>());
        pending.Confirm(user.Id, saved.Id).Should().NotBeNull();
        var closedReplay = await service.PrepareAsync(new(user.Id, "Habit 1, Habit 3", BlockAnswer: new(block.Id, ["1", "3"])), default);
        closedReplay.IsSuccess.Should().BeTrue();
        closedReplay.Value.Response!.PendingOperations.Should().BeEmpty();
        var replay = await service.PrepareAsync(new ProcessUserChatCommand(user.Id, "Habit 1", BlockAnswer: new(block.Id, ["1"])), default);
        replay.ErrorCode.Should().Be(ErrorCodes.AstraBlockAnswered);
        (await db.PendingAgentOperations.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredOrInvalidatedAnswer_ReturnsGoneWithoutMutation(bool invalidated)
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var db = factory.Context;
        var user = User.Create("Alex", "alex@example.com").Value;
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var block = IssuedAstraBlock.Create(user.Id, new("choice", "When?", [new("a", "Morning"), new("b", "Evening")], []));
        var store = new IssuedAstraBlockStore(db);
        await store.AddAsync(block, default);
        if (invalidated)
            await store.InvalidateOpenAsync(user.Id, default);
        else
            await db.IssuedAstraBlocks.Where(item => item.Id == block.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ExpiresAtUtc, block.CreatedAtUtc.AddSeconds(-1)));
        var quota = Substitute.For<IPayGateService>();
        var service = new AstraBlockAnswerService(store, new UnitOfWork(db, new DatabaseConnectionSettings()), quota,
            Substitute.For<IAgentOperationExecutor>(), new GenericRepository<User>(db), Substitute.For<IProductAnalytics>(),
            Substitute.For<IFeatureFlagService>(), Substitute.For<IPendingAgentOperationStore>());
        (await service.PrepareAsync(new(user.Id, "Morning", BlockAnswer: new(block.Id, ["a"])), default)).ErrorCode.Should().Be(invalidated ? ErrorCodes.AstraBlockInvalidated : ErrorCodes.AstraBlockExpired);
        (await store.GetAsync(block.Id, user.Id, default))!.AnsweredAtUtc.Should().BeNull();
        await quota.DidNotReceiveWithAnyArgs().TryConsumeAiMessage(default, default!, default);
    }

    [Fact]
    public async Task QuotaRace_RollsBackTheChoiceClaim()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var db = factory.Context;
        var user = User.Create("Alex", "alex@example.com").Value;
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var block = IssuedAstraBlock.Create(user.Id, new("choice", "When?", [new("a", "Morning"), new("b", "Evening")], []));
        var store = new IssuedAstraBlockStore(db);
        await store.AddAsync(block, default);
        var quota = Substitute.For<IPayGateService>();
        var work = new UnitOfWork(db, new DatabaseConnectionSettings());
        quota.TryConsumeAiMessage(user.Id, work, Arg.Any<CancellationToken>()).Returns(Result.PayGateFailure("Daily limit"));
        var service = new AstraBlockAnswerService(store, work, quota, Substitute.For<IAgentOperationExecutor>(),
            new GenericRepository<User>(db), Substitute.For<IProductAnalytics>(), Substitute.For<IFeatureFlagService>(),
            Substitute.For<IPendingAgentOperationStore>());
        (await service.PrepareAsync(new(user.Id, "Morning", BlockAnswer: new(block.Id, ["a"])), default)).ErrorCode.Should().Be(Result.PayGateErrorCode);
        (await store.GetAsync(block.Id, user.Id, default))!.AnsweredAtUtc.Should().BeNull();
    }
}
