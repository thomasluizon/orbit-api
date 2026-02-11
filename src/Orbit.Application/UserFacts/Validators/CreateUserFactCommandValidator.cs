using FluentValidation;
using Orbit.Application.UserFacts.Commands;

namespace Orbit.Application.UserFacts.Validators;

public class CreateUserFactCommandValidator : AbstractValidator<CreateUserFactCommand>
{
    private static readonly string[] AllowedCategories = ["preference", "routine", "context"];

    public CreateUserFactCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        RuleFor(x => x.FactText)
            .NotEmpty()
            .MaximumLength(500);

        RuleFor(x => x.Category)
            .Must(c => c is null || AllowedCategories.Contains(c.ToLowerInvariant()))
            .WithMessage("Category must be one of: preference, routine, context.");
    }
}
