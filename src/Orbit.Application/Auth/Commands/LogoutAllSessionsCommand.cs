using MediatR;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Commands;

public record LogoutAllSessionsCommand(Guid UserId) : IRequest<Result>;

public class LogoutAllSessionsCommandHandler(IAuthSessionService authSessionService)
    : IRequestHandler<LogoutAllSessionsCommand, Result>
{
    public Task<Result> Handle(LogoutAllSessionsCommand request, CancellationToken cancellationToken)
    {
        return authSessionService.RevokeAllSessionsAsync(request.UserId, cancellationToken);
    }
}
