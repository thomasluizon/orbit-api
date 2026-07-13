using FluentValidation;
using Orbit.Application.Notifications.Queries;

namespace Orbit.Application.Notifications.Validators;

public class GetNotificationsQueryValidator : AbstractValidator<GetNotificationsQuery>
{
    public GetNotificationsQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
    }
}
