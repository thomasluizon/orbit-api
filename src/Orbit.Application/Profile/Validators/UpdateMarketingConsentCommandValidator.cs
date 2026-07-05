using FluentValidation;
using Orbit.Application.Profile.Commands;

namespace Orbit.Application.Profile.Validators;

public class UpdateMarketingConsentCommandValidator : AbstractValidator<UpdateMarketingConsentCommand>
{
    public UpdateMarketingConsentCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
    }
}
