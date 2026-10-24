using System.Security.Cryptography;
using System.Text;
using MediatR;
using Microsoft.Extensions.Options;
using Orbit.Application.Auth.Models;
using Orbit.Application.Auth.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Commands;

public sealed record ExchangeDesktopHandoffCommand(string Code, string State, string CodeVerifier, string RedirectUri)
    : IRequest<Result<LoginResponse>>;

public sealed class ExchangeDesktopHandoffCommandHandler(
    IDesktopHandoffStore handoffs, IGenericRepository<User> users, IAuthSessionService sessions,
    IUnitOfWork work, IOptions<DesktopAuthOptions> options, TimeProvider timeProvider)
    : IRequestHandler<ExchangeDesktopHandoffCommand, Result<LoginResponse>>
{
    public async Task<Result<LoginResponse>> Handle(ExchangeDesktopHandoffCommand request, CancellationToken cancellationToken)
    {
        if (!DesktopHandoff.IsValidCode(request.Code) || !DesktopHandoff.IsValidState(request.State)
            || !DesktopHandoff.IsValidVerifier(request.CodeVerifier)
            || !DesktopHandoff.IsAllowedRedirect(request.RedirectUri, options.Value.RedirectUri))
            return Result.Failure<LoginResponse>(DesktopHandoff.InvalidHandoff);

        return await work.ExecuteInTransactionAsync(async token =>
        {
            var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(request.CodeVerifier)))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var userId = await handoffs.ConsumeAsync(CreateDesktopHandoffCommandHandler.HashCode(request.Code),
                request.State, challenge, request.RedirectUri, timeProvider.GetUtcNow().UtcDateTime, token);
            if (userId is null)
                return Result.Failure<LoginResponse>(DesktopHandoff.InvalidHandoff);

            var user = await users.GetByIdAsync(userId.Value, token);
            if (user is null || user.IsDeactivated)
                return Result.Failure<LoginResponse>(DesktopHandoff.InvalidHandoff);

            var session = await sessions.CreateSessionAsync(user.Id, user.Email, token);
            if (session.IsFailure)
                return Result.Failure<LoginResponse>(DesktopHandoff.InvalidHandoff);

            return Result.Success(new LoginResponse(user.Id, session.Value.AccessToken, user.Name, user.Email,
                RefreshToken: session.Value.RefreshToken));
        }, cancellationToken);
    }
}
