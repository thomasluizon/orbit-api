using FluentValidation;
using Orbit.Application.Social.Queries;

namespace Orbit.Application.Social.Validators;

public class GetInvitePreviewQueryValidator : AbstractValidator<GetInvitePreviewQuery>
{
    public GetInvitePreviewQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
    }
}
