using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Gamification.Jobs;
using Orbit.Application.Notifications.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// The goal, tag, checklist template, profile, notification and referral kinds as members of a change set, on real
/// PostgreSQL. A failing effect is a real failed statement, as in the habit tests.
/// </summary>
public sealed partial class HabitEffectPostgresTests
{
    private const string Endpoint = "fcm-token-of-the-device";
    private readonly FailOneDeletionBehavior _failingDeletion = new();
    private readonly List<bool> _pushTransactions = [];

    public static TheoryData<string> OtherKinds =>
    [
        "create_goal", "update_goal", "delete_goal", "update_goal_status", "update_goal_progress", "link_habits_to_goal",
        "reorder_goals", "create_tag", "update_tag", "delete_tag", "create_checklist_template",
        "update_checklist_template", "delete_checklist_template", "update_profile_preferences", "set_ai_summary",
        "set_proactive_astra", "update_notifications", "delete_notifications", "get_referral_code"
    ];

    [PostgresTheory]
    [MemberData(nameof(OtherKinds))]
    public async Task SetWithAnOtherKindAndAFailingMember_LeavesNoRowNoCallbackNoEventAndNoCacheChange(string kind)
    {
        await using var app = await StartAsync();

        await AssertAFailingMemberLeavesNothingAsync(app, kind, async () =>
        {
            var account = await SeedAccountAsync(app);
            return (account.World, account.ArgumentsFor(kind));
        });
    }

    [PostgresFact]
    public async Task FeatureFlagChangeThroughTheExecutor_JoinsTheOpenTransactionAndRollsBackWithIt()
    {
        await using var app = await StartAsync();
        var admin = await SeedUserAsync(app);
        await app.ExecuteSqlAsync($"UPDATE \"Users\" SET \"IsAdmin\" = TRUE WHERE \"Id\" = '{admin.Id}'");
        var request = new AgentExecuteOperationRequest(admin.Id, "manage_feature_flags",
            JsonSerializer.SerializeToElement(new { action = "create", key = "change_set_probe", description = "A probe of the change set rules" }),
            AgentExecutionSurface.Mcp, AgentAuthMethod.OAuth, [AgentScopes.ManageFeatureFlags]);

        var rolledBack = async () => await InTransactionAsync<bool>(app, async (scope, ct) =>
        {
            var response = await scope.ServiceProvider.GetRequiredService<AgentOperationExecutor>().ExecuteAsync(request, ct);
            response.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, response.Operation.PolicyReason);
            throw new InvalidOperationException("A later member fails");
        });

        await rolledBack.Should().ThrowAsync<InvalidOperationException>();
        (await app.ReadAsync(db => db.AppFeatureFlags.CountAsync(flag => flag.Key == "change_set_probe"))).Should().Be(0);

        AgentExecuteOperationResponse alone;
        await using (var scope = app.CreateScope())
            alone = await scope.ServiceProvider.GetRequiredService<AgentOperationExecutor>().ExecuteAsync(request);

