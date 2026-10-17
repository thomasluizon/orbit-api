using FluentValidation.TestHelper;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Validators;

namespace Orbit.Application.Tests.Validators;

public sealed class UndoSkipHabitCommandValidatorTests
{
    private readonly UndoSkipHabitCommandValidator _validator = new();

    [Fact]
    public void ValidIds_AreAccepted() => _validator.TestValidate(
        new UndoSkipHabitCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void EmptyIds_AreRejected()
    {
        var result = _validator.TestValidate(new UndoSkipHabitCommand(Guid.Empty, Guid.Empty, Guid.Empty));
        result.ShouldHaveValidationErrorFor(command => command.UserId);
        result.ShouldHaveValidationErrorFor(command => command.HabitId);
        result.ShouldHaveValidationErrorFor(command => command.SkipId);
    }
}
