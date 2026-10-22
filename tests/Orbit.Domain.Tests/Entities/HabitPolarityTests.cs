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
