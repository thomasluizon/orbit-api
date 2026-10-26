namespace Orbit.Domain.Models;

public sealed record OnboardingAttestation(string Platform, string Token, string? KeyId = null);

public sealed record VerifiedOnboardingAttestation(string? PublicKey, uint Counter);

public sealed record OnboardingChallenge(string Challenge, DateTimeOffset ExpiresAtUtc);

public sealed record OnboardingInstall(string InstallId, DateTimeOffset ExpiresAtUtc);

public sealed record OnboardingAccess(string InstallId);
