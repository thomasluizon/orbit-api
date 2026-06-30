using FluentValidation;
using Orbit.Application.Accountability.Commands;

namespace Orbit.Application.Accountability.Validators;

public class EndAccountabilityPairCommandValidator : AbstractValidator<EndAccountabilityPairCommand>
{
    public EndAccountabilityPairCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.PairId).NotEmpty();
    }
}
