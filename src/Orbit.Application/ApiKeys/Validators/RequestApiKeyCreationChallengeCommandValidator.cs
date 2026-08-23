using FluentValidation;
using Orbit.Application.ApiKeys.Commands;

namespace Orbit.Application.ApiKeys.Validators;

public sealed class RequestApiKeyCreationChallengeCommandValidator : AbstractValidator<RequestApiKeyCreationChallengeCommand>
{
    public RequestApiKeyCreationChallengeCommandValidator()
    {
        RuleFor(command => command.UserId)
            .NotEmpty();
    }
}
