using FluentValidation;
using Orbit.Application.Subscriptions.Queries;

namespace Orbit.Application.Subscriptions.Validators;

public class GetPlansQueryValidator : AbstractValidator<GetPlansQuery>
{
    public GetPlansQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
    }
}
