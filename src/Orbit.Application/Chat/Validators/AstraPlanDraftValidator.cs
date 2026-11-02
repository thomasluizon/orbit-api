using FluentValidation;
using Orbit.Application.Chat.Models;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Validators;

public sealed class AstraPlanDraftValidator : AbstractValidator<AstraPlanDraft>
{
    public AstraPlanDraftValidator()
    {
        RuleFor(plan => plan.Title).Must(AstraPlan.IsShortText);
        RuleFor(plan => plan.Steps).NotNull().Must(steps => steps is { Count: >= 2 and <= 8 });
        RuleForEach(plan => plan.Steps).ChildRules(step =>
        {
            step.RuleFor(value => value.Text).Must(AstraPlan.IsShortText);
            step.RuleFor(value => value.Tool).NotEmpty().MaximumLength(100);
            step.RuleFor(value => value.Phase).Must(phase => phase is "execution" or "approval");
        });
    }
}
