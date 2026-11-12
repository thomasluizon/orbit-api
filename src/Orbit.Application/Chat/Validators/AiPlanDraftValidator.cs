using FluentValidation;
using Orbit.Application.Chat.Models;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Validators;

public sealed class AiPlanDraftValidator : AbstractValidator<AiPlanDraft>
{
    public AiPlanDraftValidator()
    {
        RuleFor(plan => plan.Title).Must(AiPlan.IsShortText);
        RuleFor(plan => plan.Steps).NotNull().Must(steps => steps is { Count: >= 2 });
        RuleForEach(plan => plan.Steps).NotNull().ChildRules(step =>
        {
            step.RuleFor(value => value.Text).Must(AiPlan.IsShortText);
            step.RuleFor(value => value.Tool).NotEmpty().MaximumLength(100);
            step.RuleFor(value => value.Phase).Must(phase => phase is "execution" or "approval");
        });
    }
}
