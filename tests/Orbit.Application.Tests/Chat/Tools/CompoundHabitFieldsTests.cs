using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;

namespace Orbit.Application.Tests.Chat.Tools;

public class CompoundHabitFieldsTests
{
    [Fact]
    public async Task ExistingParent_PreservesRemindersSlipAlertChecklistAndTimes()
    {
        var mediator = Substitute.For<IMediator>();
        CreateSubHabitCommand? sent = null;
        mediator.Send(Arg.Do<CreateSubHabitCommand>(command => sent = command), Arg.Any<CancellationToken>())
            .Returns(Result.Success(Guid.NewGuid()));
        var args = JsonSerializer.SerializeToElement(new
        {
            parent_habit_id = Guid.NewGuid(),
            habits = new[] { new {
                title = "Take magnesium", frequency_unit = "Day", due_time = "22:00", due_end_time = "22:30",
                end_date = "2026-11-30", reminder_enabled = true, reminder_times = new[] { 10 },
                slip_alert_enabled = true, checklist_items = new[] { new { text = "With water" } }
            } }
        });
        (await new BulkCreateHabitsTool(mediator).ExecuteAsync(args, Guid.NewGuid(), default)).Success.Should().BeTrue();
        sent.Should().NotBeNull();
        sent!.Options!.ReminderEnabled.Should().BeTrue();
        sent.Options.ReminderTimes.Should().Equal(10);
        sent.Options.SlipAlertEnabled.Should().BeTrue();
        sent.Options.ChecklistItems.Should().ContainSingle(item => item.Text == "With water");
        sent.Options.DueTime.Should().Be(new TimeOnly(22, 0));
        sent.Options.EndDate.Should().Be(new DateOnly(2026, 11, 30));
    }

    [Theory]
    [InlineData("reminder_enabled")]
    [InlineData("reminder_times")]
    [InlineData("scheduled_reminders")]
    [InlineData("relative_reminders")]
    [InlineData("slip_alert_enabled")]
    [InlineData("tag_names")]
    [InlineData("goal_ids")]
    [InlineData("start_date")]
    public void BatchCreate_ExposesFormFieldsOnEveryNode(string field)
    {
        var schema = JsonSerializer.SerializeToElement(new BulkCreateHabitsTool(Substitute.For<IMediator>()).GetParameterSchema());
        var root = schema.GetProperty("properties").GetProperty("habits").GetProperty("items").GetProperty("properties");
        root.TryGetProperty(field, out _).Should().BeTrue($"the form field {field} needs an Astra creation path");
        var child = root.GetProperty("sub_habits").GetProperty("items").GetProperty("properties");
        child.TryGetProperty(field, out _).Should().BeTrue($"children must support {field} too");
    }
}
