using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Gamification.Jobs;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Referrals.Jobs;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Events;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public sealed partial class HabitEffectPostgresTests
{
    private static readonly string[] UnversionedTables =
        ["__EFMigrationsHistory", "PendingAgentOperations", "AgentAuditLogs", "TransactionCommits"];

    public static TheoryData<string> HabitKinds =>
    [
        "assign_tags", "bulk_update_habits", "bulk_reschedule_habits", "bulk_skip_habits", "bulk_delete_habits",
        "update_habit", "skip_habit", "create_sub_habit", "bulk_create_habits", "create_habit", "unlog_habit",
        "log_habit", "bulk_log_habits", "undo_skip_habit", "delete_habit", "duplicate_habit", "move_habit",
        "move_habit_parent", "reorder_habits", "update_checklist", "bulk_update_habit_emojis", "link_goals_to_habit"
    ];

    [PostgresTheory]
    [MemberData(nameof(HabitKinds))]
    public async Task SetWithAFailingMember_LeavesNoRowNoCallbackNoEventAndNoCacheChange(string kind)
    {
        await using var app = await StartAsync();

        await AssertAFailingMemberLeavesNothingAsync(app, kind, async () =>
        {
            var world = await SeedWorldAsync(app);
            return (world, world.ArgumentsFor(kind));
        });
    }

    private async Task AssertAFailingMemberLeavesNothingAsync(PostgresOrbitApp app, string kind,
        Func<Task<(World World, object Arguments)>> seed)
    {
        var (world, arguments) = await seed();
        var member = await HoldAsync(app, world.User.Id, kind, arguments);
        var failing = await HoldFailingUpdateAsync(app, world);
        var before = await SnapshotAsync(app);
        var jobs = app.Jobs.Created.Count;
        app.Bus.ClearReceivedCalls();

        var approved = await ApproveSetAsync(app, world.User.Id, member, failing);

        approved.Operation.Status.Should().Be(AgentOperationStatus.Failed, approved.Operation.PolicyReason);
        _failingUpdate.Failures.Should().Be(1, "the set reaches its failing member after the first member writes");
        (await SnapshotAsync(app)).Should().BeEquivalentTo(before);
        app.Jobs.Created.Should().HaveCount(jobs);
        app.Bus.DidNotReceiveWithAnyArgs().Publish(default, default!, default);

        var (controlWorld, controlArguments) = await seed();
        var alone = await HoldAsync(app, controlWorld.User.Id, kind, controlArguments);
        var beforeControl = await SnapshotAsync(app);

        var control = await ApproveAsync(app, controlWorld.User.Id, alone.Id);

        control.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, control.Operation.PolicyReason);
        (await SnapshotAsync(app)).Should().NotBeEquivalentTo(beforeControl,
            "the same member writes a row when no later member fails, so the equal snapshot above proves a rollback");
    }

    [PostgresFact]
    public async Task SetWithAFailingEffect_SavesEveryMemberAndPublishesTheirEventsWithTheOrigin()
    {
        await using var app = await StartAsync();
        var world = await SeedWorldAsync(app);
        var log = await HoldAsync(app, world.User.Id, "log_habit", new { habit_id = world.Run.Id });
        var rename = await HoldAsync(app, world.User.Id, "update_habit", new { habit_id = world.Read.Id, title = "Study" });
        var delete = await HoldAsync(app, world.User.Id, "delete_habit", new { habit_id = world.Morning.Id });
        var version = await CacheVersionAsync(app, world.User.Id);
        app.Bus.ClearReceivedCalls();
        _faults.FailReadsOf("UserAchievements");

        var approved = await ApproveSetAsync(app, world.User.Id, log, rename, delete);

        approved.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, approved.Operation.PolicyReason);
        _faults.FailedReads.Should().BePositive();
        (await app.ReadAsync(db => db.HabitLogs.CountAsync(saved => saved.HabitId == world.Run.Id))).Should().Be(1);
        (await app.ReadAsync(db => db.Habits.SingleAsync(saved => saved.Id == world.Read.Id))).Title.Should().Be("Study");
        (await app.ReadAsync(db => db.Habits.IgnoreQueryFilters().SingleAsync(saved => saved.Id == world.Morning.Id)))
            .IsDeleted.Should().BeTrue();
        var payload = PublishedPayload(app, world.User.Id);
        payload.Origin.Should().Be(PostgresOrbitApp.Origin);
        payload.Changes.Should().Contain(change => change.Kind == "habitLog" && change.Op == "create");
        payload.Changes.Should().Contain(change => change.Kind == "habit" && change.Op == "update" && change.Ids.Contains(world.Read.Id));
        payload.Changes.Should().Contain(change => change.Kind == "habit" && change.Op == "delete" && change.Ids.Contains(world.Morning.Id));
        (await CacheVersionAsync(app, world.User.Id)).Should().BeGreaterThan(version);
    }

    [PostgresFact]
    public async Task SetWithADuplicateLog_ReturnsTheTypedFailureAndRollsBack()
    {
        await using var app = await StartAsync();
        var world = await SeedWorldAsync(app);
        var walk = await SeedHabitAsync(app, world.User.Id, "Walk", loggedDaysAgo: [4]);
        var rename = await HoldAsync(app, world.User.Id, "update_habit", new { habit_id = world.Read.Id, title = "Study" });
        var log = await HoldAsync(app, world.User.Id, "log_habit", new { habit_id = walk.Id });
        await ArmConcurrentLogAsync(app, walk.Id, world.Today);

        var approved = await ApproveSetAsync(app, world.User.Id, rename, log);

        _faults.Fired.Should().BeTrue();
        approved.Operation.Status.Should().Be(AgentOperationStatus.Failed);
        approved.Operation.ErrorCode.Should().Be(DomainErrors.AlreadyLoggedForDate.Code,
            "the duplicate is the typed failure of the log member, not a database exception");
        (await app.ReadAsync(db => db.Habits.SingleAsync(saved => saved.Id == world.Read.Id))).Title.Should().Be("Read");
        (await app.ReadAsync(db => db.HabitLogs.CountAsync(saved => saved.HabitId == walk.Id))).Should().Be(1,
            "only the log that the other connection moved to today stays");
    }

    [PostgresFact]
    public async Task CommittedSet_RunsItsCallbacksRewardsTheReferralAndRaisesTheCacheVersion()
    {
        await using var app = await StartAsync();
        var world = await SeedWorldAsync(app);
        var referral = await SeedQualifyingReferralAsync(app, world);
        await SeedXpBelowTheNextLevelAsync(app, world.User.Id);
        var log = await HoldAsync(app, world.User.Id, "log_habit", new { habit_id = world.Run.Id });
        var rename = await HoldAsync(app, world.User.Id, "update_habit", new { habit_id = world.Read.Id, title = "Study" });
        var version = await CacheVersionAsync(app, world.User.Id);

        var approved = await ApproveSetAsync(app, world.User.Id, log, rename);

        approved.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, approved.Operation.PolicyReason);
        app.Jobs.Created.Should().Contain(job => job.Type == typeof(SendGamificationPushJob));
        app.Jobs.Created.Should().Contain(job => job.Type == typeof(DeliverReferralRewardJob));
        (await app.ReadAsync(db => db.Referrals.SingleAsync(saved => saved.Id == referral))).Status
            .Should().NotBe(ReferralStatus.Pending);
        (await CacheVersionAsync(app, world.User.Id)).Should().BeGreaterThan(version);
    }

    [PostgresFact]
    public async Task RolledBackSet_RunsNoCallbackDeliversNoReferralRewardAndPublishesNothing()
    {
        await using var app = await StartAsync();
        var world = await SeedWorldAsync(app);
        var referral = await SeedQualifyingReferralAsync(app, world);
        await SeedXpBelowTheNextLevelAsync(app, world.User.Id);
        var log = await HoldAsync(app, world.User.Id, "log_habit", new { habit_id = world.Run.Id });
        var failing = await HoldFailingUpdateAsync(app, world);
        var version = await CacheVersionAsync(app, world.User.Id);
        app.Bus.ClearReceivedCalls();

        var approved = await ApproveSetAsync(app, world.User.Id, log, failing);

        approved.Operation.Status.Should().Be(AgentOperationStatus.Failed);
        app.Jobs.Created.Should().NotContain(job => job.Type == typeof(SendGamificationPushJob));
        app.Jobs.Created.Should().Contain(job => job.Type == typeof(DeliverReferralRewardJob),
            "the referral check enqueues its reward job before the commit on purpose");
        (await app.ReadAsync(db => db.Referrals.SingleAsync(saved => saved.Id == referral))).Status
            .Should().Be(ReferralStatus.Pending);
        await using (var scope = app.CreateScope())
        {
            var deliver = () => scope.ServiceProvider.GetRequiredService<DeliverReferralRewardJob>()
                .ExecuteAsync(referral, world.User.Id, CancellationToken.None);
            await deliver.Should().ThrowAsync<InvalidOperationException>();
        }
        app.Bus.DidNotReceiveWithAnyArgs().Publish(default, default!, default);
        (await CacheVersionAsync(app, world.User.Id)).Should().Be(version);
    }

    [PostgresFact]
    public async Task SetWithABulkLogWhoseItemFails_RollsBackAndLeavesNoLog()
    {
        await using var app = await StartAsync();
        var world = await SeedWorldAsync(app);
        var walk = await SeedHabitAsync(app, world.User.Id, "Walk", loggedDaysAgo: [4]);
        var log = await HoldAsync(app, world.User.Id, "bulk_log_habits", new { habit_ids = new[] { world.Run.Id, walk.Id } });
        var rename = await HoldAsync(app, world.User.Id, "update_habit", new { habit_id = world.Morning.Id, title = "Dawn" });
        await ArmConcurrentLogAsync(app, walk.Id, world.Today);

        var approved = await ApproveSetAsync(app, world.User.Id, rename, log);

        _faults.Fired.Should().BeTrue();
        approved.Operation.Status.Should().Be(AgentOperationStatus.Failed);
        (await app.ReadAsync(db => db.HabitLogs.CountAsync(saved => saved.HabitId == world.Run.Id))).Should().Be(0);
        (await app.ReadAsync(db => db.Habits.SingleAsync(saved => saved.Id == world.Morning.Id))).Title.Should().Be("Morning");
    }

    [PostgresFact]
    public async Task EmojiMember_ExecutesItsStoredRevisedItemsWithNoModelCall()
    {
        await using var app = await StartAsync();
        var world = await SeedWorldAsync(app);
        var emojis = await HoldAsync(app, world.User.Id, "bulk_update_habit_emojis",
            new { infer_from_title = false, revised_items = new[] { new { habit_id = world.Run.Id, emoji = "🏃" } } });
        var rename = await HoldAsync(app, world.User.Id, "update_habit", new { habit_id = world.Read.Id, title = "Study" });

        var approved = await ApproveSetAsync(app, world.User.Id, emojis, rename);

        approved.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, approved.Operation.PolicyReason);
        (await app.ReadAsync(db => db.Habits.SingleAsync(saved => saved.Id == world.Run.Id))).Emoji.Should().Be("🏃");
        await _emojiInference.DidNotReceiveWithAnyArgs().InferAsync(default, default!, default);
    }

    [PostgresTheory]
    [InlineData("log_habit")]
    [InlineData("delete_habit")]
    [InlineData("create_habit")]
    public async Task SingleApproval_FailingEffectDoesNotFailTheWrite(string kind)
    {
        await using var app = await StartAsync();
        var world = await SeedWorldAsync(app);
        var held = await HoldAsync(app, world.User.Id, kind, world.ArgumentsFor(kind));
        _faults.FailReadsOf("UserAchievements");

        var approved = await ApproveAsync(app, world.User.Id, held.Id);

        approved.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, approved.Operation.PolicyReason);
        switch (kind)
        {
            case "log_habit":
                _faults.FailedReads.Should().BePositive();
                (await app.ReadAsync(db => db.HabitLogs.CountAsync(saved => saved.HabitId == world.Run.Id))).Should().Be(1);
                break;
            case "delete_habit":
                (await app.ReadAsync(db => db.Habits.IgnoreQueryFilters().SingleAsync(saved => saved.Id == world.Read.Id)))
                    .IsDeleted.Should().BeTrue();
                break;
            default:
                _faults.FailedReads.Should().BePositive();
                (await app.ReadAsync(db => db.Habits.CountAsync(saved => saved.Title == "Meditate"))).Should().Be(1);
                break;
        }
    }

    private async Task<PendingAgentOperation> HoldFailingUpdateAsync(PostgresOrbitApp app, World world)
    {
        _failingUpdate.FailHabitId = world.Fails.Id;
        return await HoldAsync(app, world.User.Id, "update_habit", new { habit_id = world.Fails.Id, title = "Fails later" });
    }

    private static async Task<PendingAgentOperation> HoldAsync(PostgresOrbitApp app, Guid userId, string operationId, object arguments)
    {
        await using var scope = app.CreateScope();
        var held = await scope.ServiceProvider.GetRequiredService<AgentOperationExecutor>().ExecuteAsync(new(userId,
            operationId, JsonSerializer.SerializeToElement(arguments), AgentExecutionSurface.Chat, AgentAuthMethod.Jwt));
        held.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation,
            $"{operationId} is held for approval: {held.Operation.PolicyReason} {held.Operation.ErrorCode}");
        return held.PendingOperation!;
    }

    private static async Task<AgentExecuteOperationResponse> ApproveSetAsync(PostgresOrbitApp app, Guid userId,
        params PendingAgentOperation[] members)
    {
        Guid setId;
        await using (var scope = app.CreateScope())
        {
            var services = scope.ServiceProvider;
            var changeSets = new AccountChangeSetService(services.GetRequiredService<IPendingAgentOperationStore>(),
                services.GetRequiredService<IPendingOperationChangePreviewer>(), services.GetRequiredService<IAgentCatalogService>(),
                services.GetRequiredService<AiToolRegistry>());
            var combined = await changeSets.CombineAsync(userId, members, default, requireAllMembers: true);
            combined.Should().NotBeNull("every atomic kind joins one change set");
            setId = combined!.PendingOperation.Id;
        }
        return await ApproveAsync(app, userId, setId);
    }

    private static async Task<AgentExecuteOperationResponse> ApproveAsync(PostgresOrbitApp app, Guid userId, Guid pendingId)
    {
        await using var scope = app.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IPendingAgentOperationStore>();
        var execution = store.GetExecution(userId, pendingId)!;
        var confirmation = store.Confirm(userId, pendingId)!;
        return await scope.ServiceProvider.GetRequiredService<AgentOperationExecutor>().ExecuteAsync(new(userId,
            execution.OperationId, execution.Arguments, execution.Surface, AgentAuthMethod.Jwt,
            ConfirmationToken: confirmation.ConfirmationToken));
    }

    private static AccountEventPayload PublishedPayload(PostgresOrbitApp app, Guid userId)
    {
        var call = app.Bus.ReceivedCalls().Should().ContainSingle(received =>
            received.GetMethodInfo().Name == nameof(IAccountEventBus.Publish)).Subject;
        call.GetArguments()[0].Should().Be(userId);
        return (AccountEventPayload)call.GetArguments()[1]!;
    }

    private static Task<long> CacheVersionAsync(PostgresOrbitApp app, Guid userId) => app.ReadAsync(db =>
        db.UserCacheVersions.Where(row => row.Id == userId).Select(row => (long?)row.Version).SingleOrDefaultAsync()
            .ContinueWith(read => read.Result ?? 0, TaskScheduler.Default));

    private static async Task<IReadOnlyDictionary<string, string>> SnapshotAsync(PostgresOrbitApp app)
    {
        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync();
        var tables = new List<string>();
        await using (var list = new NpgsqlCommand("""
            SELECT table_name FROM information_schema.tables
            WHERE table_schema = 'public' AND table_type = 'BASE TABLE' ORDER BY table_name
            """, connection))
        await using (var reader = await list.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                tables.Add(reader.GetString(0));
        var snapshot = new Dictionary<string, string>();
        foreach (var table in tables.Except(UnversionedTables))
        {
            await using var hash = new NpgsqlCommand(
                $"SELECT coalesce(md5(string_agg(row_value::text, '|' ORDER BY row_value::text)), '') FROM \"{table}\" AS row_value",
                connection);
            snapshot[table] = (string)(await hash.ExecuteScalarAsync())!;
        }
        return snapshot;
    }

    private static async Task<World> SeedWorldAsync(PostgresOrbitApp app)
    {
        var user = await SeedUserAsync(app);
        var today = await TodayAsync(app, user.Id);
        var goal = await SeedGoalAsync(app, user.Id, GoalType.Standard);
        var habits = new Dictionary<string, Habit>();
        foreach (var title in new[] { "Run", "Read", "Morning", "Fails", "Swim" })
            habits[title] = await SeedHabitAsync(app, user.Id, title);
        habits["Stretch"] = await SeedHabitAsync(app, user.Id, "Stretch", loggedDaysAgo: [0]);
        Guid tagId;
        await using (var scope = app.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
            var tag = Tag.Create(user.Id, "Health", "#7c3aed").Value;
            db.Tags.Add(tag);
            await db.SaveChangesAsync();
            tagId = tag.Id;
        }
        string skipId;
        await using (var scope = app.CreateScope())
        {
            var skipped = await Tool(scope, "skip_habit").ExecuteAsync(
                JsonSerializer.SerializeToElement(new { habit_id = habits["Swim"].Id }), user.Id, default);
            skipped.Success.Should().BeTrue(skipped.Error);
            skipId = JsonSerializer.SerializeToElement(skipped.Payload).GetProperty("skip_id").GetString()!;
        }
        return new World(user, today, goal.Id, tagId, skipId, habits);
    }

    private static async Task<Guid> SeedQualifyingReferralAsync(PostgresOrbitApp app, World world)
    {
        await using var scope = app.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var referrer = User.Create("Referrer", $"referrer-{Guid.NewGuid():N}@example.com").Value;
        db.Users.Add(referrer);
        var referral = Referral.Create(referrer.Id, world.User.Id);
        db.Referrals.Add(referral);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Referrals\" SET \"CreatedAtUtc\" = {DateTime.UtcNow.AddDays(-3)} WHERE \"Id\" = {referral.Id}");
        var run = await db.Habits.Include(habit => habit.Logs).SingleAsync(habit => habit.Id == world.Run.Id);
        foreach (var daysAgo in new[] { 2, 1 })
            db.HabitLogs.Add(run.Log(world.Today.AddDays(-daysAgo), advanceDueDate: false).Value);
        await db.SaveChangesAsync();
        return referral.Id;
    }

    private static async Task SeedXpBelowTheNextLevelAsync(PostgresOrbitApp app, Guid userId)
    {
        await using var scope = app.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var user = await db.Users.SingleAsync(saved => saved.Id == userId);
        user.AddXp(99);
        await db.SaveChangesAsync();
    }

    private sealed record World(User User, DateOnly Today, Guid GoalId, Guid TagId, string SkipId,
        IReadOnlyDictionary<string, Habit> Habits)
    {
        public Habit Run => Habits["Run"];
        public Habit Read => Habits["Read"];
        public Habit Morning => Habits["Morning"];
        public Habit Fails => Habits["Fails"];

        public object ArgumentsFor(string kind) => kind switch
        {
            "assign_tags" => new { habit_id = Run.Id, tag_ids = new[] { TagId } },
            "bulk_update_habits" => new { filter = new { habit_ids = new[] { Run.Id, Read.Id } }, updates = new { description = "Daily" } },
            "bulk_reschedule_habits" => new { revised_items = new[] { new { habit_id = Run.Id, updates = new { due_date = Date(2) } } } },
            "bulk_skip_habits" => new { habit_ids = new[] { Run.Id, Read.Id } },
            "bulk_delete_habits" => new { habit_ids = new[] { Run.Id, Read.Id } },
            "update_habit" => new { habit_id = Run.Id, goal_ids = new[] { GoalId } },
            "skip_habit" => new { habit_id = Run.Id },
            "create_sub_habit" => new { parent_habit_id = Morning.Id, title = "Make the bed", frequency_unit = "Day" },
            "bulk_create_habits" => new { habits = new[] { new { title = "Write", frequency_unit = "Day", goal_ids = new[] { GoalId } } } },
            "create_habit" => new { title = "Meditate", frequency_unit = "Day" },
            "unlog_habit" => new { habit_id = Habits["Stretch"].Id },
            "log_habit" => new { habit_id = Run.Id },
            "bulk_log_habits" => new { habit_ids = new[] { Run.Id, Read.Id } },
            "undo_skip_habit" => new { habit_id = Habits["Swim"].Id, skip_id = SkipId },
            "delete_habit" => new { habit_id = Read.Id },
            "duplicate_habit" => new { habit_id = Run.Id },
            "move_habit" => new { habit_id = Read.Id, new_parent_id = Morning.Id },
            "move_habit_parent" => new { habit_id = Read.Id, parent_id = Morning.Id },
            "reorder_habits" => new { positions = new[] { new { habit_id = Run.Id, position = 1 }, new { habit_id = Read.Id, position = 0 } } },
            "update_checklist" => new { habit_id = Run.Id, checklist_items = new[] { new { text = "Shoes", is_checked = false } } },
            "bulk_update_habit_emojis" => new { infer_from_title = false, revised_items = new[] { new { habit_id = Run.Id, emoji = "🏃" } } },
            "link_goals_to_habit" => new { habit_id = Run.Id, goal_ids = new[] { GoalId } },
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No arguments for this kind.")
        };

        private string Date(int days) => Today.AddDays(days).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Fails one habit's update after the update writes, as a later set member that fails does.</summary>
    private sealed class FailAfterWriteBehavior : IPipelineBehavior<UpdateHabitFieldsCommand, Result<string>>
    {
        public Guid? FailHabitId { get; set; }
        public int Failures { get; private set; }

        private Func<Task>? _afterWrite;

        /// <summary>Runs <paramref name="afterWrite"/> once after the next update writes, while the transaction of the set is still open.</summary>
        public void RunAfterTheNextWrite(Func<Task> afterWrite) => _afterWrite = afterWrite;

        public async Task<Result<string>> Handle(UpdateHabitFieldsCommand request, RequestHandlerDelegate<Result<string>> next,
            CancellationToken cancellationToken)
        {
            var result = await next(cancellationToken);
            if (Interlocked.Exchange(ref _afterWrite, null) is { } afterWrite)
                await afterWrite();
            if (request.HabitId != FailHabitId) return result;
            Failures++;
            throw new InvalidOperationException("Simulated failure after the member's write");
        }
    }
}
