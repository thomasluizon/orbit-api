using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
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
    IGenericRepository<UserSessionRefreshToken> refreshTokenRepository,
    IGenericRepository<User> userRepository,
    ITokenService tokenService,
    IUnitOfWork unitOfWork,
    IOptions<JwtSettings> jwtSettings) : IAuthSessionService
{
    private const int MaxRevokeAllAttempts = 3;
    private const int MaxRevokeSessionAttempts = 3;

    private readonly JwtSettings _jwtSettings = jwtSettings.Value;

    private DateTime? GetRefreshExpiry(DateTime nowUtc) =>
        _jwtSettings.RefreshExpiryDays.HasValue
            ? nowUtc.AddDays(_jwtSettings.RefreshExpiryDays.Value)
            : null;

    public async Task<Result<SessionTokens>> CreateSessionAsync(Guid userId, string email, CancellationToken cancellationToken = default)
    {
        var refreshToken = GenerateRefreshToken();
#pragma warning disable ORBIT0004
        var nowUtc = DateTime.UtcNow;
#pragma warning restore ORBIT0004
        var sessionResult = UserSession.Create(
            userId,
            HashToken(refreshToken),
            GetRefreshExpiry(nowUtc));

        if (sessionResult.IsFailure)
            return Result.Failure<SessionTokens>(sessionResult.Error, ErrorCodes.SessionCreationFailed);

        await userSessionRepository.AddAsync(sessionResult.Value, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(new SessionTokens(
            tokenService.GenerateToken(userId, email, sessionResult.Value.Id),
            refreshToken));
    }

    public async Task<Result<SessionTokens>> RefreshSessionAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
#pragma warning disable ORBIT0004
        var nowUtc = DateTime.UtcNow;
#pragma warning restore ORBIT0004
        var tokenHash = HashToken(refreshToken);

        var session = await userSessionRepository.FindOneTrackedAsync(
            s => s.TokenHash == tokenHash,
            cancellationToken: cancellationToken);

        if (session is null || !session.CanUse(nowUtc))
            return Result.Failure<SessionTokens>(ErrorMessages.InvalidSession);

        var user = await userRepository.GetByIdAsync(session.UserId, cancellationToken);
        if (user is null)
            return Result.Failure<SessionTokens>(ErrorMessages.InvalidSession);

        var newRefreshToken = GenerateRefreshToken();
        var previousToken = UserSessionRefreshToken.Create(session.Id, tokenHash).Value;
        var rotateResult = session.Rotate(
            HashToken(newRefreshToken),
            GetRefreshExpiry(nowUtc),
            nowUtc);

        if (rotateResult.IsFailure)
            return Result.Failure<SessionTokens>(ErrorMessages.InvalidSession);

        try
        {
            await refreshTokenRepository.AddAsync(previousToken, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            unitOfWork.DiscardChanges();
            return Result.Failure<SessionTokens>(ErrorMessages.InvalidSession);
        }
        catch (DbUpdateException exception) when (DbUniqueViolation.IsUniqueViolation(exception))
        {
            unitOfWork.DiscardChanges();
            return Result.Failure<SessionTokens>(ErrorMessages.InvalidSession);
        }

        return Result.Success(new SessionTokens(
            tokenService.GenerateToken(user.Id, user.Email, session.Id),
            newRefreshToken));
    }

    public async Task<Result> RevokeSessionAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        var tokenHash = HashToken(refreshToken);
        for (var attempt = 0; attempt < MaxRevokeSessionAttempts; attempt++)
        {
            var session = await userSessionRepository.FindOneTrackedAsync(
                s => s.TokenHash == tokenHash,
                cancellationToken: cancellationToken);

            if (session is null)
            {
                var previousToken = await refreshTokenRepository.FindOneTrackedAsync(
                    token => token.TokenHash == tokenHash,
                    cancellationToken: cancellationToken);
                if (previousToken is not null)
                    session = await userSessionRepository.GetByIdAsync(previousToken.UserSessionId, cancellationToken);
            }

            if (session is null)
                return Result.Failure(ErrorMessages.InvalidSession);

#pragma warning disable ORBIT0004
            session.Revoke(DateTime.UtcNow);
#pragma warning restore ORBIT0004
            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return Result.Success();
            }
            catch (DbUpdateConcurrencyException)
            {
                unitOfWork.ResetTracking();
            }
        }

        return Result.Failure(ErrorMessages.InvalidSession);
    }

    public async Task<Result> RevokeAllSessionsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
            return Result.Failure(DomainErrors.UserIdRequired);

#pragma warning disable ORBIT0004
        var nowUtc = DateTime.UtcNow;
#pragma warning restore ORBIT0004

        for (var attempt = 0; attempt < MaxRevokeAllAttempts; attempt++)
        {
            var activeSessions = await userSessionRepository.FindTrackedAsync(
                s => s.UserId == userId && s.RevokedAtUtc == null,
                cancellationToken);

            if (activeSessions.Count == 0)
                return Result.Success();

            foreach (var session in activeSessions)
                session.Revoke(nowUtc);

            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return Result.Success();
            }
            catch (DbUpdateConcurrencyException)
            {
                unitOfWork.ResetTracking();
            }
        }

        return Result.Failure(ErrorMessages.InvalidSession);
    }

    public async Task<bool> HasSessionForTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
#pragma warning disable ORBIT0004
        var nowUtc = DateTime.UtcNow;
#pragma warning restore ORBIT0004
        var tokenHash = HashToken(refreshToken);
        return await userSessionRepository.AnyAsync(
            session => session.TokenHash == tokenHash
                && session.RevokedAtUtc == null
                && (session.ExpiresAtUtc == null || session.ExpiresAtUtc > nowUtc),
            cancellationToken);
    }

    public async Task<bool> IsSessionActiveAsync(Guid sessionId, Guid userId, CancellationToken cancellationToken = default)
    {
        var nowAtUtc = DateTime.UtcNow;
        return await userSessionRepository.AnyAsync(
            session => session.Id == sessionId
                && session.UserId == userId
                && session.RevokedAtUtc == null
                && (session.ExpiresAtUtc == null || session.ExpiresAtUtc > nowAtUtc),
            cancellationToken);
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
