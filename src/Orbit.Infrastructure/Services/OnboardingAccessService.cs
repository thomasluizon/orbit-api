using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Services;

public sealed class OnboardingAccessService(
    IOptions<OnboardingSecuritySettings> options,
    IEnumerable<IOnboardingAttestationVerifier> verifiers,
    IDistributedRateLimitService limiter,
    IDistributedQuotaService quotas,
    IAppConfigService config,
    TimeProvider clock) : IOnboardingAccessService
{
    public const string IpHourlyLimit = "OnboardingIpCallsPerHour";
    public const string InstallHourlyLimit = "OnboardingInstallCallsPerHour";
    public const string DailyCallLimit = "OnboardingCallsPerDay";
    public const string DailyTokenLimit = "OnboardingTokensPerDay";
    public const int ReservedTokensPerCall = 16384;
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan InstallLifetime = TimeSpan.FromHours(1);
    private readonly OnboardingSecuritySettings settings = options.Value;

    public async Task<OnboardingChallenge?> ChallengeAsync(string platform, string clientIp, CancellationToken cancellationToken)
    {
        if (!Configured(platform) || !await quotas.TryAcquireAsync("onboarding-enroll", Hash(clientIp),
                10, 1, TimeSpan.FromHours(1), cancellationToken))
            return null;
        var expires = clock.GetUtcNow().Add(ChallengeLifetime);
        return new OnboardingChallenge(Sign(new Credential("challenge", platform, RandomId(), RandomId(),
            expires.ToUnixTimeSeconds(), Hash(clientIp), null, 0)), expires);
    }

    public async Task<OnboardingInstall?> EnrollAsync(string challenge, OnboardingAttestation evidence,
        string clientIp, CancellationToken cancellationToken)
    {
        var credential = Read(challenge, "challenge", evidence.Platform, clientIp);
        if (credential is null || !await ConsumeAsync(challenge, credential, cancellationToken))
            return null;
        var proof = await VerifyAsync(evidence, challenge, null, 0, cancellationToken);
        if (proof is null)
            return null;
        var expires = clock.GetUtcNow().Add(InstallLifetime);
        var install = credential with
        {
            Kind = "install",
            Nonce = RandomId(),
            Expires = expires.ToUnixTimeSeconds(),
            PublicKey = proof.PublicKey,
            Counter = proof.Counter
        };
        return new OnboardingInstall(Sign(install), expires);
    }

    public async Task<OnboardingAccess?> AuthorizeAsync(string installId, OnboardingAttestation evidence,
        OnboardingInput input, string clientIp, CancellationToken cancellationToken)
    {
        var credential = Read(installId, "install", evidence.Platform, clientIp);
        if (credential is null || !await ConsumeAsync(installId, credential, cancellationToken))
            return null;

        var proof = await VerifyAsync(evidence, BindRequest(installId, input), credential.PublicKey,
            credential.Counter, cancellationToken);
        if (proof is null)
            return null;

        var ipLimit = await config.GetAsync(IpHourlyLimit, 0, cancellationToken);
        var installLimit = await config.GetAsync(InstallHourlyLimit, 0, cancellationToken);
        var calls = await config.GetAsync(DailyCallLimit, 0, cancellationToken);
        var tokens = await config.GetAsync(DailyTokenLimit, 0, cancellationToken);
        if (ipLimit <= 0 || installLimit <= 0 || calls <= 0 || tokens < ReservedTokensPerCall)
            return null;

        if (!await quotas.TryAcquireAsync("onboarding-ip", credential.IpHash, Math.Min(ipLimit, 10), 1,
                TimeSpan.FromHours(1), cancellationToken)
            || !await quotas.TryAcquireAsync("onboarding-install", Hash(credential.Subject), installLimit, 1,
                TimeSpan.FromHours(1), cancellationToken)
            || !await quotas.TryAcquireAsync("onboarding-calls", "global", calls, 1,
                TimeSpan.FromDays(1), cancellationToken)
            || !await quotas.TryAcquireAsync("onboarding-tokens", "global", tokens, ReservedTokensPerCall,
                TimeSpan.FromDays(1), cancellationToken))
            return null;

        var next = credential with { Nonce = RandomId(), Counter = proof.Counter };
        return new OnboardingAccess(Sign(next));
    }

    public static string BindRequest(string installId, OnboardingInput input)
        => $"orbit-onboarding-setup-v1\n{installId}\n{Convert.ToBase64String(Encoding.UTF8.GetBytes(input.Title))}\n{input.Language}";

    public static string ChallengeHash(string challenge)
        => Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(challenge)));

    private bool Configured(string platform)
        => Encoding.UTF8.GetByteCount(settings.SigningSecret) >= 32
           && verifiers.Any(verifier => verifier.Supports(platform) && verifier.IsConfigured);

    private async Task<VerifiedOnboardingAttestation?> VerifyAsync(OnboardingAttestation evidence,
        string challenge, string? publicKey, uint counter, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(evidence.Token) || evidence.Token.Length > 32768)
            return null;
        var verifier = verifiers.SingleOrDefault(verifier => verifier.Supports(evidence.Platform));
        return verifier is { IsConfigured: true }
            ? await verifier.VerifyAsync(evidence, challenge, publicKey, counter, cancellationToken)
            : null;
    }

    private async Task<bool> ConsumeAsync(string token, Credential credential, CancellationToken cancellationToken)
    {
        var remaining = DateTimeOffset.FromUnixTimeSeconds(credential.Expires) - clock.GetUtcNow();
        return remaining > TimeSpan.Zero && (await limiter.TryAcquireLeaseAsync("onboarding-replay", Hash(token),
            1, remaining.Add(TimeSpan.FromMinutes(1)), cancellationToken)).Allowed;
    }

    private Credential? Read(string token, string kind, string platform, string clientIp)
    {
        if (!Configured(platform) || string.IsNullOrWhiteSpace(token) || token.Length > 4096)
            return null;
        var parts = token.Split('.');
        if (parts.Length != 2)
            return null;
        try
        {
            var payload = Decode(parts[0]);
            var signature = Decode(parts[1]);
            if (parts[0] != Base64Url(payload) || parts[1] != Base64Url(signature)
                || !CryptographicOperations.FixedTimeEquals(signature, HMACSHA256.HashData(
                    Encoding.UTF8.GetBytes(settings.SigningSecret), payload)))
                return null;
            var credential = JsonSerializer.Deserialize<Credential>(payload);
            var now = clock.GetUtcNow().ToUnixTimeSeconds();
            return credential is not null && credential.Kind == kind && credential.Platform == platform
                && credential.IpHash == Hash(clientIp) && credential.Expires > now
                && credential.Expires <= now + (long)InstallLifetime.TotalSeconds
                && credential.Subject.Length == 32 && credential.Nonce.Length == 32
                ? credential : null;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return null;
        }
    }

    private string Sign(Credential credential)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(credential);
        return $"{Base64Url(payload)}.{Base64Url(HMACSHA256.HashData(Encoding.UTF8.GetBytes(settings.SigningSecret), payload))}";
    }

    private string Hash(string value)
        => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(settings.SigningSecret), Encoding.UTF8.GetBytes(value)));
    private static string RandomId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '='));

    private sealed record Credential(string Kind, string Platform, string Subject, string Nonce,
        long Expires, string IpHash, string? PublicKey, uint Counter);
}
