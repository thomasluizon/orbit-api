using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Chat;

public sealed class HeldWriteArgumentCheckTests
{
    [Theory]
    [InlineData("delete_habit", """{"habit_id":"$habit"}""")]
    [InlineData("duplicate_habit", """{"habit_id":"$habit"}""")]
    [InlineData("move_habit", """{"habit_id":"$habit","new_parent_id":null}""")]
    [InlineData("move_habit_parent", """{"habit_id":"$habit","parent_id":null}""")]
    [InlineData("delete_goal", """{"goal_id":"$goal"}""")]
    [InlineData("delete_tag", """{"tag_id":"$tag"}""")]
    [InlineData("bulk_delete_habits", """{"habit_ids":["$habit"]}""")]
    [InlineData("link_goals_to_habit", """{"habit_id":"$habit","goal_ids":["$goal"]}""")]
    [InlineData("link_habits_to_goal", """{"goal_id":"$goal","habit_ids":["$habit"]}""")]
    public async Task FixedTargets_CheckOwnershipWithoutWriting(string name, string arguments)
    {
        using var context = new HeldWriteTestContext();
        var check = context.Tool(name).Should().BeAssignableTo<IArgumentCheckTool>().Subject;
        var valid = JsonSerializer.Deserialize<JsonElement>(context.Expand(arguments));
        var invalid = JsonSerializer.Deserialize<JsonElement>(arguments.Replace("$habit", "$foreign").Replace("$goal", "$foreign").Replace("$tag", "$foreign"));
        invalid = JsonSerializer.Deserialize<JsonElement>(context.Expand(invalid.GetRawText()));

        (await check.CheckArgumentsAsync(valid, context.UserId, CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await check.CheckArgumentsAsync(invalid, context.UserId, CancellationToken.None)).IsFailure.Should().BeTrue();

        context.Commands.Should().BeEmpty();
        context.Added.Should().BeEmpty();
        context.Habit.IsDeleted.Should().BeFalse();
        context.Goal.IsDeleted.Should().BeFalse();
        context.Tag.IsDeleted.Should().BeFalse();
        await context.UnitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("delete_checklist_template")]
    [InlineData("delete_user_facts")]
    [InlineData("delete_notifications")]
    [InlineData("update_notifications")]
    public async Task FixedRecordActions_CheckOwnershipWithoutWriting(string name)
    {
        using var context = new HeldWriteTestContext();
        var foreignUser = Guid.NewGuid();
        var own = CreateRecord(name, context.UserId);
        var foreign = CreateRecord(name, foreignUser);
        context.Records.AddRange([own, foreign]);
        var check = (IArgumentCheckTool)context.Tool(name);

        (await check.CheckArgumentsAsync(Arguments(name, own.Id), context.UserId, CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await check.CheckArgumentsAsync(Arguments(name, foreign.Id), context.UserId, CancellationToken.None)).IsFailure.Should().BeTrue();

        context.Commands.Should().BeEmpty();
        context.Added.Should().BeEmpty();
        await context.UnitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReferralCodeCheck_DoesNotGenerateCode()
    {
        using var context = new HeldWriteTestContext();
        var check = (IArgumentCheckTool)context.Tool("get_referral_code");
        var args = JsonSerializer.Deserialize<JsonElement>("{}");
        (await check.CheckArgumentsAsync(args, context.UserId, CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await check.CheckArgumentsAsync(args, Guid.Empty, CancellationToken.None)).IsFailure.Should().BeTrue();
        context.Commands.Should().BeEmpty();
    }

    [Theory]
    [InlineData("move_habit", "new_parent_id")]
    [InlineData("move_habit_parent", "parent_id")]
    public async Task MoveChecks_RejectCyclesAndForeignParents(string name, string parentField)
    {
        using var context = new HeldWriteTestContext();
        var check = (IArgumentCheckTool)context.Tool(name);
        foreach (var parentId in new[] { context.Habit.Id, context.ForeignHabit.Id })
        {
            var args = JsonSerializer.SerializeToElement(new Dictionary<string, object>
            {
                ["habit_id"] = context.Habit.Id,
                [parentField] = parentId
            });
            (await check.CheckArgumentsAsync(args, context.UserId, CancellationToken.None)).IsFailure.Should().BeTrue();
        }
        context.Habit.ParentHabitId.Should().BeNull();
    }

    [Theory]
    [InlineData("update_goal_progress", """{"goal_id":"$goal","current_value":1}""")]
    [InlineData("update_goal_status", """{"goal_id":"$goal","status":"Completed"}""")]
    public async Task GoalChecks_ApplyDomainGuardsWithoutMutating(string name, string arguments)
    {
        using var context = new HeldWriteTestContext();
        if (name == "update_goal_progress")
            context.Goal.AddHabit(context.Habit);
        else
            context.Goal.MarkCompleted();
        var before = (context.Goal.CurrentValue, context.Goal.Status, context.Goal.UpdatedAtUtc, context.Goal.Habits.Count);
        var check = (IArgumentCheckTool)context.Tool(name);
        (await check.CheckArgumentsAsync(JsonSerializer.Deserialize<JsonElement>(context.Expand(arguments)),
            context.UserId, CancellationToken.None)).IsFailure.Should().BeTrue();
        (context.Goal.CurrentValue, context.Goal.Status, context.Goal.UpdatedAtUtc, context.Goal.Habits.Count).Should().Be(before);
    }

    [Theory]
    [InlineData("log_habit")]
    [InlineData("skip_habit")]
    [InlineData("bulk_log_habits")]
    [InlineData("bulk_skip_habits")]
    public async Task HabitChecks_RejectUnschedulableDateWithoutMutating(string name)
    {
        using var context = new HeldWriteTestContext();
        var check = (IArgumentCheckTool)context.Tool(name);
        var args = name.StartsWith("bulk_", StringComparison.Ordinal)
            ? JsonSerializer.Deserialize<JsonElement>(context.Expand("""{"habit_ids":["$habit"],"date":"2026-10-01"}"""))
            : JsonSerializer.Deserialize<JsonElement>(context.Expand("""{"habit_id":"$habit","date":"2026-10-01"}"""));
        var before = JsonSerializer.Serialize(context.Habit);
        (await check.CheckArgumentsAsync(args, context.UserId, CancellationToken.None)).IsFailure.Should().BeTrue();
        JsonSerializer.Serialize(context.Habit).Should().Be(before);
        context.Commands.Should().BeEmpty();
    }

    private static Orbit.Domain.Common.Entity CreateRecord(string name, Guid userId) => name switch
    {
        "delete_checklist_template" => ChecklistTemplate.Create(userId, "Read", ["Read"]).Value,
        "delete_user_facts" => UserFact.Create(userId, "Enjoys reading", null).Value,
        _ => Notification.Create(userId, "Read", "Read")
    };

    private static JsonElement Arguments(string name, Guid id) => name switch
    {
        "delete_checklist_template" => JsonSerializer.SerializeToElement(new { template_id = id }),
        "delete_user_facts" => JsonSerializer.SerializeToElement(new { fact_id = id }),
        "delete_notifications" => JsonSerializer.SerializeToElement(new { action = "delete_one", notification_id = id }),
        _ => JsonSerializer.SerializeToElement(new { action = "mark_read", notification_id = id })
    };
}
