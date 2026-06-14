using FluentValidation;
using Orbit.Application.ApiKeys.Commands;
using Orbit.Domain.Models;

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
            .WithMessage("API key scopes must be non-empty strings.")
            .Must(scope => string.IsNullOrWhiteSpace(scope) || AgentScopes.All.Contains(scope.Trim()))
            .WithMessage("API key scope '{PropertyValue}' is not a recognized scope.");

        RuleFor(x => x.ExpiresAtUtc)
            .Must(expiresAtUtc => !expiresAtUtc.HasValue || expiresAtUtc.Value > DateTime.UtcNow)
            .WithMessage("API key expiry must be in the future.");
    }
}
