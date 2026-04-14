using FluentValidation;
using Orbit.Application.ApiKeys.Commands;

namespace Orbit.Application.ApiKeys.Validators;

public class CreateApiKeyValidator : AbstractValidator<CreateApiKeyCommand>
{
    public CreateApiKeyValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        RuleFor(x => x.Name)
            .NotEmpty()
            .WithMessage("API key name is required.")
            .MaximumLength(50)
            .WithMessage("API key name must be 50 characters or less.");

        RuleForEach(x => x.Scopes)
            .NotEmpty()
            .WithMessage("API key scopes must be non-empty strings.");

        RuleFor(x => x.ExpiresAtUtc)
            .Must(expiresAtUtc => !expiresAtUtc.HasValue || expiresAtUtc.Value > DateTime.UtcNow)
            .WithMessage("API key expiry must be in the future.");
    }
}
