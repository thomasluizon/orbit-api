using FluentAssertions;
using NSubstitute;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Validators;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Validators;

public class HabitStartDateValidatorTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("end-before")]
    [InlineData("general")]
    [InlineData("one-time")]
    [InlineData("minimum")]
    public async Task StartDate_ValidatedOnCreateAndUpdate(string scenario)
    {
        var start = scenario == "minimum" ? DateOnly.MinValue : new DateOnly(2028, 2, 29);
        var general = scenario == "general";
        var unit = general || scenario == "one-time" ? (FrequencyUnit?)null : FrequencyUnit.Day;
        var quantity = unit.HasValue ? (int?)1 : null;
        var end = scenario == "end-before" ? start.AddDays(-1) : (DateOnly?)null;
        var user = Guid.NewGuid();
        var create = new CreateHabitCommand(user, "Read", null, unit, quantity,
            IsGeneral: general, StartDate: start, Options: new HabitCommandOptions(EndDate: end));
        new CreateHabitCommandValidator().Validate(create).IsValid.Should().Be(scenario == "valid");
        var update = new UpdateHabitCommand(user, Guid.NewGuid(), "Read", null, unit, quantity,
            IsGeneral: general, StartDate: start, Options: new UpdateHabitCommandOptions(EndDate: end));
        (await new UpdateHabitCommandValidator(Substitute.For<IGenericRepository<Habit>>()).ValidateAsync(update))
            .IsValid.Should().Be(scenario == "valid");
    }
}
