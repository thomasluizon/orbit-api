using Orbit.Application.Common;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Services;

namespace Orbit.Api.Extensions;

public static partial class ServiceCollectionExtensions
{
    internal static void AddOnboardingServices(WebApplicationBuilder builder)
    {
        builder.Services.Configure<OnboardingSecuritySettings>(builder.Configuration.GetSection(OnboardingSecuritySettings.SectionName));
        builder.Services.AddScoped<IOnboardingAccessService, OnboardingAccessService>();
        builder.Services.AddScoped<IOnboardingAttestationVerifier, TurnstileOnboardingVerifier>();
        builder.Services.AddScoped<IOnboardingAttestationVerifier, PlayIntegrityOnboardingVerifier>();
        builder.Services.AddScoped<IOnboardingAttestationVerifier, AppAttestOnboardingVerifier>();
        builder.Services.AddScoped<IOnboardingSuggestionService, OnboardingSuggestionService>();
        builder.Services.AddScoped<IDistributedQuotaService, DistributedRateLimitService>();
    }
}
