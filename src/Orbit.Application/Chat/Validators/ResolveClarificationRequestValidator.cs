using FluentValidation;
using Orbit.Application.Chat.Models;

namespace Orbit.Application.Chat.Validators;

public class ResolveClarificationRequestValidator : AbstractValidator<ResolveClarificationRequest>
{
    public const int MaxValueLength = 2048;

    public ResolveClarificationRequestValidator()
    {
        RuleFor(x => x.Value)
            .NotEmpty()
            .WithMessage("Clarification value cannot be empty.")
            .MaximumLength(MaxValueLength)
            .WithMessage($"Clarification value cannot exceed {MaxValueLength} characters.");
    }
}
