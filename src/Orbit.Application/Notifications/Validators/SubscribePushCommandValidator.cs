using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Notifications.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Application.Notifications.Validators;

public class SubscribePushCommandValidator : AbstractValidator<SubscribePushCommand>
{
    public SubscribePushCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Endpoint).NotEmpty().MaximumLength(2000);
        RuleFor(x => x.Endpoint)
            .Must((command, endpoint) => PushSubscription.ClassifyTransport(command.P256dh) != PushTransport.WebPush
                || WebPushEndpointPolicy.IsAllowed(endpoint))
            .WithMessage(ErrorMessages.PushEndpointInvalid.Message)
            .WithErrorCode(ErrorCodes.PushEndpointInvalid);
        RuleFor(x => x.P256dh).NotEmpty().MaximumLength(500);
        RuleFor(x => x.Auth).NotEmpty().MaximumLength(500);
    }
}
