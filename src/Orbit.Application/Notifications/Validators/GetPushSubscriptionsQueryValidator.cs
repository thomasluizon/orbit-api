using FluentValidation;
using Orbit.Application.Notifications.Queries;

namespace Orbit.Application.Notifications.Validators;

public class GetPushSubscriptionsQueryValidator : AbstractValidator<GetPushSubscriptionsQuery>
{
    public GetPushSubscriptionsQueryValidator()
    {
        RuleFor(query => query.UserId).NotEmpty();
    }
}
