using System.Security.Cryptography;
using System.Text;
using MediatR;
using Microsoft.Extensions.Options;
using Orbit.Application.Auth.Models;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Commands;

public sealed record CreateDesktopHandoffCommand(Guid UserId, string State, string CodeChallenge,
    string CodeChallengeMethod, string RedirectUri) : IRequest<Result<DesktopHandoffResponse>>;

public sealed record DesktopHandoffResponse(string Code, int ExpiresIn);

public sealed class CreateDesktopHandoffCommandHandler(
    IDesktopHandoffStore handoffs, IGenericRepository<User> users, IUnitOfWork work,
    IOptions<DesktopAuthOptions> options, TimeProvider timeProvider)
    : IRequestHandler<CreateDesktopHandoffCommand, Result<DesktopHandoffResponse>>
{
    public async Task<Result<DesktopHandoffResponse>> Handle(CreateDesktopHandoffCommand request, CancellationToken cancellationToken)
    {
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var handoff = DesktopHandoff.Create(request.UserId, HashCode(code), request.State, request.CodeChallenge,
            request.CodeChallengeMethod, request.RedirectUri, options.Value.RedirectUri, timeProvider.GetUtcNow().UtcDateTime);
        if (handoff.IsFailure)
            return Result.Failure<DesktopHandoffResponse>(DesktopHandoff.InvalidHandoff);

        var user = await users.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null || user.IsDeactivated)
            return Result.Failure<DesktopHandoffResponse>(DesktopHandoff.InvalidHandoff);

        await handoffs.AddAsync(handoff.Value, cancellationToken);
        await work.SaveChangesAsync(cancellationToken);
        return Result.Success(new DesktopHandoffResponse(code, DesktopHandoff.LifetimeSeconds));
    }

    internal static string HashCode(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
}
