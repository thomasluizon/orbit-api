using FluentAssertions;
using FluentValidation.TestHelper;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Validators;

namespace Orbit.Application.Tests.Validators;

public class BulkLogHabitsCommandValidatorTests
{
    private readonly BulkLogHabitsCommandValidator _validator = new();

    private static BulkLogHabitsCommand ValidCommand() => new(
        UserId: Guid.NewGuid(),
        Items: [new BulkLogItem(Guid.NewGuid())]);

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
    public void Validate_NullItems_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { Items = null! });
        result.ShouldHaveValidationErrorFor(x => x.Items);
    }

    [Fact]
    public void Validate_EmptyItems_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { Items = [] });
        result.ShouldHaveValidationErrorFor(x => x.Items);
    }

    [Fact]
    public void Validate_TooManyItems_HasError()
    {
        var items = Enumerable.Range(0, AppConstants.MaxBulkOperationSize + 1)
            .Select(_ => new BulkLogItem(Guid.NewGuid())).ToList();

        var result = _validator.TestValidate(ValidCommand() with { Items = items });
        result.ShouldHaveValidationErrorFor(x => x.Items);
    }

    [Fact]
    public void Validate_ExactlyMaxItems_NoError()
    {
        var items = Enumerable.Range(0, AppConstants.MaxBulkOperationSize)
            .Select(_ => new BulkLogItem(Guid.NewGuid())).ToList();

        var result = _validator.TestValidate(ValidCommand() with { Items = items });
        result.ShouldNotHaveValidationErrorFor(x => x.Items);
    }

    [Fact]
    public void Validate_ItemWithEmptyHabitId_HasError()
    {
        var items = new List<BulkLogItem> { new(Guid.Empty) };

        var result = _validator.TestValidate(ValidCommand() with { Items = items });
        result.Errors.Should().Contain(e => e.PropertyName.Contains("HabitId"));
    }

    [Fact]
    public void Validate_MultipleValidItems_NoErrors()
    {
        var items = new List<BulkLogItem>
        {
            new(Guid.NewGuid()),
            new(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow))
        };

        var result = _validator.TestValidate(ValidCommand() with { Items = items });
        result.ShouldNotHaveAnyValidationErrors();
    }
}
