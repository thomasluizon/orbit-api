using MediatR;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Onboarding.Commands;

public sealed record GetOnboardingChallengeCommand(string Platform, string ClientIp) : IRequest<OnboardingChallenge?>;
public sealed record EnrollOnboardingInstallCommand(string Challenge, OnboardingAttestation Attestation,
    string ClientIp) : IRequest<OnboardingInstall?>;

public sealed class GetOnboardingChallengeCommandHandler(IOnboardingAccessService access)
    : IRequestHandler<GetOnboardingChallengeCommand, OnboardingChallenge?>
{
    public Task<OnboardingChallenge?> Handle(GetOnboardingChallengeCommand request, CancellationToken cancellationToken)
        => access.ChallengeAsync(request.Platform, request.ClientIp, cancellationToken);
}

public sealed class EnrollOnboardingInstallCommandHandler(IOnboardingAccessService access)
    : IRequestHandler<EnrollOnboardingInstallCommand, OnboardingInstall?>
{
    public Task<OnboardingInstall?> Handle(EnrollOnboardingInstallCommand request, CancellationToken cancellationToken)
        => access.EnrollAsync(request.Challenge, request.Attestation, request.ClientIp, cancellationToken);
}
