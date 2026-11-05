using FluentAssertions;
using NSubstitute;
using Orbit.Domain.Entities;

namespace Orbit.Application.Tests.Chat.Tools;

public partial class CreateHabitToolTests
{
    [Fact]
    public async Task WithMonthDays_CreatesAHabitDueOnTheChosenDays()
    {
        var result = await Execute("""
            {"title": "Pagar contas", "frequency_unit": "Month", "frequency_quantity": 1, "month_days": [15, 8]}
            """);

        result.Success.Should().BeTrue();
        await _habitRepo.Received(1).AddAsync(
            Arg.Is<Habit>(habit => habit.MonthDays.SequenceEqual(new[] { 8, 15 }) && habit.NextDueDate == new DateOnly(2026, 4, 8)),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("""{"title": "Pagar contas", "frequency_unit": "Month", "month_days": [0]}""")]
    [InlineData("""{"title": "Pagar contas", "frequency_unit": "Month", "month_days": [8, 8]}""")]
    [InlineData("""{"title": "Pagar contas", "frequency_unit": "Month", "month_days": ["8"]}""")]
    [InlineData("""{"title": "Pagar contas", "frequency_unit": "Week", "month_days": [8]}""")]
    public async Task WithInvalidMonthDays_CreatesNothing(string json)
    {
        var result = await Execute(json);

        result.Success.Should().BeFalse();
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithInlineSubHabit_InheritsParentMonthDays()
    {
        var addedHabits = new List<Habit>();
        _habitRepo.AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                addedHabits.Add(call.Arg<Habit>());
                return Task.CompletedTask;
            });

        var result = await Execute("""
            {"title": "Contas do mês", "frequency_unit": "Month", "frequency_quantity": 1, "month_days": [8, 15],
             "sub_habits": [{"title": "Aluguel"}]}
            """);

        result.Success.Should().BeTrue();
        addedHabits.Single(habit => habit.ParentHabitId is not null).MonthDays.Should().Equal(8, 15);
    }
}
