using Google.Apis.Auth.OAuth2;
using Google.Apis.PlayIntegrity.v1;
using Google.Apis.PlayIntegrity.v1.Data;
using Google.Apis.Services;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Services;

public sealed class PlayIntegrityOnboardingVerifier(IOptions<OnboardingSecuritySettings> options, TimeProvider clock)
    : IOnboardingAttestationVerifier
{
    public bool Supports(string platform) => platform == "android";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.Value.PlayIntegrityCredentialsJson)
        && !string.IsNullOrWhiteSpace(options.Value.AndroidPackageName)
        && options.Value.AndroidCertificateDigests.Length > 0;

    public async Task<VerifiedOnboardingAttestation?> VerifyAsync(OnboardingAttestation evidence,
        string challenge, string? publicKey, uint counter, CancellationToken cancellationToken)
    {
        if (!IsConfigured || !Supports(evidence.Platform) || string.IsNullOrWhiteSpace(evidence.Token))
            return null;
        var credential = CredentialFactory.FromJson<ServiceAccountCredential>(options.Value.PlayIntegrityCredentialsJson)
            .ToGoogleCredential().CreateScoped(PlayIntegrityService.Scope.Playintegrity);
        using var service = new PlayIntegrityService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Orbit onboarding"
        });
        var response = await service.V1.DecodeIntegrityToken(new DecodeIntegrityTokenRequest
        {
            IntegrityToken = evidence.Token
        }, options.Value.AndroidPackageName).ExecuteAsync(cancellationToken);
        return IsValid(response.TokenPayloadExternal, options.Value, OnboardingAccessService.ChallengeHash(challenge), clock.GetUtcNow())
            ? new VerifiedOnboardingAttestation(null, 0) : null;
    }

    internal static bool IsValid(TokenPayloadExternal? payload, OnboardingSecuritySettings settings,
        string requestHash, DateTimeOffset now)
    {
        var details = payload?.RequestDetails;
        var app = payload?.AppIntegrity;
        var age = details?.TimestampMillis is { } timestamp ? now.ToUnixTimeMilliseconds() - timestamp : long.MaxValue;
        return details?.RequestPackageName == settings.AndroidPackageName
            && details.RequestHash == requestHash && age is >= 0 and <= 300_000
            && app?.AppRecognitionVerdict == "PLAY_RECOGNIZED"
            && app.PackageName == settings.AndroidPackageName
            && app.CertificateSha256Digest is { Count: > 0 } digests
            && digests.All(digest => settings.AndroidCertificateDigests.Contains(digest, StringComparer.Ordinal))
            && payload?.DeviceIntegrity?.DeviceRecognitionVerdict?.Contains("MEETS_DEVICE_INTEGRITY") == true
            && payload.AccountDetails?.AppLicensingVerdict == "LICENSED"
            && payload.TestingDetails?.IsTestingResponse != true;
    }
}
