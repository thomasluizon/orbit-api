using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Chat.Tools;

public sealed class RepeatReminderToolTests
{
    [Fact]
    public void UpdateArguments_AcceptRepeatAndClearItExplicitly()
    {
        var parsed = BulkHabitToolArguments.ParseUpdateFields(JsonSerializer.SerializeToElement(new
        {
            repeat_reminder = new { interval_minutes = 480, active_start = "22:00", active_end = "06:00" }
        }));
        parsed.Error.Should().BeNull();
        parsed.Changes!.RepeatReminder!.IntervalMinutes.Should().Be(480);
        parsed.Changes.RepeatReminder.ActiveEnd.Should().Be(new TimeOnly(6, 0));
        parsed.Changes.ReminderEnabled.Should().BeTrue();
        var clear = BulkHabitToolArguments.ParseUpdateFields(JsonSerializer.SerializeToElement(new { clear_fields = new[] { "repeat_reminder" } }));
        clear.Error.Should().BeNull();
        clear.Changes!.HasRepeatReminder.Should().BeTrue();
        clear.Changes.RepeatReminder.Should().BeNull();
    }

    [Fact]
    public async Task CreateHabit_ForwardsRepeatFromModelArgumentsToTheAppCommand()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<CreateHabitCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success(Guid.NewGuid()));
        var tool = new CreateHabitTool(Substitute.For<IUserDateService>(), mediator);
        var args = JsonSerializer.SerializeToElement(new
        {
            title = "Drink water",
            frequency_unit = "Day",
            frequency_quantity = 1,
            repeat_reminder = new { interval_minutes = 30 }
        });
        (await tool.ExecuteAsync(args, Guid.NewGuid(), default)).Success.Should().BeTrue();
        await mediator.Received(1).Send(Arg.Is<CreateHabitCommand>(c => c.Options != null
            && c.Options.ReminderEnabled && c.Options.RepeatReminder != null
            && c.Options.RepeatReminder.IntervalMinutes == 30), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GeneralRepeat_DoesNotAskForAnUnneededHabitFrequency()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<CreateHabitCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success(Guid.NewGuid()));
        var tool = new CreateHabitTool(Substitute.For<IUserDateService>(), mediator);
        var args = JsonSerializer.SerializeToElement(new
        {
            title = "Water habit",
            is_general = true,
            repeat_reminder = new { interval_minutes = 30 }
        });
        tool.NeedsClarification(args).Should().BeFalse();
        (await tool.ExecuteAsync(args, Guid.NewGuid(), default)).Success.Should().BeTrue();
        await mediator.Received(1).Send(Arg.Is<CreateHabitCommand>(c => c.IsGeneral && c.Options != null
            && c.Options.RepeatReminder != null && c.Options.RepeatReminder.IntervalMinutes == 30), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BulkCreate_ForwardsTheRepeatSettingsForEveryHabit()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<BulkCreateHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new BulkCreateResult([])));
        var args = JsonSerializer.SerializeToElement(new
        {
            habits = new[]
            {
                new { title = "Drink water", is_general = true, repeat_reminder = new { interval_minutes = 30 } },
                new { title = "Take medicine", is_general = true, repeat_reminder = new { interval_minutes = 480 } }
            }
        });
        (await new BulkCreateHabitsTool(mediator).ExecuteAsync(args, Guid.NewGuid(), default)).Success.Should().BeTrue();
        await mediator.Received(1).Send(Arg.Is<BulkCreateHabitsCommand>(c => c.Habits.Count == 2
            && c.Habits.All(h => h.ReminderEnabled && h.RepeatReminder != null)), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(14)]
    [InlineData(90)]
    [InlineData(1440)]
    public void UnsupportedIntervals_AreRejectedBeforeExecution(int minutes)
    {
        var parsed = BulkHabitToolArguments.ParseUpdateFields(JsonSerializer.SerializeToElement(new
        {
            repeat_reminder = new { interval_minutes = minutes }
        }));
        parsed.Error.Should().NotBeNull();
    }
}
