using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Common;
using Orbit.Domain.Common;

namespace Orbit.Application.Auth.Services;

public enum EmailChallengeOperation
{
    AccountDeletion,
    ApiKeyCreation,
}

public readonly record struct EmailChallengeConfirmation(TimeSpan RemainingLifetime);

public sealed class EmailChallengeService(IMemoryCache cache, TimeProvider timeProvider)
{
    private const int LockStripeCount = 256;
    private static readonly TimeSpan ChallengeTtl = TimeSpan.FromMinutes(AppConstants.SensitiveOperationChallengeTtlMinutes);
    private static readonly TimeSpan RequestCooldown = TimeSpan.FromSeconds(AppConstants.SensitiveOperationChallengeCooldownSeconds);
    private readonly object[] _challengeLocks = Enumerable.Range(0, LockStripeCount)
        .Select(_ => new object())
        .ToArray();

    public Result<string> Issue(EmailChallengeOperation operation, string email)
    {
        var normalizedEmail = email.ToLowerInvariant();
        var cacheKey = ChallengeCacheKey(operation, normalizedEmail);
        lock (ChallengeLock(cacheKey))
        {
            var nowAtUtc = timeProvider.GetUtcNow().UtcDateTime;

            if (cache.TryGetValue(cacheKey, out VerificationEntry? existing) &&
                existing is not null &&
                nowAtUtc - existing.CreatedAt < RequestCooldown)
            {
                return Result.Failure<string>(ErrorMessages.CodeRequestCooldown);
            }

            var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
            cache.Set(cacheKey, new VerificationEntry(code, 0, nowAtUtc), new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = ChallengeTtl,
            });

            return Result.Success(code);
        }
    }

    public Result<EmailChallengeConfirmation> Confirm(
        EmailChallengeOperation operation,
        string email,
        string code)
    {
        var normalizedEmail = email.ToLowerInvariant();
        var cacheKey = ChallengeCacheKey(operation, normalizedEmail);
        lock (ChallengeLock(cacheKey))
        {
            if (CountFailedAttempts(operation, normalizedEmail) >= AppConstants.MaxVerificationAttempts)
                return Result.Failure<EmailChallengeConfirmation>(ErrorMessages.TooManyCodeAttempts);

            if (!cache.TryGetValue(cacheKey, out VerificationEntry? entry) || entry is null)
                return Result.Failure<EmailChallengeConfirmation>(ExpiredError(operation));

            var remainingLifetime = ChallengeTtl - (timeProvider.GetUtcNow().UtcDateTime - entry.CreatedAt);
            if (remainingLifetime <= TimeSpan.Zero)
            {
                cache.Remove(cacheKey);
                return Result.Failure<EmailChallengeConfirmation>(ExpiredError(operation));
            }

            if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(entry.Code),
                Encoding.UTF8.GetBytes(code)))
            {
                var attempts = RecordFailedAttempt(operation, normalizedEmail);
                var attemptsRemaining = Math.Max(0, AppConstants.MaxVerificationAttempts - attempts);
                return Result.Failure<EmailChallengeConfirmation>(InvalidCodeError(operation, attemptsRemaining));
            }

            cache.Remove(cacheKey);
            return Result.Success(new EmailChallengeConfirmation(remainingLifetime));
        }
    }

    public void AuthorizeOnce(
        EmailChallengeOperation operation,
        Guid userId,
        TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero)
            return;

        cache.Set(GrantCacheKey(operation, userId), new OneTimeGrant(), new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = lifetime,
        });
    }

    public bool TryConsumeAuthorization(EmailChallengeOperation operation, Guid userId)
    {
        var cacheKey = GrantCacheKey(operation, userId);
        if (!cache.TryGetValue(cacheKey, out OneTimeGrant? grant) || grant is null)
            return false;

        cache.Remove(cacheKey);
        return grant.TrySpend();
    }

    public bool HasAuthorization(EmailChallengeOperation operation, Guid userId) =>
        cache.TryGetValue(GrantCacheKey(operation, userId), out OneTimeGrant? grant) &&
        grant is not null &&
        !grant.IsSpent;

    private int CountFailedAttempts(EmailChallengeOperation operation, string email) =>
        cache.TryGetValue(FailedAttemptCacheKey(operation, email), out int attempts) ? attempts : 0;

    private int RecordFailedAttempt(EmailChallengeOperation operation, string email)
    {
        var attempts = CountFailedAttempts(operation, email) + 1;
        cache.Set(FailedAttemptCacheKey(operation, email), attempts, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(AppConstants.VerificationAttemptWindowMinutes),
        });
        return attempts;
    }

    private static AppError ExpiredError(EmailChallengeOperation operation) => operation switch
    {
        EmailChallengeOperation.AccountDeletion => ErrorMessages.DeletionCodeExpired,
        EmailChallengeOperation.ApiKeyCreation => ErrorMessages.ApiKeyCreationCodeExpired,
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private static AppError InvalidCodeError(EmailChallengeOperation operation, int attemptsRemaining) => operation switch
    {
        EmailChallengeOperation.AccountDeletion => ErrorMessages.InvalidDeletionCode,
        EmailChallengeOperation.ApiKeyCreation => ErrorMessages.InvalidApiKeyCreationCode.Format(attemptsRemaining),
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private static string ChallengeCacheKey(EmailChallengeOperation operation, string email) => operation switch
    {
        EmailChallengeOperation.AccountDeletion => $"delete:{email}",
        EmailChallengeOperation.ApiKeyCreation => $"api-key-create:{email}",
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private static string FailedAttemptCacheKey(EmailChallengeOperation operation, string email) => operation switch
    {
        EmailChallengeOperation.AccountDeletion => $"delete-attempts:{email}",
        EmailChallengeOperation.ApiKeyCreation => $"api-key-create-attempts:{email}",
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private static string GrantCacheKey(EmailChallengeOperation operation, Guid userId) =>
        $"email-challenge-grant:{operation}:{userId}";

    private object ChallengeLock(string cacheKey)
    {
        var stripe = (uint)StringComparer.Ordinal.GetHashCode(cacheKey) % LockStripeCount;
        return _challengeLocks[(int)stripe];
    }

    private sealed class OneTimeGrant
    {
        private int _spent;

        public bool IsSpent => Volatile.Read(ref _spent) != 0;

        public bool TrySpend() => Interlocked.Exchange(ref _spent, 1) == 0;
    }
}
