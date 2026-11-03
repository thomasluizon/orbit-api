using FluentValidation;

namespace Orbit.Application.Marketing.Validators;

public sealed record MarketingRecipientDelivery(Guid BroadcastId, Guid RecipientId, string Email, string Language);

public sealed class MarketingRecipientDeliveryValidator : AbstractValidator<MarketingRecipientDelivery>
{
    public MarketingRecipientDeliveryValidator()
    {
        RuleFor(request => request.BroadcastId).NotEmpty();
        RuleFor(request => request.RecipientId).NotEmpty();
        RuleFor(request => request.Email).NotEmpty().MaximumLength(320).EmailAddress();
        RuleFor(request => request.Language).NotEmpty().MaximumLength(32);
    }
}
