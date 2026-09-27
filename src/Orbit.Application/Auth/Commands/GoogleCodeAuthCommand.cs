using MediatR;
using Orbit.Application.Auth.Queries;
using Orbit.Application.Behaviors;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Commands;

public record GoogleCodeAuthCommand(
    string Code,
    string CodeVerifier,
    string RedirectUri,
    string Language = "en",
    string? ReferralCode = null) : IRequest<Result<LoginResponse>>, IConcurrencyRetryable;

public sealed class GoogleCodeAuthCommandHandler(
    IGoogleAuthorizationCodeService authorizationCodeService,
    GoogleSignInFlow signInFlow) : IRequestHandler<GoogleCodeAuthCommand, Result<LoginResponse>>
{
    public async Task<Result<LoginResponse>> Handle(GoogleCodeAuthCommand request, CancellationToken cancellationToken)
    {
        var identity = await authorizationCodeService.ExchangeAsync(
            request.Code, request.CodeVerifier, request.RedirectUri, cancellationToken);
        if (identity.IsFailure)
            return identity.PropagateError<LoginResponse>();

        return await signInFlow.CompleteAsync(
            identity.Value.Email,
            identity.Value.Name,
            request.Language,
            request.ReferralCode,
            identity.Value.AccessToken,
            identity.Value.RefreshToken,
            cancellationToken);
    }
}
