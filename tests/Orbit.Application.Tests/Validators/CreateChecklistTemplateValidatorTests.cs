using FluentValidation.TestHelper;
using Orbit.Application.ChecklistTemplates.Commands;
using Orbit.Application.ChecklistTemplates.Validators;
using Orbit.Application.Common;

namespace Orbit.Application.Tests.Validators;

public class CreateChecklistTemplateValidatorTests
{
    private readonly CreateChecklistTemplateCommandValidator _validator = new();

    private static CreateChecklistTemplateCommand ValidCommand() => new(
        UserId: Guid.NewGuid(),
        Name: "Morning Checklist",
        Items: ["Brush teeth", "Drink water"]);

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
    public void Validate_EmptyName_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { Name = "" });
        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void Validate_NullName_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { Name = null! });
        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void Validate_NameOver100Chars_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { Name = new string('a', 101) });
        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void Validate_NameExactly100Chars_NoError()
    {
        var result = _validator.TestValidate(ValidCommand() with { Name = new string('a', 100) });
        result.ShouldNotHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void Validate_TooManyItems_HasError()
    {
        var items = Enumerable.Range(0, AppConstants.MaxChecklistItems + 1)
            .Select(i => $"Item {i}").ToList();

        var result = _validator.TestValidate(ValidCommand() with { Items = items });
        result.ShouldHaveValidationErrorFor(x => x.Items);
    }

    [Fact]
    public void Validate_ExactlyMaxItems_NoError()
    {
        var items = Enumerable.Range(0, AppConstants.MaxChecklistItems)
            .Select(i => $"Item {i}").ToList();

        var result = _validator.TestValidate(ValidCommand() with { Items = items });
        result.ShouldNotHaveValidationErrorFor(x => x.Items);
    }

    [Fact]
    public void Validate_NullItems_NoError()
    {
        // Items null is allowed (When guard prevents the Must rule from firing)
        var result = _validator.TestValidate(ValidCommand() with { Items = null! });
        result.ShouldNotHaveValidationErrorFor(x => x.Items);
    }
}
