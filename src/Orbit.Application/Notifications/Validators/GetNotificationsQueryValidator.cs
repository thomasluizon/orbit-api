using FluentValidation;
using Orbit.Application.Notifications.Queries;
using Orbit.Application.Common;

namespace Orbit.Application.Notifications.Validators;

public class GetNotificationsQueryValidator : AbstractValidator<GetNotificationsQuery>
{
    public GetNotificationsQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Offset).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Limit).InclusiveBetween(1, AppConstants.MaxNotificationsReturned);
    }
}
