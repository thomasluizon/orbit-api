using FluentValidation;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Validators;

public sealed class AstraBlockAnswerValidator : AbstractValidator<AstraBlockAnswer>
{
    public AstraBlockAnswerValidator()
    {
        RuleFor(answer => answer.BlockId).NotEmpty().WithErrorCode("ASTRA_BLOCK_ID_REQUIRED");
        RuleFor(answer => answer.SelectedIds).NotNull().WithErrorCode("ASTRA_BLOCK_SELECTION_REQUIRED");
        RuleFor(answer => answer.SelectedIds)
            .Must(ids => ids is null || ids.All(id => !string.IsNullOrWhiteSpace(id))
                && ids.Distinct(StringComparer.Ordinal).Count() == ids.Count)
            .WithErrorCode("ASTRA_BLOCK_INVALID_SELECTION");
        RuleFor(answer => answer.FreeText).MaximumLength(500).WithErrorCode("ASTRA_BLOCK_FREE_TEXT_TOO_LONG");
    }
}
