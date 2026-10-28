using FluentAssertions;
using NSubstitute;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Validators;
using Orbit.Domain.Enums;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Tests.Validators;

public sealed class RepeatReminderValidatorTests
{
    [Theory]
    [InlineData(15, false)]
    [InlineData(15, true)]
    [InlineData(1380, false)]
    [InlineData(1380, true)]
    public async Task CreateAndUpdate_ValidateRepresentableStart(int minutes, bool overflow)
    {
        var startAtUtc = DateTime.SpecifyKind(DateTime.MaxValue.AddMinutes(-minutes), DateTimeKind.Utc);
        if (overflow)
            startAtUtc = startAtUtc.AddTicks(1);
        var create = new CreateHabitCommand(Guid.NewGuid(), "Drink water", null, FrequencyUnit.Day, 1,
            Options: new(RepeatReminder: new(minutes, StartAtUtc: startAtUtc)));
        var update = new UpdateHabitCommand(create.UserId, Guid.NewGuid(), create.Title, null, FrequencyUnit.Day, 1,
            Options: new(RepeatReminder: create.Options!.RepeatReminder));
        new CreateHabitCommandValidator().Validate(create).IsValid.Should().Be(!overflow);
        (await new UpdateHabitCommandValidator(Substitute.For<IGenericRepository<Habit>>()).ValidateAsync(update))
            .IsValid.Should().Be(!overflow);
    }

    [Theory]
    [InlineData(14, false)]
    [InlineData(15, true)]
    [InlineData(20, true)]
    [InlineData(30, true)]
    [InlineData(45, true)]
    [InlineData(60, true)]
    [InlineData(90, false)]
    [InlineData(1380, true)]
    [InlineData(1440, false)]
    public async Task CreateAndUpdate_ValidateTheSameIntervalBoundary(int minutes, bool valid)
    {
        var create = new CreateHabitCommand(Guid.NewGuid(), "Drink water", null, FrequencyUnit.Day, 1,
            Options: new(RepeatReminder: new(minutes)));
        var update = new UpdateHabitCommand(create.UserId, Guid.NewGuid(), create.Title, null, FrequencyUnit.Day, 1,
            Options: new(RepeatReminder: new(minutes)));
        new CreateHabitCommandValidator().Validate(create).IsValid.Should().Be(valid);
        (await new UpdateHabitCommandValidator(Substitute.For<IGenericRepository<Habit>>()).ValidateAsync(update)).IsValid.Should().Be(valid);
    }

    [Fact]
    public void ActiveWindowAndStart_RejectPartialWindowsAndNonUtcInstants()
    {
        RepeatReminderOptions[] invalid = [new(60, new(22, 0)), new(60, new(22, 0), new(22, 0)),
            new(60, StartAtUtc: new DateTime(2027, 9, 26))];
        foreach (var repeat in invalid)
        {
            var command = new CreateHabitCommand(Guid.NewGuid(), "Drink water", null, FrequencyUnit.Day, 1,
                Options: new(RepeatReminder: repeat));
            new CreateHabitCommandValidator().Validate(command).IsValid.Should().BeFalse();
        }
    }

    [Fact]
    public void DoseAndDone_RejectInvalidInstantsAndEmptyTokens()
    {
        new LogHabitCommandValidator().Validate(new LogHabitCommand(Guid.NewGuid(), Guid.NewGuid(), TakenAtUtc: DateTime.UtcNow.AddHours(1)))
            .IsValid.Should().BeFalse();
        new CorrectHabitDoseCommandValidator().Validate(new CorrectHabitDoseCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified))).IsValid.Should().BeFalse();
        new CompleteReminderCommandValidator().Validate(new CompleteReminderCommand("")).IsValid.Should().BeFalse();
        new CompleteReminderCommandValidator().Validate(new CompleteReminderCommand(new string('a', 2049))).IsValid.Should().BeFalse();
    }
}
