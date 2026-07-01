using FluentValidation;
using Orbit.Application.Social.Queries;

namespace Orbit.Application.Social.Validators;

public class GetFriendProfileQueryValidator : AbstractValidator<GetFriendProfileQuery>
{
    public GetFriendProfileQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.FriendUserId).NotEmpty().NotEqual(x => x.UserId);
    }
}
