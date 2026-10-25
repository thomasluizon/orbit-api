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
        RuleFor(x => x.Endpoint)
            .Must((command, endpoint) => PushSubscription.ClassifyTransport(command.P256dh) != PushTransport.Wns
                || WnsEndpointPolicy.IsAllowed(endpoint))
            .WithMessage(DomainErrors.WnsEndpointInvalid.Message);
        RuleFor(x => x.ApnsTopic)
            .Must((command, topic) => PushSubscription.IsAllowedApnsTopic(topic)
                && (topic is null || PushSubscription.ClassifyTransport(command.P256dh) == PushTransport.Apns))
            .WithMessage(DomainErrors.PushApnsTopicInvalid.Message);
        RuleFor(x => x.DeviceId)
            .Must((command, deviceId) => deviceId != Guid.Empty
                && (deviceId is not null || PushSubscription.ClassifyTransport(command.P256dh) != PushTransport.Wns)
                && (deviceId is null || PushSubscription.ClassifyTransport(command.P256dh) is PushTransport.Wns or PushTransport.Apns))
            .WithMessage(DomainErrors.PushDeviceIdInvalid.Message);
    }
}
