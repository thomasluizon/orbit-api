using FluentAssertions;
using FluentValidation.TestHelper;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Validators;

namespace Orbit.Application.Tests.Validators;

public class BulkSkipHabitsCommandValidatorTests
{
    private readonly BulkSkipHabitsCommandValidator _validator = new();

    private static BulkSkipHabitsCommand ValidCommand() => new(
        UserId: Guid.NewGuid(),
        Items: [new BulkSkipItem(Guid.NewGuid())]);

    [Fact]
    public void Validate_ValidCommand_NoErrors()
    {
        var result = _validator.TestValidate(ValidCommand());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyUserId_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { UserId = Guid.Empty });
        result.ShouldHaveValidationErrorFor(x => x.UserId);
    }

    [Fact]
    public void Validate_EmptyItemsList_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { Items = [] });
        result.ShouldHaveValidationErrorFor(x => x.Items);
    }

    [Fact]
    public void Validate_TooManyItems_HasError()
    {
        var items = Enumerable.Range(0, 101)
            .Select(_ => new BulkSkipItem(Guid.NewGuid()))
            .ToList();
        var result = _validator.TestValidate(ValidCommand() with { Items = items });
        result.ShouldHaveValidationErrorFor(x => x.Items);
    }

    [Fact]
    public void Validate_MaxItems_NoError()
    {
        var items = Enumerable.Range(0, 100)
            .Select(_ => new BulkSkipItem(Guid.NewGuid()))
            .ToList();
        var result = _validator.TestValidate(ValidCommand() with { Items = items });
        result.ShouldNotHaveValidationErrorFor(x => x.Items);
    }

    [Fact]
    public void Validate_ItemWithEmptyHabitId_HasError()
    {
        var command = ValidCommand() with { Items = [new BulkSkipItem(Guid.Empty)] };
        var result = _validator.TestValidate(command);
        result.Errors.Should().Contain(e => e.PropertyName.Contains("HabitId"));
    }
}
