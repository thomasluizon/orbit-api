using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Tests.Entities;

public class HabitPolarityTests
{
    [Theory]
    [InlineData("Não tomar multivitamínico")]
    [InlineData("Sem refrigerante")]
    [InlineData("Parar de fumar")]
    [InlineData("Evitar açúcar")]
    [InlineData("Don't eat sugar")]
    [InlineData("Do not smoke")]
    [InlineData("Stop smoking")]
    [InlineData("Avoid soda")]
    public void NegatedName_CannotUseAvoidType(string title)
    {
        var result = Habit.Create(new HabitCreateParams(Guid.NewGuid(), title, FrequencyUnit.Day, 1,
            new DateOnly(2026, 10, 27), IsBadHabit: true));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("BAD_HABIT_NEGATED_NAME");
    }
    [Theory]
    [InlineData("Fumar", "Não fumar")]
    [InlineData("Smoking", "Don't smoke")]
    public void Update_RejectsMixedFormWithoutMutating(string behavior, string negation)
    {
        var habit = Habit.Create(new HabitCreateParams(Guid.NewGuid(), behavior, FrequencyUnit.Day, 1,
            new DateOnly(2026, 10, 27), IsBadHabit: true)).Value;
        var result = habit.Update(new HabitUpdateParams(negation, null, FrequencyUnit.Day, 1,
            null, true, null));
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("BAD_HABIT_NEGATED_NAME");
        habit.Title.Should().Be(behavior);
    }

    [Theory]
    [InlineData("No caffeine", false)]
    [InlineData("No caffeine", true)]
    [InlineData("Avoid sugary drinks", false)]
    [InlineData("Avoid sugary drinks", true)]
    [InlineData("Não fumar", false)]
    [InlineData("Não fumar", true)]
    public void LegacyAvoidName_UnrelatedUpdatePreservesTitleAndPolarity(string title, bool reminderOnly)
    {
        var habit = HydrateLegacyAvoidHabit(title);
        var update = new HabitUpdateParams(habit.Title, reminderOnly ? habit.Description : "Updated description",
            habit.FrequencyUnit, habit.FrequencyQuantity, habit.Days.ToList(), habit.IsBadHabit, habit.NextDueDate,
            ReminderEnabled: reminderOnly ? true : null, ReminderTimes: reminderOnly ? [30] : null);

        var result = habit.Update(update);

        result.IsSuccess.Should().BeTrue();
        habit.Title.Should().Be(title);
        habit.IsBadHabit.Should().BeTrue();
        habit.Description.Should().Be(reminderOnly ? "Original description" : "Updated description");
        habit.ReminderEnabled.Should().Be(reminderOnly);
        habit.ReminderTimes.Should().Equal(reminderOnly ? [30] : [15]);
    }

    [Fact]
    public void LegacyAvoidName_RenamingToAnotherNegationStillFails()
    {
        var habit = HydrateLegacyAvoidHabit("No caffeine");

        var result = habit.Update(new HabitUpdateParams("Avoid caffeine", habit.Description,
            habit.FrequencyUnit, habit.FrequencyQuantity, habit.Days.ToList(), true, habit.NextDueDate));

        result.ErrorCode.Should().Be("BAD_HABIT_NEGATED_NAME");
        habit.Title.Should().Be("No caffeine");
    }

    [Fact]
    public void Commitment_ChangingOnlyPolarityToAvoidStillFails()
    {
        var habit = Habit.Create(new HabitCreateParams(Guid.NewGuid(), "No caffeine", FrequencyUnit.Day, 1,
            new DateOnly(2026, 10, 27))).Value;

        var result = habit.Update(new HabitUpdateParams(habit.Title, habit.Description,
            habit.FrequencyUnit, habit.FrequencyQuantity, habit.Days.ToList(), true, habit.NextDueDate));

        result.ErrorCode.Should().Be("BAD_HABIT_NEGATED_NAME");
        habit.IsBadHabit.Should().BeFalse();
    }

    private static Habit HydrateLegacyAvoidHabit(string title)
    {
        var habit = Habit.Create(new HabitCreateParams(Guid.NewGuid(), "Caffeine", FrequencyUnit.Day, 1,
            new DateOnly(2026, 10, 27), Description: "Original description", IsBadHabit: true)).Value;
        typeof(Habit).GetProperty(nameof(Habit.Title))!.SetValue(habit, title);
        return habit;
    }

    [Theory]
    [InlineData("Não tomar multivitamínico", false)]
    [InlineData("Don't take multivitamins", false)]
    [InlineData("Fumar", true)]
    [InlineData("Smoking", true)]
    public void ValidCommitmentAndAvoidForms_AreAccepted(string title, bool avoid)
    {
        Habit.Create(new HabitCreateParams(Guid.NewGuid(), title, FrequencyUnit.Day, 1,
            new DateOnly(2026, 10, 27), IsBadHabit: avoid)).IsSuccess.Should().BeTrue();
    }
}
