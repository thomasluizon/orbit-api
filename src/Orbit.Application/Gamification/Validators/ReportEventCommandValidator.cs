using FluentValidation;
using Orbit.Application.Gamification.Commands;

namespace Orbit.Application.Gamification.Validators;

public class ReportEventCommandValidator : AbstractValidator<ReportEventCommand>
{
    public ReportEventCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.EventKey)
            .NotEmpty()
            .Must(eventKey => AchievementEventMap.IsKnown(eventKey))
            .WithMessage("Unknown event key.");
    }
}
