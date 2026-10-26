using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Onboarding.Commands;
using Orbit.Domain.Models;

namespace Orbit.Application.Onboarding.Validators;

public sealed class SuggestOnboardingSetupCommandValidator : AbstractValidator<SuggestOnboardingSetupCommand>
{
    public SuggestOnboardingSetupCommandValidator()
    {
        RuleFor(request => request.Title).NotEmpty().MaximumLength(OnboardingInput.MaxTitleLength);
        RuleFor(request => request.Language).Must(language => language is not null && AppConstants.SupportedLanguages.Contains(language));
        RuleFor(request => request.InstallId).MaximumLength(4096);
        When(request => request.Attestation is not null, () =>
            RuleFor(request => request.Attestation!).SetValidator(new OnboardingAttestationValidator()));
    }
}

public sealed class GetOnboardingChallengeCommandValidator : AbstractValidator<GetOnboardingChallengeCommand>
{
    public GetOnboardingChallengeCommandValidator()
        => RuleFor(request => request.Platform).Must(platform => platform is "web" or "desktop" or "android" or "ios");
}

public sealed class EnrollOnboardingInstallCommandValidator : AbstractValidator<EnrollOnboardingInstallCommand>
{
    public EnrollOnboardingInstallCommandValidator()
    {
        RuleFor(request => request.Challenge).NotEmpty().MaximumLength(4096);
        RuleFor(request => request.Attestation).NotNull().SetValidator(new OnboardingAttestationValidator());
    }
}

public sealed class OnboardingAttestationValidator : AbstractValidator<OnboardingAttestation>
{
    public OnboardingAttestationValidator()
    {
        RuleFor(evidence => evidence.Platform).Must(platform => platform is "web" or "desktop" or "android" or "ios");
        RuleFor(evidence => evidence.Token).NotEmpty().MaximumLength(32768);
        RuleFor(evidence => evidence.KeyId).MaximumLength(128);
    }
}
