using MediatR;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Commands;

public record LogoutSessionCommand(string RefreshToken) : IRequest<Result>;

public class LogoutSessionCommandHandler(IAuthSessionService authSessionService)
    : IRequestHandler<LogoutSessionCommand, Result>
{
    public Task<Result> Handle(LogoutSessionCommand request, CancellationToken cancellationToken)
    {
        return authSessionService.RevokeSessionAsync(request.RefreshToken, cancellationToken);
    }
}
