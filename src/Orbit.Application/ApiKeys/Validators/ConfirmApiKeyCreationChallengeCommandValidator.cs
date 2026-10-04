using Orbit.Application.Common;
using FluentValidation;
using Orbit.Application.ApiKeys.Commands;

namespace Orbit.Application.ApiKeys.Validators;

public sealed class ConfirmApiKeyCreationChallengeCommandValidator : AbstractValidator<ConfirmApiKeyCreationChallengeCommand>
{
    public ConfirmApiKeyCreationChallengeCommandValidator()
    {
        RuleFor(command => command.UserId)
            .NotEmpty();

        RuleFor(command => command.Code)
            .NotEmpty()
            .Length(6)
            .Matches(@"^\d{6}$")
            .WithCopy(ValidationErrorCodes.VerificationCodeFormat);
    }
}
