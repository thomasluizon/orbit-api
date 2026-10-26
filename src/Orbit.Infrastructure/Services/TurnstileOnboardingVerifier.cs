using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Services;

public sealed class TurnstileOnboardingVerifier(ITurnstileVerificationService turnstile,
    IOptions<BotProtectionSettings> botOptions, IOptions<OnboardingSecuritySettings> options)
    : IOnboardingAttestationVerifier
{
    public bool Supports(string platform) => platform is "web" or "desktop";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(botOptions.Value.SecretKey)
        && !botOptions.Value.SecretKey.StartsWith("1x", StringComparison.Ordinal)
        && !botOptions.Value.SecretKey.StartsWith("2x", StringComparison.Ordinal)
        && !botOptions.Value.SecretKey.StartsWith("3x", StringComparison.Ordinal)
        && options.Value.TurnstileHostnames.Length > 0;

    public async Task<VerifiedOnboardingAttestation?> VerifyAsync(OnboardingAttestation evidence,
        string challenge, string? publicKey, uint counter, CancellationToken cancellationToken)
    {
        if (!IsConfigured || !Supports(evidence.Platform) || string.IsNullOrWhiteSpace(evidence.Token)
            || evidence.Token.Length > 2048)
            return null;
        var result = await turnstile.VerifyAsync(evidence.Token, cancellationToken);
        return result.Success && result.Hostname is not null
            && options.Value.TurnstileHostnames.Contains(result.Hostname, StringComparer.OrdinalIgnoreCase)
            ? new VerifiedOnboardingAttestation(null, 0) : null;
    }
}
