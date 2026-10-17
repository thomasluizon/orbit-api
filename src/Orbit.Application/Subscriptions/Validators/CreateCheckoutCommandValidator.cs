using Orbit.Application.Common;
using FluentValidation;
using Orbit.Application.Subscriptions.Commands;

namespace Orbit.Application.Subscriptions.Validators;

public class CreateCheckoutCommandValidator : AbstractValidator<CreateCheckoutCommand>
{
    private static readonly string[] AllowedIntervals = ["monthly", "yearly"];

    public CreateCheckoutCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        RuleFor(x => x.Interval)
            .NotEmpty()
            .WithCopy(ValidationErrorCodes.BillingIntervalRequired)
            .Must(interval => AllowedIntervals.Contains(interval?.ToLower()))
            .WithCopy(ValidationErrorCodes.BillingIntervalSupported);
    }
}
