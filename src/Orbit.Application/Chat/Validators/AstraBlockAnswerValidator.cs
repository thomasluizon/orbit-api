using FluentValidation;
using Orbit.Domain.Models;
using Orbit.Application.Common;

namespace Orbit.Application.Chat.Validators;

public sealed class AstraBlockAnswerValidator : AbstractValidator<AstraBlockAnswer>
{
    public AstraBlockAnswerValidator()
    {
        RuleFor(answer => answer.BlockId).NotEmpty().WithCopy(ErrorCodes.AstraBlockIdRequired);
        RuleFor(answer => answer.SelectedIds).NotNull().WithCopy(ErrorCodes.AstraBlockSelectionRequired);
        RuleFor(answer => answer.SelectedIds)
            .Must(ids => ids is null || ids.All(id => !string.IsNullOrWhiteSpace(id))
                && ids.Distinct(StringComparer.Ordinal).Count() == ids.Count)
            .WithCopy(ErrorCodes.AstraBlockInvalidSelection);
        RuleFor(answer => answer.FreeText).MaximumLength(500).WithCopy(ErrorCodes.AstraBlockFreeTextTooLong);
    }
}
