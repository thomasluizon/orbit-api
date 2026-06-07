using FluentValidation;
using Orbit.Application.Subscriptions.Commands;

namespace Orbit.Application.Subscriptions.Validators;

public class VerifyPlayPurchaseCommandValidator : AbstractValidator<VerifyPlayPurchaseCommand>
{
    public VerifyPlayPurchaseCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.ProductId).NotEmpty();
        RuleFor(x => x.PurchaseToken).NotEmpty();
    }
}
