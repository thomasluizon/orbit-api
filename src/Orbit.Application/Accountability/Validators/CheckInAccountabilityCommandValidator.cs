using FluentValidation;
using Orbit.Application.Accountability.Commands;
using Orbit.Application.Common;

namespace Orbit.Application.Accountability.Validators;

public class CheckInAccountabilityCommandValidator : AbstractValidator<CheckInAccountabilityCommand>
{
    public CheckInAccountabilityCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.PairId).NotEmpty();
        RuleFor(x => x.Note).MaximumLength(AppConstants.MaxAccountabilityNoteLength);
    }
}
