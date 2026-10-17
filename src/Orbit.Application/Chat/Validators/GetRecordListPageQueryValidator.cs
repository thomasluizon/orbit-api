using FluentValidation;
using Orbit.Application.Chat.Queries;

namespace Orbit.Application.Chat.Validators;

public class GetRecordListPageQueryValidator : AbstractValidator<GetRecordListPageQuery>
{
    public GetRecordListPageQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Kind).Must(kind => kind is "notifications" or "tags" or "templates" or "keys");
        RuleFor(x => x.Cursor).NotEmpty().MaximumLength(256);
    }
}
