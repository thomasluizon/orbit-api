using Asp.Versioning;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Api.RateLimiting;
using Orbit.Application.Onboarding.Commands;
using Orbit.Domain.Models;

namespace Orbit.Api.Controllers;

[ApiController]
[ApiVersion(1.0)]
[ApiVersion(2.0)]
[Route("api/onboarding")]
[AllowAnonymous]
[EnableCors("Onboarding")]
[TypeFilter(typeof(OnboardingEndpointFilter))]
[RequestSizeLimit(49152)]
public sealed class OnboardingController(ISender sender) : ControllerBase
{
    public const string InstallHeader = "X-Orbit-Install-Id";
    public sealed record FallbackResponse(string Code);
    public sealed record ChallengeRequest(string Platform);
    public sealed record EnrollmentRequest(string Challenge, OnboardingAttestation Attestation);
    public sealed record SuggestSetupRequest(string? Title, string? Language = "en", OnboardingAttestation? Attestation = null);

    [HttpPost("challenge")]
    [ProducesResponseType<OnboardingChallenge>(StatusCodes.Status200OK)]
    [ProducesResponseType<FallbackResponse>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Challenge([FromBody] ChallengeRequest request, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new GetOnboardingChallengeCommand(request.Platform,
            HttpContext.GetClientIpAddress() ?? "unknown"), cancellationToken);
        return result is null ? StatusCode(429, new FallbackResponse("ONBOARDING_LIMIT")) : Ok(result);
    }

    [HttpPost("install")]
    [ProducesResponseType<OnboardingInstall>(StatusCodes.Status200OK)]
    [ProducesResponseType<FallbackResponse>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Enroll([FromBody] EnrollmentRequest request, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new EnrollOnboardingInstallCommand(request.Challenge, request.Attestation,
            HttpContext.GetClientIpAddress() ?? "unknown"), cancellationToken);
        return result is null ? StatusCode(429, new FallbackResponse("ONBOARDING_LIMIT")) : Ok(result);
    }

    [HttpPost("suggest-setup")]
    [ProducesResponseType<HabitSetupSuggestion>(StatusCodes.Status200OK)]
    [ProducesResponseType<FallbackResponse>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<FallbackResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> SuggestSetup([FromBody] SuggestSetupRequest request,
        [FromHeader(Name = InstallHeader)] string? installId, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new SuggestOnboardingSetupCommand(request.Title, request.Language,
            installId, request.Attestation, HttpContext.GetClientIpAddress() ?? "unknown"), cancellationToken);
        if (result.Suggestion is not null)
        {
            Response.Headers[InstallHeader] = result.InstallId;
            return Ok(result.Suggestion);
        }
        return StatusCode(result.Code == "ONBOARDING_LIMIT" ? 429 : 503,
            new FallbackResponse(result.Code ?? "ONBOARDING_UNAVAILABLE"));
    }
}