        alone.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, alone.Operation.PolicyReason);
        (await app.ReadAsync(db => db.AppFeatureFlags.CountAsync(flag => flag.Key == "change_set_probe"))).Should().Be(1);
    }

    [PostgresFact]
    public async Task FeatureFlagChangeInsideTransaction_ClearsTheFlagCacheAfterTheCommit()
    {
        await using var app = await StartAsync();
        var admin = await SeedUserAsync(app);
        await app.ExecuteSqlAsync($"UPDATE \"Users\" SET \"IsAdmin\" = TRUE WHERE \"Id\" = '{admin.Id}'");
        await using (var scope = app.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
            db.AppFeatureFlags.Add(AppFeatureFlag.Create("change_set_probe", false));
            await db.SaveChangesAsync();
        }

        var readInsideTheTransaction = await InTransactionAsync(app, async (scope, ct) =>
        {
            var changed = await Tool(scope, "manage_feature_flags").ExecuteAsync(
                JsonSerializer.SerializeToElement(new { action = "enable_for_everyone", key = "change_set_probe" }), admin.Id, ct);
            changed.Success.Should().BeTrue(changed.Error);
            return await EnabledFlagsAsync(app, admin.Id);
        });

        readInsideTheTransaction.Should().NotContain("change_set_probe", "another request reads the committed row");
        (await EnabledFlagsAsync(app, admin.Id)).Should().Contain("change_set_probe",
            "the flag cache empties after the commit, so a read during the transaction cannot keep the old flags");
    }

    private static async Task<IReadOnlyList<string>> EnabledFlagsAsync(PostgresOrbitApp app, Guid userId)
    {
        await using var scope = app.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IFeatureFlagService>().GetEnabledKeysForUserAsync(userId);
    }

    [PostgresFact]
    public async Task CreateGoalInsideTransaction_FailedEffectQuery_DoesNotAbortLaterStatements()
    {
        await using var app = await StartAsync();
        var user = await SeedUserAsync(app);
        _faults.FailReadsOf("UserAchievements");

        var result = await InTransactionAsync(app, async (scope, ct) =>
        {
            var created = await Tool(scope, "create_goal").ExecuteAsync(
                JsonSerializer.SerializeToElement(new { title = "Read 12 books", target_value = 12, unit = "books" }), user.Id, ct);
            var count = await scope.ServiceProvider.GetRequiredService<OrbitDbContext>().Goals.CountAsync(ct);
            return (created, count);
        });

        result.created.Success.Should().BeTrue(result.created.Error);
        _faults.FailedReads.Should().BePositive();
        result.count.Should().Be(1);
        (await app.ReadAsync(db => db.Goals.CountAsync())).Should().Be(1);
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetWithACreateGoalWhoseGamificationFails_SavesEveryMemberAndPublishesTheirEvents(bool failsOnInsert)
    {
        await using var app = await StartAsync();
        var world = await SeedWorldAsync(app);
        await app.ExecuteSqlAsync($"DELETE FROM \"Goals\" WHERE \"UserId\" = '{world.User.Id}'");
        var goal = await HoldAsync(app, world.User.Id, "create_goal", new { title = "Read 12 books", target_value = 12, unit = "books" });
        var rename = await HoldAsync(app, world.User.Id, "update_habit", new { habit_id = world.Read.Id, title = "Study" });
        var tag = await HoldAsync(app, world.User.Id, "create_tag", new { name = "Focus", color = "#112233" });
        var version = await CacheVersionAsync(app, world.User.Id);
        app.Bus.ClearReceivedCalls();
        if (failsOnInsert)
            await FailXpAwardsAsync(app, times: 1000);
        else
            _faults.FailReadsOf("UserAchievements");

        var approved = await ApproveSetAsync(app, world.User.Id, goal, rename, tag);

        approved.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, approved.Operation.PolicyReason);
        if (!failsOnInsert)
            _faults.FailedReads.Should().BePositive();
        (await app.ReadAsync(db => db.Goals.CountAsync(saved => saved.Title == "Read 12 books"))).Should().Be(1);
        (await app.ReadAsync(db => db.Habits.SingleAsync(saved => saved.Id == world.Read.Id))).Title.Should().Be("Study");
        (await app.ReadAsync(db => db.Tags.CountAsync(saved => saved.Name == "Focus"))).Should().Be(1);
        (await app.ReadAsync(db => db.XpAwardLogs.CountAsync(log => log.UserId == world.User.Id))).Should().Be(0);
        var payload = PublishedPayload(app, world.User.Id);
        payload.Origin.Should().Be(PostgresOrbitApp.Origin);
        payload.Changes.Should().Contain(change => change.Kind == "goal" && change.Op == "create");
        payload.Changes.Should().Contain(change => change.Kind == "habit" && change.Op == "update" && change.Ids.Contains(world.Read.Id));
        payload.Changes.Should().Contain(change => change.Kind == "tag" && change.Op == "create");
        (await CacheVersionAsync(app, world.User.Id)).Should().BeGreaterThan(version);
    }

    [PostgresFact]
    public async Task SetWithAPushRegistrationThatMeetsAUniqueViolation_SucceedsAndSavesTheOtherMembers()
    {
        await using var app = await StartAsync();
        var world = await SeedWorldAsync(app);
        var earlier = await SeedPushSubscriptionAsync(app, world.User.Id, "an-earlier-token");
        var subscribe = await HoldAsync(app, world.User.Id, "update_notifications",
            new { action = "subscribe_push", endpoint = Endpoint, p256dh = PushSubscription.FcmSentinel, auth = PushSubscription.FcmSentinel });
        var rename = await HoldAsync(app, world.User.Id, "update_habit", new { habit_id = world.Read.Id, title = "Study" });
        ArmConcurrentRegistration(app, earlier);

        var approved = await ApproveSetAsync(app, world.User.Id, subscribe, rename);

        _faults.Fired.Should().BeTrue();
        approved.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, approved.Operation.PolicyReason);
        (await app.ReadAsync(db => db.PushSubscriptions.CountAsync(saved => saved.Endpoint == Endpoint))).Should().Be(1,
            "only the registration that the other connection made stays");
        (await app.ReadAsync(db => db.Habits.SingleAsync(saved => saved.Id == world.Read.Id))).Title.Should().Be("Study");
    }

    [PostgresFact]
    public async Task SinglePushRegistrationThatMeetsAUniqueViolation_Succeeds()
    {
        await using var app = await StartAsync();
        var user = await SeedUserAsync(app);
        var earlier = await SeedPushSubscriptionAsync(app, user.Id, "an-earlier-token");
        var subscribe = await HoldAsync(app, user.Id, "update_notifications",
            new { action = "subscribe_push", endpoint = Endpoint, p256dh = PushSubscription.FcmSentinel, auth = PushSubscription.FcmSentinel });
        ArmConcurrentRegistration(app, earlier);

        var approved = await ApproveAsync(app, user.Id, subscribe.Id);

        _faults.Fired.Should().BeTrue();
        approved.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, approved.Operation.PolicyReason);
        (await app.ReadAsync(db => db.PushSubscriptions.CountAsync(saved => saved.Endpoint == Endpoint))).Should().Be(1);
    }

    [PostgresTheory]
    [InlineData("update_goal_status")]
    [InlineData("update_goal_progress")]
    public async Task SetWithAGoalThatCompletes_SavesTheAwardAndSendsItsPushAfterTheCommit(string kind)
    {
        await using var app = await StartAsync();
        var account = await SeedAccountAsync(app);
        var world = account.World;
        var completion = await HoldAsync(app, world.User.Id, kind, account.CompletionArgumentsFor(kind));
        var rename = await HoldAsync(app, world.User.Id, "update_habit", new { habit_id = world.Read.Id, title = "Study" });
        var version = await CacheVersionAsync(app, world.User.Id);

        var approved = await ApproveSetAsync(app, world.User.Id, completion, rename);

        approved.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, approved.Operation.PolicyReason);
        await AssertGoalRewardedAsync(app, account.GoalId);
        app.Jobs.Created.Should().Contain(job => job.Type == typeof(SendGamificationPushJob));
        (await CacheVersionAsync(app, world.User.Id)).Should().BeGreaterThan(version);
    }

    [PostgresTheory]
    [InlineData("update_goal_status")]
    [InlineData("update_goal_progress")]
    public async Task RolledBackSetWithAGoalThatCompletes_SavesNoAwardSendsNoPushAndKeepsTheCacheVersion(string kind)
    {
        await using var app = await StartAsync();
        var account = await SeedAccountAsync(app);
        var world = account.World;
        var completion = await HoldAsync(app, world.User.Id, kind, account.CompletionArgumentsFor(kind));
        var failing = await HoldFailingUpdateAsync(app, world);
        var version = await CacheVersionAsync(app, world.User.Id);

        var approved = await ApproveSetAsync(app, world.User.Id, completion, failing);

        approved.Operation.Status.Should().Be(AgentOperationStatus.Failed);
        _failingUpdate.Failures.Should().Be(1);
        (await app.ReadAsync(db => db.Goals.SingleAsync(goal => goal.Id == account.GoalId))).Status.Should().Be(GoalStatus.Active);
        (await app.ReadAsync(db => db.XpAwardLogs.CountAsync(log => log.SourceId == account.GoalId))).Should().Be(0);
        (await app.ReadAsync(db => db.GoalProgressLogs.CountAsync(log => log.GoalId == account.GoalId))).Should().Be(0);
        app.Jobs.Created.Should().NotContain(job => job.Type == typeof(SendGamificationPushJob));
        (await CacheVersionAsync(app, world.User.Id)).Should().Be(version);
    }

    [PostgresFact]
    public async Task CommittedSetWithATimeZoneChange_GivesTheNewTimeZoneOnTheNextRead()
    {
        await using var app = await StartAsync();
        var world = await SeedWorldAsync(app);
        (await TimeZoneAsync(app, world.User.Id)).Should().NotBe("Asia/Tokyo");
        var zone = await HoldAsync(app, world.User.Id, "update_profile_preferences", new { action = "set_timezone", timezone = "Asia/Tokyo" });
        var rename = await HoldAsync(app, world.User.Id, "update_habit", new { habit_id = world.Read.Id, title = "Study" });
        string? readInsideTheSet = null;
        _failingUpdate.RunAfterTheNextWrite(async () => readInsideTheSet = await TimeZoneAsync(app, world.User.Id));

        var approved = await ApproveSetAsync(app, world.User.Id, zone, rename);

        approved.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, approved.Operation.PolicyReason);
        readInsideTheSet.Should().NotBeNull().And.NotBe("Asia/Tokyo", "another request reads the committed row while the set is open");
        (await TimeZoneAsync(app, world.User.Id)).Should().Be("Asia/Tokyo",
            "the cache entry leaves after the commit, so a read during the set cannot keep the old value");
    }

    [PostgresFact]
    public async Task RolledBackSetWithATimeZoneChange_KeepsTheDatePreferenceCacheEntry()
    {
        await using var app = await StartAsync();
        var world = await SeedWorldAsync(app);
        await TimeZoneAsync(app, world.User.Id);
        var zone = await HoldAsync(app, world.User.Id, "update_profile_preferences", new { action = "set_timezone", timezone = "Asia/Tokyo" });
        var failing = await HoldFailingUpdateAsync(app, world);
        (await DatePreferenceEntryAsync(app, world.User.Id)).Should().NotBeNull();

        var approved = await ApproveSetAsync(app, world.User.Id, zone, failing);

        approved.Operation.Status.Should().Be(AgentOperationStatus.Failed);
        _failingUpdate.Failures.Should().Be(1);
        (await DatePreferenceEntryAsync(app, world.User.Id)).Should().NotBeNull("a rolled-back set removes nothing");
        (await app.ReadAsync(db => db.Users.SingleAsync(saved => saved.Id == world.User.Id))).TimeZone.Should().NotBe("Asia/Tokyo");
    }

    [PostgresFact]
    public async Task SingleDeleteSelectedWhoseSecondIdFails_SavesNoDeletion()
    {
        await using var app = await StartAsync();
        var account = await SeedAccountAsync(app);
        var held = await HoldAsync(app, account.World.User.Id, "delete_notifications",
            new { action = "delete_selected", notification_ids = account.NotificationIds });
        _failingDeletion.FailNotificationId = account.NotificationIds[1];

        var approved = await ApproveAsync(app, account.World.User.Id, held.Id);

        approved.Operation.Status.Should().Be(AgentOperationStatus.Failed);
        _failingDeletion.Failures.Should().Be(1);
        (await app.ReadAsync(db => db.Notifications.IgnoreQueryFilters()
            .CountAsync(saved => account.NotificationIds.Contains(saved.Id) && !saved.IsDeleted))).Should().Be(2);
    }

    [PostgresFact]
    public async Task SingleTestPush_SendsWithNoOpenTransactionAndReportsTheSend()
    {
        await using var app = await StartAsync();
        var user = await SeedUserAsync(app);
        await SeedPushSubscriptionAsync(app, user.Id, Endpoint);
        var held = await HoldAsync(app, user.Id, "update_notifications", new { action = "test_push" });

        var approved = await ApproveAsync(app, user.Id, held.Id);

        approved.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, approved.Operation.PolicyReason);
        var payload = JsonSerializer.SerializeToElement(approved.Operation.Payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        payload.GetProperty("status").GetString().Should().Be("sent");
        payload.GetProperty("subscriptionCount").GetInt32().Should().Be(1);
        _pushTransactions.Should().ContainSingle().Which.Should().BeFalse("a push inside an open transaction holds its connection for the whole send");
    }

    [PostgresFact]
    public async Task SetBuiltWithATestPushMember_IsRefusedBeforeAnyMemberRuns()
    {
        await using var app = await StartAsync();
        var world = await SeedWorldAsync(app);
        await SeedPushSubscriptionAsync(app, world.User.Id, Endpoint);
        var push = await HoldAsync(app, world.User.Id, "update_notifications", new { action = "test_push" });
        var rename = await HoldAsync(app, world.User.Id, "update_habit", new { habit_id = world.Read.Id, title = "Study" });
        Guid setId;
        await using (var scope = app.CreateScope())
        {
            var services = scope.ServiceProvider;
            var store = services.GetRequiredService<IPendingAgentOperationStore>();
            var arguments = AccountChangeSet.BuildArguments(
                [store.GetExecution(world.User.Id, push.Id)!, store.GetExecution(world.User.Id, rename.Id)!]);
            var preview = (await services.GetRequiredService<IPendingOperationChangePreviewer>()
                .PreviewAsync(world.User.Id, AccountChangeSet.OperationName, arguments)).Value!;
            var fingerprint = AgentOperationFingerprint.Compute(AccountChangeSet.OperationName, arguments.GetRawText());
            setId = store.Create(world.User.Id,
                services.GetRequiredService<IAgentCatalogService>().GetCapability(AccountChangeSet.CapabilityId)!,
                AccountChangeSet.OperationName, arguments.GetRawText(), "Review", fingerprint, AgentExecutionSurface.Chat).Id;
            store.Revise(world.User.Id, setId, fingerprint, arguments.GetRawText(), fingerprint, preview.PreviewFingerprint!)
                .Should().BeTrue();
        }

        var approved = await ApproveAsync(app, world.User.Id, setId);

        approved.Operation.Status.Should().Be(AgentOperationStatus.Denied);
        approved.Operation.PolicyReason.Should().Be(Orbit.Application.Common.AgentPolicyReasons.UnsupportedByPolicy);
        _pushTransactions.Should().BeEmpty("a test push is not part of a change set");
        (await app.ReadAsync(db => db.Habits.SingleAsync(saved => saved.Id == world.Read.Id))).Title.Should().Be("Read");
    }

    private void ArmConcurrentRegistration(PostgresOrbitApp app, Guid earlierSubscriptionId) => _faults.Arm(
        () => app.ExecuteSqlAsync($"UPDATE \"PushSubscriptions\" SET \"Endpoint\" = '{Endpoint}' WHERE \"Id\" = '{earlierSubscriptionId}'"),
        "PushSubscriptions");

    private static async Task<Guid> SeedPushSubscriptionAsync(PostgresOrbitApp app, Guid userId, string endpoint)
    {
        await using var scope = app.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var subscription = PushSubscription.Create(userId, endpoint, PushSubscription.FcmSentinel, PushSubscription.FcmSentinel).Value;
        db.PushSubscriptions.Add(subscription);
        await db.SaveChangesAsync();
        return subscription.Id;
    }

    private static async Task<string?> TimeZoneAsync(PostgresOrbitApp app, Guid userId)
    {
        await using var scope = app.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IUserDateService>().GetUserTimeZoneAsync(userId)).Id;
    }

    private static async Task<string?> DatePreferenceEntryAsync(PostgresOrbitApp app, Guid userId)
    {
        await using var scope = app.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IDistributedCache>().GetStringAsync($"user-tz:{userId}");
    }

    private static async Task<Account> SeedAccountAsync(PostgresOrbitApp app)
    {
        var world = await SeedWorldAsync(app);
        await using var scope = app.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var goal = Goal.Create(new Goal.CreateGoalParams(world.User.Id, "Run far", 10, "km")).Value;
        var template = ChecklistTemplate.Create(world.User.Id, "Morning", ["Water"]).Value;
        var notifications = new[] { Notification.Create(world.User.Id, "First", "Body"), Notification.Create(world.User.Id, "Second", "Body") };
        db.Goals.Add(goal);
        db.ChecklistTemplates.Add(template);
        db.Notifications.AddRange(notifications);
        await db.SaveChangesAsync();
        return new Account(world, goal.Id, template.Id, [.. notifications.Select(notification => notification.Id)]);
    }

    private sealed record Account(World World, Guid GoalId, Guid TemplateId, Guid[] NotificationIds)
    {
        public object ArgumentsFor(string kind) => kind switch
        {
            "create_goal" => new { title = "Read 12 books", target_value = 12, unit = "books" },
            "update_goal" => new { goal_id = GoalId, title = "Run very far" },
            "delete_goal" => new { goal_id = GoalId },
            "update_goal_status" => new { goal_id = GoalId, status = "Abandoned" },
            "update_goal_progress" => new { goal_id = GoalId, current_value = 3 },
            "link_habits_to_goal" => new { goal_id = GoalId, habit_ids = new[] { World.Run.Id } },
            "reorder_goals" => new { positions = new[] { new { goal_id = GoalId, position = 4 } } },
            "create_tag" => new { name = "Focus", color = "#112233" },
            "update_tag" => new { tag_id = World.TagId, name = "Body", color = "#112233" },
            "delete_tag" => new { tag_id = World.TagId },
            "create_checklist_template" => new { name = "Packing", items = new[] { "Shoes" } },
            "update_checklist_template" => new { template_id = TemplateId, name = "Dawn" },
            "delete_checklist_template" => new { template_id = TemplateId },
            "update_profile_preferences" => new { action = "set_language", language = "pt-BR" },
            "set_ai_summary" => new { enabled = false },
            "set_proactive_astra" => new { enabled = false },
            "update_notifications" => new { action = "mark_read", notification_id = NotificationIds[0] },
            "delete_notifications" => new { action = "delete_selected", notification_ids = NotificationIds },
            "get_referral_code" => new { },
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No arguments for this kind.")
        };

        public object CompletionArgumentsFor(string kind) => kind == "update_goal_status"
            ? new { goal_id = GoalId, status = "Completed" }
            : new { goal_id = GoalId, current_value = 10 };
    }

    /// <summary>Fails the deletion of one notification with a typed failure, as a refused item of a batch does.</summary>
    private sealed class FailOneDeletionBehavior : IPipelineBehavior<DeleteNotificationCommand, Result>
    {
        public Guid? FailNotificationId { get; set; }
        public int Failures { get; private set; }

        public Task<Result> Handle(DeleteNotificationCommand request, RequestHandlerDelegate<Result> next,
            CancellationToken cancellationToken)
        {
            if (request.NotificationId != FailNotificationId) return next(cancellationToken);
            Failures++;
            return Task.FromResult(Result.Failure("The second deletion is refused"));
        }
    }

    /// <summary>Records, for each push of a person, whether the unit of work of the request holds an open transaction.</summary>
    private sealed class TransactionRecordingPush(IUnitOfWork unitOfWork, List<bool> openTransactions) : IPushNotificationService
    {
        public Task SendToUserAsync(Guid userId, string title, string body, string? url = null, CancellationToken cancellationToken = default)
        {
            openTransactions.Add(unitOfWork.HasActiveTransaction);
            return Task.CompletedTask;
        }

        public Task<IReadOnlySet<int>> TrySendManyAsync(IReadOnlyList<PushDelivery> deliveries, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<int>>(new HashSet<int>());

        public Task<IReadOnlySet<int>> TrySendManyAsync(IReadOnlyList<PushDelivery> deliveries, Func<int, Task> onDelivered,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlySet<int>>(new HashSet<int>());

        public Task SendManyAsync(IReadOnlyList<PushDelivery> deliveries, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> TrySendToUserAsync(Guid userId, string title, string body, string? url = null,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> TrySendReminderToUserAsync(Guid userId, string title, string body, IReadOnlyDictionary<string, string> data,
            string? url = null, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
