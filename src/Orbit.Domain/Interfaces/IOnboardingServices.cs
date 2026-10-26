using Orbit.Domain.Models;

namespace Orbit.Domain.Interfaces;

public interface IOnboardingAttestationVerifier
{
    bool Supports(string platform);
    bool IsConfigured { get; }
    Task<VerifiedOnboardingAttestation?> VerifyAsync(OnboardingAttestation evidence,
        string challenge, string? publicKey, uint counter, CancellationToken cancellationToken);
}

public interface IOnboardingAccessService
{
    Task<OnboardingChallenge?> ChallengeAsync(string platform, string clientIp, CancellationToken cancellationToken);
    Task<OnboardingInstall?> EnrollAsync(string challenge, OnboardingAttestation evidence,
        string clientIp, CancellationToken cancellationToken);
    Task<OnboardingAccess?> AuthorizeAsync(string installId, OnboardingAttestation evidence,
        OnboardingInput input, string clientIp, CancellationToken cancellationToken);
}

public interface IOnboardingSuggestionService
{
    Task<HabitSetupSuggestion?> SuggestAsync(OnboardingInput input, CancellationToken cancellationToken);
}

