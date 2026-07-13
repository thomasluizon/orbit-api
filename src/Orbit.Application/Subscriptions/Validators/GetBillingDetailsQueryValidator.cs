using FluentValidation;
using Orbit.Application.Subscriptions.Queries;

namespace Orbit.Application.Subscriptions.Validators;

public class GetBillingDetailsQueryValidator : AbstractValidator<GetBillingDetailsQuery>
{
    public GetBillingDetailsQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
    }
}
