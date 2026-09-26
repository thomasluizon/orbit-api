using FluentValidation;

namespace Orbit.Application.Chat.Validators;

public sealed class RevisePendingOperationRequestValidator : AbstractValidator<RevisePendingOperationRequest>
{
    public RevisePendingOperationRequestValidator()
    {
        RuleFor(request => request.PreviewFingerprint).NotEmpty().MaximumLength(256);
        RuleFor(request => request.Items).NotNull()
            .Must(items => items is not null && items.Count <= 500)
            .WithMessage("At most 500 items may be revised.");
        RuleFor(request => request.Items)
            .Must(items => items is not null && items.Select(item => item.ItemId)
                .Distinct(StringComparer.Ordinal).Count() == items.Count)
            .WithMessage("Item IDs must be unique.");
        RuleForEach(request => request.Items).ChildRules(item =>
            item.RuleFor(value => value.ItemId).NotEmpty().MaximumLength(100));
    }
}
