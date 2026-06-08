using FluentValidation.TestHelper;
using Orbit.Application.Subscriptions.Commands;
using Orbit.Application.Subscriptions.Validators;

namespace Orbit.Application.Tests.Validators;

public class VerifyPlayPurchaseCommandValidatorTests
{
    private readonly VerifyPlayPurchaseCommandValidator _validator = new();

    private static VerifyPlayPurchaseCommand ValidCommand() => new(
        UserId: Guid.NewGuid(),
        ProductId: "orbit_pro",
        PurchaseToken: "play_token_123");

    [Fact]
    public void Validate_Valid_NoErrors()
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
    public void Validate_EmptyProductId_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { ProductId = "" });
        result.ShouldHaveValidationErrorFor(x => x.ProductId);
    }

    [Fact]
    public void Validate_EmptyPurchaseToken_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { PurchaseToken = "" });
        result.ShouldHaveValidationErrorFor(x => x.PurchaseToken);
    }
}
