using FluentValidation;
using Orbit.Application.Subscriptions.Commands;

namespace Orbit.Application.Subscriptions.Validators;

public sealed class VerifyAppStorePurchaseCommandValidator : AbstractValidator<VerifyAppStorePurchaseCommand>
{
    public VerifyAppStorePurchaseCommandValidator()
    {
        RuleFor(command => command.UserId).NotEmpty();
        RuleFor(command => command.SignedTransaction).NotEmpty().MaximumLength(100_000);
    }
}

public sealed class HandleAppStoreNotificationCommandValidator : AbstractValidator<HandleAppStoreNotificationCommand>
{
    public HandleAppStoreNotificationCommandValidator() =>
        RuleFor(command => command.SignedPayload).NotEmpty().MaximumLength(100_000);
}
