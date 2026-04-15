using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Services;

public class AuthSessionService(
    IGenericRepository<UserSession> userSessionRepository,
    IGenericRepository<User> userRepository,
    ITokenService tokenService,
    IUnitOfWork unitOfWork,
    IOptions<JwtSettings> jwtSettings) : IAuthSessionService
{
    private readonly JwtSettings _jwtSettings = jwtSettings.Value;

    public async Task<Result<SessionTokens>> CreateSessionAsync(Guid userId, string email, CancellationToken cancellationToken = default)
    {
        var refreshToken = GenerateRefreshToken();
        var sessionResult = UserSession.Create(
            userId,
            HashToken(refreshToken),
            DateTime.UtcNow.AddDays(_jwtSettings.RefreshExpiryDays));

        if (sessionResult.IsFailure)
            return Result.Failure<SessionTokens>(sessionResult.Error, ErrorCodes.SessionCreationFailed);

        await userSessionRepository.AddAsync(sessionResult.Value, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(new SessionTokens(
            tokenService.GenerateToken(userId, email),
            refreshToken));
    }

    public async Task<Result<SessionTokens>> RefreshSessionAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        var nowUtc = DateTime.UtcNow;
        var tokenHash = HashToken(refreshToken);

        var session = await userSessionRepository.FindOneTrackedAsync(
            s => s.TokenHash == tokenHash,
            cancellationToken: cancellationToken);

        if (session is null || !session.CanUse(nowUtc))
            return Result.Failure<SessionTokens>(ErrorMessages.InvalidSession, ErrorCodes.InvalidSession);

        var user = await userRepository.GetByIdAsync(session.UserId, cancellationToken);
        if (user is null)
            return Result.Failure<SessionTokens>(ErrorMessages.InvalidSession, ErrorCodes.InvalidSession);

        var newRefreshToken = GenerateRefreshToken();
        session.Rotate(
            HashToken(newRefreshToken),
            nowUtc.AddDays(_jwtSettings.RefreshExpiryDays),
            nowUtc);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(new SessionTokens(
            tokenService.GenerateToken(user.Id, user.Email),
            newRefreshToken));
    }

    public async Task<Result> RevokeSessionAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        var tokenHash = HashToken(refreshToken);
        var session = await userSessionRepository.FindOneTrackedAsync(
            s => s.TokenHash == tokenHash,
            cancellationToken: cancellationToken);

        if (session is null)
            return Result.Failure(ErrorMessages.InvalidSession, ErrorCodes.InvalidSession);

        session.Revoke(DateTime.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }

    private static string GenerateRefreshToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(64));
    }

    private static string HashToken(string token)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }
}
