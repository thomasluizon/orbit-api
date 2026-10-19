using FluentValidation;
using Orbit.Domain.Enums;

namespace Orbit.Application.Auth.Validators;

public sealed record OwedSubscriptionCancellationRequest(SubscriptionSource Provider, string ProviderIdentifier);

public sealed class OwedSubscriptionCancellationValidator : AbstractValidator<OwedSubscriptionCancellationRequest>
{
    public OwedSubscriptionCancellationValidator()
    {
        RuleFor(request => request.Provider).IsInEnum();
        RuleFor(request => request.ProviderIdentifier).NotEmpty();
    }
}
