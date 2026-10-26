using System.Diagnostics;
using MediatR;
using Microsoft.Extensions.Logging;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Onboarding.Commands;

public sealed record OnboardingSetupResult(HabitSetupSuggestion? Suggestion, string? InstallId, string? Code)
{
    public static OnboardingSetupResult Limited => new(null, null, "ONBOARDING_LIMIT");
    public static OnboardingSetupResult Unavailable => new(null, null, "ONBOARDING_UNAVAILABLE");
}

public sealed record SuggestOnboardingSetupCommand(string? Title, string? Language, string? InstallId,
    OnboardingAttestation? Attestation, string ClientIp) : IRequest<OnboardingSetupResult>;

public sealed class SuggestOnboardingSetupCommandHandler(IOnboardingAccessService access,
    IContentModerationService moderation, IOnboardingSuggestionService suggestions,
    ILogger<SuggestOnboardingSetupCommandHandler> logger)
    : IRequestHandler<SuggestOnboardingSetupCommand, OnboardingSetupResult>
{
    public async Task<OnboardingSetupResult> Handle(SuggestOnboardingSetupCommand request, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = OnboardingSetupResult.Unavailable;
        var calls = 0;
        try
        {
            var input = OnboardingInput.Create(request.Title, request.Language);
            if (input.IsFailure)
                return result;
            if (request.Attestation is null || string.IsNullOrWhiteSpace(request.InstallId))
                return result = OnboardingSetupResult.Limited;
            var authorization = await access.AuthorizeAsync(request.InstallId, request.Attestation, input.Value,
                request.ClientIp, cancellationToken);
            if (authorization is null)
                return result = OnboardingSetupResult.Limited;
            var checkedInput = await moderation.CheckTextAsync(input.Value.Title, cancellationToken);
            if (checkedInput.Flagged || checkedInput.Unavailable)
                return result;
            calls = 1;
            var suggestion = await suggestions.SuggestAsync(input.Value, cancellationToken);
            if (suggestion is null || !HabitSetupSuggestionGuard.IsValid(suggestion))
                return result;
            return result = new OnboardingSetupResult(suggestion, authorization.InstallId, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return result;
        }
        finally
        {
            logger.LogDebug("Onboarding outcome {OutcomeCode} calls {CallCount} latency {LatencyMs}",
                result.Code ?? "SUCCESS", calls, stopwatch.ElapsedMilliseconds);
        }
    }
}
