using FluentValidation;
using Orbit.Application.Chat.Queries;

namespace Orbit.Application.Chat.Validators;

public sealed class RefreshReadBlockQueryValidator : AbstractValidator<RefreshReadBlockQuery>
{
    public RefreshReadBlockQueryValidator()
    {
        RuleFor(q => q.UserId).NotEmpty();
        RuleFor(q => q.Token).NotEmpty().MaximumLength(32768);
    }
}
