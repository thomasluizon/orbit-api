using FluentValidation;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Validators;

public sealed record StartingStepArguments(string? Goal, string? Step, string? Language, int StepCount);

public sealed class StartingStepArgumentsValidator : AbstractValidator<StartingStepArguments>
{
    public StartingStepArgumentsValidator()
    {
        RuleFor(value => value.Goal).Must(StartingStep.IsValidText);
        RuleFor(value => value.Step).Must(StartingStep.IsValidText);
        RuleFor(value => value.Language).Must(StartingStep.IsValidLanguage);
        RuleFor(value => value.StepCount).Equal(1);
    }
}
