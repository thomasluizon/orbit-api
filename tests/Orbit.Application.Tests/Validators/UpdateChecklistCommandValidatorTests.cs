using FluentAssertions;
using FluentValidation.TestHelper;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Validators;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Tests.Validators;

public class UpdateChecklistCommandValidatorTests
{
    private readonly UpdateChecklistCommandValidator _validator = new();

    private static UpdateChecklistCommand ValidCommand() => new(
        UserId: Guid.NewGuid(),
        HabitId: Guid.NewGuid(),
        ChecklistItems: [new ChecklistItem("Task 1", false)]);

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
    public void Validate_EmptyHabitId_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { HabitId = Guid.Empty });
        result.ShouldHaveValidationErrorFor(x => x.HabitId);
    }

    [Fact]
    public void Validate_NullChecklistItems_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { ChecklistItems = null! });
        result.ShouldHaveValidationErrorFor(x => x.ChecklistItems);
    }

    [Fact]
    public void Validate_TooManyItems_HasError()
    {
        var items = Enumerable.Range(0, 51)
            .Select(i => new ChecklistItem($"Item {i}", false))
            .ToList();
        var result = _validator.TestValidate(ValidCommand() with { ChecklistItems = items });
        result.ShouldHaveValidationErrorFor(x => x.ChecklistItems);
    }

    [Fact]
    public void Validate_MaxItems_NoError()
    {
        var items = Enumerable.Range(0, 50)
            .Select(i => new ChecklistItem($"Item {i}", false))
            .ToList();
        var result = _validator.TestValidate(ValidCommand() with { ChecklistItems = items });
        result.ShouldNotHaveValidationErrorFor(x => x.ChecklistItems);
    }

    [Fact]
    public void Validate_ItemTextTooLong_HasError()
    {
        var items = new List<ChecklistItem> { new(new string('a', 501), false) };
        var result = _validator.TestValidate(ValidCommand() with { ChecklistItems = items });
        result.ShouldHaveValidationErrorFor(x => x.ChecklistItems);
    }

    [Fact]
    public void Validate_EmptyList_NoError()
    {
        var result = _validator.TestValidate(ValidCommand() with { ChecklistItems = [] });
        result.ShouldNotHaveAnyValidationErrors();
    }
}
