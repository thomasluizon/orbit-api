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
            .WithMessage("Billing interval is required.")
            .Must(interval => AllowedIntervals.Contains(interval?.ToLower()))
            .WithMessage("Billing interval must be 'monthly' or 'yearly'.");
    }
}
