using MediatR;
using Orbit.Application.Auth.Queries;
using Orbit.Application.Behaviors;
using Orbit.Domain.Common;

namespace Orbit.Application.Auth.Commands;

public record CompleteGoogleSignInCommand(
    string Email,
    string Name,
    string Language,
    string? ReferralCode,
    string? GoogleAccessToken,
    string? GoogleRefreshToken) : IRequest<Result<LoginResponse>>, IConcurrencyRetryable;

public sealed class CompleteGoogleSignInCommandHandler(GoogleSignInFlow signInFlow)
    : IRequestHandler<CompleteGoogleSignInCommand, Result<LoginResponse>>
{
    public Task<Result<LoginResponse>> Handle(CompleteGoogleSignInCommand request, CancellationToken cancellationToken) =>
        signInFlow.CompleteAsync(request.Email, request.Name, request.Language, request.ReferralCode,
            request.GoogleAccessToken, request.GoogleRefreshToken, cancellationToken);
}
