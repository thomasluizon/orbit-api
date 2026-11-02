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
    IOptions<JwtSettings> jwtSettings, AuthValidationCache? validationCache = null) : IAuthSessionService
{
    private const int MaxRevokeAllAttempts = 3;
    private const int MaxRevokeSessionAttempts = 3;

    private readonly JwtSettings _jwtSettings = jwtSettings.Value;

    private DateTime? GetRefreshExpiry(DateTime nowUtc) =>
        _jwtSettings.RefreshExpiryDays.HasValue
            ? nowUtc.AddDays(_jwtSettings.RefreshExpiryDays.Value)
            : null;

    public Task<Result<SessionTokens>> CreateSessionAsync(Guid userId, string email, CancellationToken cancellationToken = default)
        => unitOfWork.ExecuteInTransactionAsync(async token =>
    {
        await unitOfWork.AcquireUserRowLockAsync(userId, token);
        var owner = await userRepository.GetByIdAsync(userId, token);
        if (owner is null)
            return Result.Failure<SessionTokens>(ErrorMessages.InvalidSession);
        await userRepository.ReloadAsync(owner, token);
        if (owner.IsDeactivated)
            return Result.Failure<SessionTokens>(ErrorMessages.InvalidSession);
        var refreshToken = GenerateRefreshToken();
#pragma warning disable ORBIT0004 // CreateSessionAsync computes the refresh-token ExpiresAtUtc instant through GetRefreshExpiry; no calendar date is derived. https://github.com/thomasluizon/orbit-tickets/issues/230
        var nowUtc = DateTime.UtcNow;
#pragma warning restore ORBIT0004
        var sessionResult = UserSession.Create(
            userId,
            HashToken(refreshToken),
            GetRefreshExpiry(nowUtc));

        if (sessionResult.IsFailure)
            return Result.Failure<SessionTokens>(sessionResult.Error, ErrorCodes.SessionCreationFailed);

        await userSessionRepository.AddAsync(sessionResult.Value, token);
        await unitOfWork.SaveChangesAsync(token);

        return Result.Success(new SessionTokens(
            tokenService.GenerateToken(userId, email, sessionResult.Value.Id),
            refreshToken));
    }, cancellationToken);

    public async Task<Result<SessionTokens>> RefreshSessionAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
#pragma warning disable ORBIT0004 // RefreshSessionAsync uses nowUtc for CanUse validity, refresh expiry and Rotate audit stamps, all session instants. https://github.com/thomasluizon/orbit-tickets/issues/230
        var nowUtc = DateTime.UtcNow;
#pragma warning restore ORBIT0004
        var tokenHash = HashToken(refreshToken);

        var session = await userSessionRepository.FindOneTrackedAsync(
            s => s.TokenHash == tokenHash,
            cancellationToken: cancellationToken);

        if (session is null)
        {
            await RevokeSessionAsync(refreshToken, cancellationToken);
            return Result.Failure<SessionTokens>(ErrorMessages.InvalidSession);
        }

        if (!session.CanUse(nowUtc))
            return Result.Failure<SessionTokens>(ErrorMessages.InvalidSession);

        var user = await userRepository.GetByIdAsync(session.UserId, cancellationToken);
        if (user is null || user.IsDeactivated)
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
            await RevokeRotatedSessionAsync(session.Id, tokenHash, cancellationToken);
            return Result.Failure<SessionTokens>(ErrorMessages.InvalidSession);
        }
        catch (DbUpdateException exception) when (DbUniqueViolation.IsUniqueViolation(exception))
        {
            unitOfWork.DiscardChanges();
            await RevokeRotatedSessionAsync(session.Id, tokenHash, cancellationToken);
            return Result.Failure<SessionTokens>(ErrorMessages.InvalidSession);
        }

        return Result.Success(new SessionTokens(
            tokenService.GenerateToken(user.Id, user.Email, session.Id),
            newRefreshToken));
    }

    private async Task RevokeRotatedSessionAsync(Guid sessionId, string previousHash, CancellationToken cancellationToken)
    {
        unitOfWork.ResetTracking();
        for (var attempt = 0; attempt < MaxRevokeSessionAttempts; attempt++)
        {
            var current = await userSessionRepository.GetByIdAsync(sessionId, cancellationToken);
            if (current is null || current.TokenHash == previousHash || current.RevokedAtUtc is not null)
                return;
            current.Revoke(DateTime.UtcNow);
            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                validationCache?.InvalidateUser(current.UserId);
                return;
            }
            catch (DbUpdateConcurrencyException)
            {
                unitOfWork.ResetTracking();
            }
        }
    }

    public async Task<Result> RevokeSessionAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        var tokenHash = HashToken(refreshToken);
        for (var attempt = 0; attempt < MaxRevokeSessionAttempts; attempt++)
        {
            var session = await userSessionRepository.FindOneTrackedAsync(
                s => s.TokenHash == tokenHash,
                cancellationToken: cancellationToken);

            var reused = session is null;
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

            session.Revoke(DateTime.UtcNow);
            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                validationCache?.InvalidateUser(session.UserId);
                return reused ? Result.Failure(ErrorMessages.InvalidSession) : Result.Success();
            }
            catch (DbUpdateConcurrencyException)
            {
                unitOfWork.ResetTracking();
            }
        }

        return Result.Failure(ErrorMessages.InvalidSession);
    }

    public Task<Result> RevokeAllSessionsAsync(Guid userId, CancellationToken cancellationToken = default)
        => unitOfWork.ExecuteInTransactionAsync(async token =>
    {
        if (userId == Guid.Empty)
            return Result.Failure(DomainErrors.UserIdRequired);

        await unitOfWork.AcquireUserRowLockAsync(userId, token);
        var nowUtc = DateTime.UtcNow;

        for (var attempt = 0; attempt < MaxRevokeAllAttempts; attempt++)
        {
            var activeSessions = await userSessionRepository.FindTrackedAsync(
                s => s.UserId == userId && s.RevokedAtUtc == null,
                token);

            if (activeSessions.Count == 0)
                return Result.Success();

            foreach (var session in activeSessions)
                session.Revoke(nowUtc);

            try
            {
                await unitOfWork.SaveChangesAsync(token);
                validationCache?.InvalidateUser(userId);
                return Result.Success();
            }
            catch (DbUpdateConcurrencyException)
            {
                unitOfWork.ResetTracking();
            }
        }

        return Result.Failure(ErrorMessages.InvalidSession);
    }, cancellationToken);

    public async Task<bool> HasSessionForTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
#pragma warning disable ORBIT0004 // HasSessionForTokenAsync compares ExpiresAtUtc with nowUtc to reject expired refresh tokens. https://github.com/thomasluizon/orbit-tickets/issues/230
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
        var cacheKey = ("active-session", sessionId, userId);
        if (validationCache?.TryGet<ActiveSession>(cacheKey, out _) == true)
            return true;
        var epoch = validationCache?.Epoch ?? 0;
        var nowAtUtc = DateTime.UtcNow;
        var active = await userSessionRepository.ProjectAsync(
            session => session.Id == sessionId
                && session.UserId == userId
                && session.User != null && !session.User.IsDeactivated
                && session.RevokedAtUtc == null
                && (session.ExpiresAtUtc == null || session.ExpiresAtUtc > nowAtUtc),
            query => query.Select(session => new ActiveSession(session.ExpiresAtUtc)), cancellationToken);
        if (active.Count == 1)
            validationCache?.Set(cacheKey, userId, active[0], epoch, session: true, active[0].ExpiresAtUtc);
        return active.Count == 1;
    }

    private sealed record ActiveSession(DateTime? ExpiresAtUtc);

    private static string GenerateRefreshToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(64));
    }

    private static string HashToken(string token)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }
}
