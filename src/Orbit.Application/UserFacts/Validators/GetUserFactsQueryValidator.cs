using FluentValidation;
using Orbit.Application.UserFacts.Queries;

namespace Orbit.Application.UserFacts.Validators;

public class GetUserFactsQueryValidator : AbstractValidator<GetUserFactsQuery>
{
    public GetUserFactsQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
    }
}
