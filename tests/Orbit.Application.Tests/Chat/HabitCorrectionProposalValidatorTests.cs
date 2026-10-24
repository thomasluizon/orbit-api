using Orbit.Domain.Enums;
using System.Text.Json;
using FluentAssertions;
using Orbit.Application.Chat.Validators;
using Orbit.Domain.Entities;

namespace Orbit.Application.Tests.Chat;

public class HabitCorrectionProposalValidatorTests
{
    [Theory]
    [InlineData("Deixe os dois como positivos", "Não tomar multivitamínico")]
    [InlineData("Make both positive", "Don't take multivitamins")]
    public void PolarityCorrection_PreservesOneTimeAndRejectsInventedRecurrence(string message, string title)
    {
        var current = Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, Guid.NewGuid(), title, null, null,
            new DateOnly(2026, 10, 29), DueTime: new TimeOnly(13, 30))).Value;
        var validator = new HabitCorrectionProposalValidator();
        var patch = JsonSerializer.SerializeToElement(new { title, is_bad_habit = false });
        validator.Validate(new HabitCorrectionProposal(message, patch, current)).IsValid.Should().BeTrue();
        var drifting = JsonSerializer.SerializeToElement(new { title, is_bad_habit = false, frequency_unit = "Day", frequency_quantity = 1 });
        validator.Validate(new HabitCorrectionProposal(message, drifting, current)).Errors
            .Should().Contain(error => error.ErrorCode == "CORRECTION_PRESERVES_SCHEDULE");
        current.FrequencyUnit.Should().BeNull();
        current.DueTime.Should().Be(new TimeOnly(13, 30));
    }

    [Theory]
    [InlineData("Agora quero repetir todo dia")]
    [InlineData("Now repeat this daily")]
    public void RequestedRecurrenceChange_IsAllowed(string message)
    {
        var current = Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, Guid.NewGuid(), "Read", null, null,
            new DateOnly(2026, 10, 29))).Value;
        var changes = JsonSerializer.SerializeToElement(new { frequency_unit = "Day", frequency_quantity = 1 });
        new HabitCorrectionProposalValidator().Validate(new HabitCorrectionProposal(message, changes, current))
            .IsValid.Should().BeTrue();
    }
}
