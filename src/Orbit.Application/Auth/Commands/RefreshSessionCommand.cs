using MediatR;
using Orbit.Application.Auth.Models;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Commands;

public record RefreshSessionCommand(string RefreshToken) : IRequest<Result<RefreshSessionResponse>>;

public class RefreshSessionCommandHandler(IAuthSessionService authSessionService)
    : IRequestHandler<RefreshSessionCommand, Result<RefreshSessionResponse>>
{
    public async Task<Result<RefreshSessionResponse>> Handle(RefreshSessionCommand request, CancellationToken cancellationToken)
    {
        var result = await authSessionService.RefreshSessionAsync(request.RefreshToken, cancellationToken);
        if (result.IsFailure)
            return Result.Failure<RefreshSessionResponse>(result.Error, result.ErrorCode!);

        return Result.Success(new RefreshSessionResponse(
            result.Value.AccessToken,
            result.Value.RefreshToken));
    }
}
