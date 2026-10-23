using MediatR;
using Orbit.Application.Auth.Queries;
using Orbit.Application.Common;
using Orbit.Domain.Common;

namespace Orbit.Application.Auth.Commands;

public record AppleNativeSignInCommand(string IdentityToken, string AuthorizationCode, string RawNonce, string? GivenName,
    string? FamilyName, string Language = "en", string? ReferralCode = null) : IRequest<Result<LoginResponse>>;

public sealed class AppleNativeSignInCommandHandler(IAppleIdentityService apple, AppleSignInFlow signIn)
    : IRequestHandler<AppleNativeSignInCommand, Result<LoginResponse>>
{
    public async Task<Result<LoginResponse>> Handle(AppleNativeSignInCommand request, CancellationToken cancellationToken)
    {
        try
        {
            var identity = await apple.AuthenticateNativeAsync(request.IdentityToken, request.AuthorizationCode, request.RawNonce, cancellationToken);
            return await signIn.CompleteAsync(identity, string.Join(' ', new[] { request.GivenName, request.FamilyName }.Where(value => !string.IsNullOrWhiteSpace(value))),
                request.Language, request.ReferralCode, cancellationToken);
        }
        catch (AppleVerificationException)
        {
            return Result.Failure<LoginResponse>(ErrorMessages.InvalidSession);
        }
    }
}
