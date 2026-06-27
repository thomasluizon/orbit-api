using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Social.Queries;

namespace Orbit.Application.Social.Validators;

public class GetFriendFeedQueryValidator : AbstractValidator<GetFriendFeedQuery>
{
    public GetFriendFeedQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();

        When(x => x.PageSize.HasValue, () =>
            RuleFor(x => x.PageSize!.Value).InclusiveBetween(1, AppConstants.MaxFriendFeedPageSize));

        When(x => !string.IsNullOrEmpty(x.Cursor), () =>
            RuleFor(x => x.Cursor!)
                .Must(cursor => FeedCursor.TryDecode(cursor, out _, out _))
                .WithMessage("Cursor is malformed."));
    }
}
