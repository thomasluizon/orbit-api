using FluentValidation;
using Orbit.Application.Subscriptions.Queries;

namespace Orbit.Application.Subscriptions.Validators;

public class GetSubscriptionStatusQueryValidator : AbstractValidator<GetSubscriptionStatusQuery>
{
    public GetSubscriptionStatusQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
    }
}
