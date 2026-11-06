using Orbit.Domain.Enums;
using System.Text.Json;
using FluentAssertions;
using Orbit.Application.Chat.Validators;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

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

    [Theory]
    [InlineData("Yes, do that", true)]
    [InlineData("Yes, prepare that change for me to approve.", true)]
    [InlineData("Sim, pode fazer", true)]
    [InlineData("Make both positive", false)]
    [InlineData("Please make both positive", false)]
    [InlineData("Pode deixar os dois positivos", false)]
    public void ConfirmationReply_CarriesThePreviousScheduleRequest(string message, bool confirms)
    {
        var current = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, Guid.NewGuid(), "Pay bills", FrequencyUnit.Month, 1,
            new DateOnly(2026, 11, 8), MonthDays: [8, 15])).Value;
        List<ChatHistoryMessage> history =
        [
            new("user", "Change the days of the month of Pay bills to the 5th and the 25th."),
            new("assistant", "Should I prepare that change?")
        ];
        var changes = JsonSerializer.SerializeToElement(new { month_days = new[] { 5, 25 } });

        var validation = new HabitCorrectionProposalValidator().Validate(new HabitCorrectionProposal(
            HabitCorrectionProposalValidator.RequestText(message, history), changes, current));

        validation.IsValid.Should().Be(confirms);
        if (!confirms)
            validation.Errors.Should().Contain(error => error.ErrorCode == "CORRECTION_PRESERVES_SCHEDULE");
    }

    [Fact]
    public void ConfirmationReply_WithoutAPreviousScheduleRequest_StillPreservesTheSchedule()
    {
        var current = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, Guid.NewGuid(), "Pay bills", FrequencyUnit.Month, 1,
            new DateOnly(2026, 11, 8), MonthDays: [8, 15])).Value;
        var changes = JsonSerializer.SerializeToElement(new { is_bad_habit = false, month_days = new[] { 5, 25 } });

        foreach (var history in new List<ChatHistoryMessage>?[] { null, [], [new("user", "Make Pay bills positive.")] })
            new HabitCorrectionProposalValidator().Validate(new HabitCorrectionProposal(
                    HabitCorrectionProposalValidator.RequestText("Yes, do that", history), changes, current)).Errors
                .Should().Contain(error => error.ErrorCode == "CORRECTION_PRESERVES_SCHEDULE");
    }
}
