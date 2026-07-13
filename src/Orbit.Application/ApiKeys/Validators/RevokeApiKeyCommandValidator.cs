using FluentValidation;
using Orbit.Application.ApiKeys.Commands;

namespace Orbit.Application.ApiKeys.Validators;

public class RevokeApiKeyCommandValidator : AbstractValidator<RevokeApiKeyCommand>
{
    public RevokeApiKeyCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.KeyId).NotEmpty();
    }
}
