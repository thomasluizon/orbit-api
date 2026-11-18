using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orbit.Api.Extensions;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class WriteKindAtomicityTests
{
    private static readonly Guid HabitId = Guid.Parse("7a8f1c1e-0d6b-4a53-9a51-5f0b2c1d0e01");
    private static readonly Guid ParentId = Guid.Parse("7a8f1c1e-0d6b-4a53-9a51-5f0b2c1d0e02");
    private static readonly Guid GoalId = Guid.Parse("7a8f1c1e-0d6b-4a53-9a51-5f0b2c1d0e03");
    private static readonly Guid TagId = Guid.Parse("7a8f1c1e-0d6b-4a53-9a51-5f0b2c1d0e04");

    private static readonly string[] ExternalKinds =
        ["manage_subscription", "send_support_request", "manage_calendar_sync", "manage_api_keys", "manage_account"];

    [Fact]
    public void RegisteredWriteKinds_AreEachAtomicOrExternal()
    {
        using var registered = RegisteredTools.Create();
        var writeKinds = registered.Tools.Where(tool => !tool.IsReadOnly).ToList();

        writeKinds.Should().HaveCount(47);
        foreach (var tool in writeKinds)
        {
            var labels = new List<string>();
            if (tool is ITransactionalAiTool) labels.Add("atomic");
            if (tool.GetType().IsDefined(typeof(ExternalEffectToolAttribute), inherit: false)) labels.Add("external");
            labels.Should().ContainSingle($"write kind {tool.Name} needs exactly one of the atomic and external labels");
        }
        writeKinds.Where(tool => tool.GetType().IsDefined(typeof(ExternalEffectToolAttribute), inherit: false))
            .Select(tool => tool.Name).Should().BeEquivalentTo(ExternalKinds);
        writeKinds.Where(tool => tool is IExternalShapeTool).Select(tool => tool.Name).Should().BeEquivalentTo(
            ["update_notifications"], "test_push is the one shape of an atomic kind that stays out of a change set");
    }

    public static TheoryData<string, string> OtherKindShapes => new()
    {
        { "create_goal", Json(new { title = "Run a marathon", target_value = 42, unit = "km" }) },
        { "create_goal", Json(new { title = "Run a marathon", goal_type = "Streak", habit_ids = new[] { HabitId } }) },
        { "update_goal", Json(new { goal_id = GoalId, title = "Run far" }) },
        { "delete_goal", Json(new { goal_id = GoalId }) },
        { "update_goal_status", Json(new { goal_id = GoalId, status = "Completed" }) },
        { "update_goal_status", Json(new { goal_id = GoalId, status = "Abandoned" }) },
        { "update_goal_progress", Json(new { goal_id = GoalId, current_value = 3 }) },
        { "update_goal_progress", Json(new { goal_name = "Run", current_value = 3, note = "Long run" }) },
        { "link_habits_to_goal", Json(new { goal_id = GoalId, habit_ids = new[] { HabitId } }) },
        { "reorder_goals", Json(new { positions = new[] { new { goal_id = GoalId, position = 0 } } }) },
        { "create_tag", Json(new { name = "Health", color = "#7c3aed" }) },
        { "update_tag", Json(new { tag_id = TagId, name = "Body", color = "#7c3aed" }) },
        { "delete_tag", Json(new { tag_id = TagId }) },
        { "create_checklist_template", Json(new { name = "Morning", items = new[] { "Water" } }) },
        { "update_checklist_template", Json(new { template_id = TagId, name = "Dawn" }) },
        { "delete_checklist_template", Json(new { template_id = TagId }) },
        { "update_profile_preferences", Json(new { action = "set_name", name = "Alex" }) },
        { "update_profile_preferences", Json(new { action = "set_timezone", timezone = "America/Sao_Paulo" }) },
        { "update_profile_preferences", Json(new { action = "set_language", language = "pt-BR" }) },
        { "update_profile_preferences", Json(new { action = "set_week_start_day", week_start_day = 0 }) },
        { "update_profile_preferences", Json(new { action = "set_clock_format", uses_24_hour_clock = true }) },
        { "update_profile_preferences", Json(new { action = "set_theme_preference", theme_preference = "dark" }) },
        { "update_profile_preferences", Json(new { action = "complete_onboarding" }) },
        { "update_profile_preferences", Json(new { action = "complete_tour" }) },
        { "update_profile_preferences", Json(new { action = "reset_tour" }) },
        { "set_ai_summary", Json(new { enabled = true }) },
        { "set_proactive_astra", Json(new { enabled = false }) },
        { "update_notifications", Json(new { action = "mark_read", notification_id = TagId }) },
        { "update_notifications", Json(new { action = "mark_all_read" }) },
        { "update_notifications", Json(new { action = "subscribe_push", endpoint = "token", p256dh = "fcm", auth = "fcm" }) },
        { "update_notifications", Json(new { action = "unsubscribe_push", endpoint = "token" }) },
        { "delete_notifications", Json(new { action = "delete_one", notification_id = TagId }) },
        { "delete_notifications", Json(new { action = "delete_all" }) },
        { "delete_notifications", Json(new { action = "delete_selected", notification_ids = new[] { TagId } }) },
        { "get_referral_code", Json(new { }) },
        { "manage_feature_flags", Json(new { action = "create", key = "flag" }) }
    };

    [Theory]
    [MemberData(nameof(OtherKindShapes))]
    public async Task OtherKinds_JoinAChangeSetForEveryArgumentShape(string kind, string arguments)
    {
        using var registered = RegisteredTools.Create();

        AiToolAtomicity.Supports(registered.Tool(kind), JsonDocument.Parse(arguments).RootElement).Should().BeTrue();
        var (combined, member, other) = await CombineWithADeleteAsync(registered, kind, arguments);

        combined.Should().NotBeNull($"{kind} with {arguments} must join a change set");
        combined!.MemberIds.Should().BeEquivalentTo([member.Id, other.Id]);
    }

    [Fact]
    public async Task TestPush_StaysOutOfAChangeSet()
    {
        using var registered = RegisteredTools.Create();
        var arguments = Json(new { action = "test_push" });

        AiToolAtomicity.Supports(registered.Tool("update_notifications"), JsonDocument.Parse(arguments).RootElement)
            .Should().BeFalse("a test push writes no row and its result reports the send");
        var (combined, _, _) = await CombineWithADeleteAsync(registered, "update_notifications", arguments);

        combined.Should().BeNull("the only other member cannot form a set alone");
    }

    public static TheoryData<string, string> HabitKindShapes => new()
    {
        { "assign_tags", Json(new { habit_id = HabitId, tag_names = new[] { "Health" } }) },
        { "bulk_update_habits", Json(new { habit_ids = new[] { HabitId }, updates = new { title = "Walk" } }) },
        { "bulk_reschedule_habits", Json(new { revised_items = new[] { new { habit_id = HabitId, due_date = "2026-04-04" } } }) },
        { "bulk_skip_habits", Json(new { habit_ids = new[] { HabitId } }) },
        { "bulk_delete_habits", Json(new { habit_ids = new[] { HabitId } }) },
        { "update_habit", Json(new { habit_id = HabitId, title = "Walk" }) },
        { "update_habit", Json(new { habit_id = HabitId, goal_ids = new[] { GoalId } }) },
        { "skip_habit", Json(new { habit_id = HabitId }) },
        { "create_sub_habit", Json(new { parent_habit_id = ParentId, title = "Stretch" }) },
        { "create_sub_habit", Json(new { title = "Stretch" }) },
        { "create_sub_habit", Json(new { parent_habit_id = ParentId, title = "Stretch", goal_ids = new[] { GoalId } }) },
        { "bulk_create_habits", Json(new { habits = new[] { new { title = "Read" } } }) },
        { "bulk_create_habits", Json(new { habits = new[] { new { title = "Read", goal_ids = new[] { GoalId } } } }) },
        { "bulk_create_habits", Json(new { habits = new object[] { new { title = "Read", parent_habit_id = ParentId }, new { title = "Write" } } }) },
        { "bulk_create_habits", Json(new { parent_habit_id = ParentId, habits = new[] { new { title = "Read", tag_ids = new[] { TagId } } } }) },
        { "bulk_create_habits", Json(new { habits = new object[] { new { title = "Read", tag_ids = new[] { TagId } } } }) },
        { "create_habit", Json(new { title = "Read", frequency_unit = "Day" }) },
        { "unlog_habit", Json(new { habit_id = HabitId }) },
        { "log_habit", Json(new { habit_id = HabitId }) },
        { "bulk_log_habits", Json(new { habit_ids = new[] { HabitId } }) },
        { "undo_skip_habit", Json(new { habit_id = HabitId, skip_id = Guid.NewGuid() }) },
        { "delete_habit", Json(new { habit_id = HabitId }) },
        { "duplicate_habit", Json(new { habit_id = HabitId }) },
        { "move_habit", Json(new { habit_id = HabitId, due_date = "2026-04-04" }) },
        { "move_habit_parent", Json(new { habit_id = HabitId, parent_habit_id = ParentId }) },
        { "reorder_habits", Json(new { positions = new[] { new { habit_id = HabitId, position = 0 } } }) },
        { "update_checklist", Json(new { habit_id = HabitId, checklist_items = new[] { new { text = "Shoes", is_checked = true } } }) },
        { "bulk_update_habit_emojis", Json(new { infer_from_title = true }) },
        { "bulk_update_habit_emojis", Json(new { infer_from_title = false, revised_items = new[] { new { habit_id = HabitId, emoji = "🏃" } } }) },
        { "link_goals_to_habit", Json(new { habit_id = HabitId, goal_ids = new[] { GoalId } }) }
    };

    [Theory]
    [MemberData(nameof(HabitKindShapes))]
    public async Task HabitKinds_JoinAChangeSetForEveryArgumentShape(string kind, string arguments)
    {
        using var registered = RegisteredTools.Create();

        var (combined, member, other) = await CombineWithADeleteAsync(registered, kind, arguments);

        combined.Should().NotBeNull($"{kind} with {arguments} must join a change set");
        combined!.MemberIds.Should().BeEquivalentTo([member.Id, other.Id]);
    }

    private static async Task<(PendingAccountChangeSet? Combined, PendingAgentOperation Member, PendingAgentOperation Other)>
        CombineWithADeleteAsync(RegisteredTools registered, string kind, string arguments)
    {
        var userId = Guid.NewGuid();
        var store = Substitute.For<IPendingAgentOperationStore>();
        var executions = new Dictionary<Guid, PendingAgentOperationExecution>();
        PendingAgentOperation Hold(string operationId, string json)
        {
            var id = Guid.NewGuid();
            executions[id] = new(id, "habits.write", operationId, JsonDocument.Parse(json).RootElement.Clone(),
                AgentExecutionSurface.Chat, AgentConfirmationRequirement.FreshConfirmation, $"preview-{id}");
            return new(id, "habits.write", operationId, operationId, AgentRiskClass.Low,
                AgentConfirmationRequirement.FreshConfirmation, DateTime.UtcNow.AddHours(1));
        }
        store.GetExecution(userId, Arg.Any<Guid>()).Returns(call => executions.GetValueOrDefault(call.ArgAt<Guid>(1)));
        store.Create(userId, Arg.Any<AgentCapability>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<AgentExecutionSurface>(), Arg.Any<PendingOperationConversation?>())
            .Returns(call => new PendingAgentOperation(Guid.NewGuid(), "account.changes", "Review", "Review",
                AgentRiskClass.Low, AgentConfirmationRequirement.FreshConfirmation, DateTime.UtcNow.AddHours(1)));
        store.Revise(userId, Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<PendingOperationChangePreview?>()).Returns(true);
        var previewer = Substitute.For<IPendingOperationChangePreviewer>();
        previewer.PreviewAsync(userId, AccountChangeSet.OperationName, Arg.Any<JsonElement>(), Arg.Any<CancellationToken>(),
                Arg.Any<AgentClientContext?>())
            .Returns(Result.Success<PendingOperationChangePreview?>(new([], 2,
                [new("item", null, "Item", [], "state")], "set-preview")));
        var service = new AccountChangeSetService(store, previewer, new AgentCatalogService(registered.Registry.GetAll()),
            registered.Registry);
        var member = Hold(kind, arguments);
        var other = Hold("bulk_delete_habits", Json(new { habit_ids = new[] { ParentId } }));

        return (await service.CombineAsync(userId, [member, other], default), member, other);
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);

    private sealed class RegisteredTools : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly IServiceScope _scope;

        private RegisteredTools(ServiceProvider provider)
        {
            _provider = provider;
            _scope = provider.CreateScope();
            Registry = _scope.ServiceProvider.GetRequiredService<AiToolRegistry>();
            Tools = [.. _scope.ServiceProvider.GetServices<IAiTool>()];
        }

        public AiToolRegistry Registry { get; }
        public IReadOnlyList<IAiTool> Tools { get; }

        public IAiTool Tool(string name) => Tools.Single(tool => tool.Name == name);

        public static RegisteredTools Create()
        {
            var builder = WebApplication.CreateBuilder();
            builder.AddOrbitAiServices();
            var dependencies = typeof(IAiTool).Assembly.GetTypes()
                .Where(type => type.IsClass && !type.IsAbstract && typeof(IAiTool).IsAssignableFrom(type))
                .Append(typeof(BulkHabitReplayPlanner))
                .SelectMany(type => type.GetConstructors())
                .SelectMany(constructor => constructor.GetParameters())
                .Select(parameter => parameter.ParameterType)
                .Where(type => type.IsInterface).Distinct();
            foreach (var type in dependencies)
                builder.Services.AddSingleton(type, Substitute.For([type], []));
            return new RegisteredTools(builder.Services.BuildServiceProvider());
        }

        public void Dispose()
        {
            _scope.Dispose();
            _provider.Dispose();
        }
    }
}
