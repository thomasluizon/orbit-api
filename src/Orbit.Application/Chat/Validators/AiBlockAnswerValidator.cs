using FluentValidation;
using Orbit.Domain.Models;
using Orbit.Application.Common;

namespace Orbit.Application.Chat.Validators;

public sealed class AiBlockAnswerValidator : AbstractValidator<AiBlockAnswer>
{
    public AiBlockAnswerValidator()
    {
        RuleFor(answer => answer.BlockId).NotEmpty().WithCopy(ErrorCodes.AiBlockIdRequired);
        RuleFor(answer => answer.SelectedIds).NotNull().WithCopy(ErrorCodes.AiBlockSelectionRequired);
        RuleFor(answer => answer.SelectedIds)
            .Must(ids => ids is null || ids.All(id => !string.IsNullOrWhiteSpace(id))
                && ids.Distinct(StringComparer.Ordinal).Count() == ids.Count)
            .WithCopy(ErrorCodes.AiBlockInvalidSelection);
        RuleFor(answer => answer.FreeText).MaximumLength(500).WithCopy(ErrorCodes.AiBlockFreeTextTooLong);
        RuleFor(answer => answer.Answers).Must(answers => answers is null
            || answers.Count is >= 1 and <= 10 && answers.All(AiQuestionRules.IsValidAnswerShape)
            && answers.Select(answer => answer.QuestionId).Distinct(StringComparer.Ordinal).Count() == answers.Count)
            .WithCopy(ErrorCodes.AiBlockInvalidSelection);
    }
}
