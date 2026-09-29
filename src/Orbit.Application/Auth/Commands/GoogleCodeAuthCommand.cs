using MediatR;
using Orbit.Application.Auth.Queries;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Commands;

public record GoogleCodeAuthCommand(
    string Code,
    string CodeVerifier,
    string RedirectUri,
    string Language = "en",
    string? ReferralCode = null,
    bool PersistGoogleTokens = true) : IRequest<Result<LoginResponse>>;

public sealed class GoogleCodeAuthCommandHandler(
    IGoogleAuthorizationCodeService authorizationCodeService,
    IMediator mediator) : IRequestHandler<GoogleCodeAuthCommand, Result<LoginResponse>>
{
    public async Task<Result<LoginResponse>> Handle(GoogleCodeAuthCommand request, CancellationToken cancellationToken)
    {
        var identity = await authorizationCodeService.ExchangeAsync(
            request.Code, request.CodeVerifier, request.RedirectUri, cancellationToken);
        if (identity.IsFailure)
            return identity.PropagateError<LoginResponse>();

        return await mediator.Send(new CompleteGoogleSignInCommand(
            identity.Value.Email,
            identity.Value.Name,
            request.Language,
            request.ReferralCode,
            request.PersistGoogleTokens ? identity.Value.AccessToken : null,
            request.PersistGoogleTokens ? identity.Value.RefreshToken : null), cancellationToken);
    }
}
