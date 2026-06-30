using FluentValidation;
using Orbit.Application.Profile.Commands;

namespace Orbit.Application.Profile.Validators;

public class UpdatePublicProfileCommandValidator : AbstractValidator<UpdatePublicProfileCommand>
{
    public UpdatePublicProfileCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
    }
}
