using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Profile.Commands;

namespace Orbit.Application.Profile.Validators;

public class SetNameCommandValidator : AbstractValidator<SetNameCommand>
{
    public SetNameCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        RuleFor(x => x.Name)
            .NotEmpty()
            .Must(name => name is null || name.Trim().Length <= AppConstants.MaxUserNameLength)
            .WithMessage($"Name must be at most {AppConstants.MaxUserNameLength} characters");
    }
}
