using FluentValidation;
using Orbit.Application.Social.Queries;

namespace Orbit.Application.Social.Validators;

public class GetFriendsQueryValidator : AbstractValidator<GetFriendsQuery>
{
    public GetFriendsQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
    }
}
