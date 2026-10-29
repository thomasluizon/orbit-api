using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Validators;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Services;

public sealed partial class BulkCreateAgentOperationTests
{
    [Theory]
    [InlineData("existing", false, null)]
    [InlineData("existing", true, null)]
    [InlineData("item_parent", false, null)]
    [InlineData("item_parent", true, null)]
    [InlineData("new_bulk", false, null)]
    [InlineData("new_bulk", true, null)]
    [InlineData("new_tree", false, null)]
    [InlineData("new_tree", true, null)]
    [InlineData("existing", true, "empty")]
    [InlineData("existing", true, "Friday")]
    [InlineData("new_tree", true, "empty")]
    [InlineData("new_tree", true, "Friday")]
    public async Task BrainDump_DisplayDatePreservesInheritedWeekdaysThroughApproval(string parentMode, bool revise, string? days)
    {
        var start = new DateOnly(2026, 10, 26);
        var existingParent = parentMode is "existing" or "item_parent";
        Habit? parent = null;
        if (existingParent)
        {
            parent = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, _userId,
                "Wednesday reading", FrequencyUnit.Day, 1, start, Days: [DayOfWeek.Wednesday])).Value;
            _factory.Context.Habits.Add(parent);
            await _factory.Context.SaveChangesAsync();
        }

        var child = new JsonObject
        {
            ["title"] = "Read",
            ["frequency_unit"] = "Day",
            ["frequency_quantity"] = 1,
            ["start_date"] = "2026-10-26"
        };
        if (days is not null) child["days"] = days == "empty" ? new JsonArray() : new JsonArray(days);
        var root = new JsonObject();
        if (existingParent)
        {
            (parentMode == "item_parent" ? child : root)["parent_habit_id"] = parent!.Id.ToString();
            root["habits"] = new JsonArray(child);
        }
        else
        {
            var proposedParent = new JsonObject
            {
                ["title"] = "Wednesday reading",
                ["frequency_unit"] = "Day",
                ["frequency_quantity"] = 1,
                ["days"] = new JsonArray("Wednesday"),
                ["sub_habits"] = new JsonArray(child)
            };
            if (parentMode == "new_tree") proposedParent["tag_ids"] = new JsonArray();
            root["habits"] = new JsonArray(proposedParent);
        }

        var request = new AgentExecuteOperationRequest(_userId, "bulk_create_habits",
            JsonSerializer.SerializeToElement(root), AgentExecutionSurface.Chat, AgentAuthMethod.Jwt,
            IncludeChangePreview: true);
        var staged = await _executor.ExecuteAsync(request);

        staged.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation);
        var pending = staged.PendingOperation!;
        var item = pending.Items.Should().ContainSingle().Subject;
        if (existingParent)
            item.Fields.Single(field => field.Field == "due_date").NewValue.Should().Be("2026-10-26");
        else
            item.Fields.Single(field => field.Field == "sub_habits").ProposedValue!.Value[0]
                .GetProperty("due_date").GetString().Should().Be("2026-10-26");

        var revisions = new PendingOperationRevisionService(_pending, _previewer,
            new RevisePendingOperationRequestValidator(), _registry);
        if (revise)
        {
            var edits = existingParent
                ? JsonSerializer.SerializeToElement(new { start_date = "2026-10-27" })
                : JsonSerializer.SerializeToElement(new { title = "Wednesday reading plan" });
            var revised = await revisions.ReviseAsync(_userId, pending.Id,
                new(pending.PreviewFingerprint!, [new(item.ItemId, edits)]), default);
            revised.IsSuccess.Should().BeTrue(revised.Error);
            if (existingParent)
                revised.Preview!.Items!.Single().Fields.Single(field => field.Field == "due_date")
                    .NewValue.Should().Be("2026-10-27");
        }

        var refreshed = await revisions.RefreshAsync(_userId, pending.Id, default);
        refreshed.IsSuccess.Should().BeTrue(refreshed.Error);
        using (var beforeApproval = _factory.CreateContext())
            beforeApproval.Habits.Count().Should().Be(existingParent ? 1 : 0);
        var execution = _pending.GetExecution(_userId, pending.Id)!;
        var offeredChild = execution.Arguments.GetProperty("habits")[0];
        if (!existingParent) offeredChild = offeredChild.GetProperty("sub_habits")[0];
        offeredChild.TryGetProperty("days", out _).Should().Be(days is not null);
        offeredChild.GetProperty("due_date").GetString().Should().Be(existingParent && revise ? "2026-10-27" : "2026-10-26");
        (await revisions.CanApproveAsync(_userId, execution, default)).Should().BeTrue();
        var confirmation = _pending.Confirm(_userId, pending.Id)!;

        var approved = await _executor.ExecuteAsync(request with
        { Arguments = execution.Arguments, ConfirmationToken = confirmation.ConfirmationToken });

        approved.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, _lastAudit?.Error);
        using var saved = _factory.CreateContext();
        var savedParent = saved.Habits.Single(habit => habit.ParentHabitId == null);
        savedParent.Days.Should().Equal(DayOfWeek.Wednesday);
        var savedChild = saved.Habits.Single(habit => habit.ParentHabitId == savedParent.Id);
        savedChild.Title.Should().Be("Read");
        savedChild.StartDate.Should().Be(existingParent && revise ? start.AddDays(1) : start);
        DayOfWeek[] expectedDays = days == "empty" ? [] : [days == "Friday" ? DayOfWeek.Friday : DayOfWeek.Wednesday];
        savedChild.Days.Should().Equal(expectedDays);
        HabitScheduleService.IsHabitDueOnDate(savedChild, start.AddDays(2)).Should().Be(days != "Friday");
        HabitScheduleService.IsHabitDueOnDate(savedChild, start.AddDays(3)).Should().Be(days == "empty");
        HabitScheduleService.IsHabitDueOnDate(savedChild, start.AddDays(4)).Should().Be(days is not null);
        _pending.GetAwaiting(_userId).Should().BeEmpty();
    }
}
