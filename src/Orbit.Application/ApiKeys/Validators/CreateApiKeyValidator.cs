using Orbit.Application.Common;
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
            .WithCopy(ValidationErrorCodes.ApiKeyNameRequired)
            .MaximumLength(50)
            .WithCopy(ValidationErrorCodes.ApiKeyNameLength);

        RuleForEach(x => x.Scopes)
            .NotEmpty()
            .WithCopy(ValidationErrorCodes.ApiKeyScopesRequired)
            .Must(scope => string.IsNullOrWhiteSpace(scope) || AgentScopes.All.Contains(scope.Trim()))
            .WithCopy(ValidationErrorCodes.ApiKeyScopeInvalid);

        RuleFor(x => x.ExpiresAtUtc)
            .Must(expiresAtUtc => !expiresAtUtc.HasValue || expiresAtUtc.Value > DateTime.UtcNow)
            .WithCopy(ValidationErrorCodes.ApiKeyExpiryFuture);
    }
}
