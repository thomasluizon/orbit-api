using FluentAssertions;
using FluentValidation.TestHelper;
using Orbit.Application.UserFacts.Commands;
using Orbit.Application.UserFacts.Validators;

namespace Orbit.Application.Tests.Validators;

public class BulkDeleteUserFactsCommandValidatorTests
{
    private readonly BulkDeleteUserFactsCommandValidator _validator = new();

    private static BulkDeleteUserFactsCommand ValidCommand() => new(
        UserId: Guid.NewGuid(),
        FactIds: new[] { Guid.NewGuid(), Guid.NewGuid() });

    [Fact]
    public void Validate_ValidList_NoErrors()
    {
        var command = ValidCommand();

        var result = _validator.TestValidate(command);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyList_HasError()
    {
        var command = ValidCommand() with { FactIds = Array.Empty<Guid>() };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.FactIds);
    }

    [Fact]
    public void Validate_Over100Items_HasError()
    {
        var ids = Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToList();
        var command = ValidCommand() with { FactIds = ids };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.FactIds);
    }

    [Fact]
    public void Validate_EmptyFactId_HasError()
    {
        var command = ValidCommand() with { FactIds = new[] { Guid.Empty } };

        var result = _validator.TestValidate(command);

        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("Fact ID must not be empty"));
    }

    [Fact]
    public void Validate_EmptyUserId_HasError()
    {
        var command = ValidCommand() with { UserId = Guid.Empty };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.UserId);
    }
}
