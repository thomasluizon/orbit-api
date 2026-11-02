using FluentValidation;
using Orbit.Application.Auth.Validators;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbit.Application.Auth.Services;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public sealed class PostgresEmailChallengeStore(
    OrbitDbContext db, Orbit.Domain.Interfaces.IUnitOfWork unitOfWork,
    IOptions<JwtSettings> settings, TimeProvider clock) : IEmailChallengeStore
{
    private byte[] Hash(string operation, string subject, string code) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(settings.Value.SecretKey),
            Encoding.UTF8.GetBytes($"email-challenge:{operation}:{subject}:{code}"));

    public async Task<Result<string>> IssueAsync(EmailChallengeOperation operation, string email, string? fixedCode = null,
        TimeSpan? lifetime = null, CancellationToken cancellationToken = default)
    {
        await new EmailChallengeIssueValidator().ValidateAndThrowAsync(new(operation, email, fixedCode), cancellationToken);
        var subject = email.Trim().ToLowerInvariant();
        return await LockedAsync(operation.ToString(), subject, async () =>
        {
            var nowAtUtc = clock.GetUtcNow().UtcDateTime;
            var row = await FindAsync(operation.ToString(), subject, cancellationToken);
            if (row is not null && nowAtUtc - row.IssuedAtUtc < TimeSpan.FromSeconds(60))
                return Result.Failure<string>(ErrorMessages.CodeRequestCooldown);
            var code = fixedCode ?? RandomNumberGenerator.GetInt32(100000, 1000000)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            var hash = Hash(operation.ToString(), subject, code);
            var ttl = lifetime ?? TimeSpan.FromMinutes(operation == EmailChallengeOperation.SignIn
                ? 5 : AppConstants.SensitiveOperationChallengeTtlMinutes);
            if (row is null)
                db.EmailChallenges.Add(EmailChallenge.Create(operation.ToString(), subject, hash, nowAtUtc, ttl));
            else
                row.Issue(hash, nowAtUtc, ttl);
            await db.SaveChangesAsync(cancellationToken);
            return Result.Success(code);
        }, cancellationToken);
    }

    public Task<Result<EmailChallengeConfirmation>> ConfirmAsync(EmailChallengeOperation operation, string email, string code,
        bool consume = true, CancellationToken cancellationToken = default)
    {
        var subject = email.Trim().ToLowerInvariant();
        return LockedAsync(operation.ToString(), subject, async () =>
        {
            var nowAtUtc = clock.GetUtcNow().UtcDateTime;
            var row = await FindAsync(operation.ToString(), subject, cancellationToken);
            if (row is not null && row.AttemptWindowEndsAtUtc > nowAtUtc && row.Attempts >= AppConstants.MaxVerificationAttempts)
                return Result.Failure<EmailChallengeConfirmation>(ErrorMessages.TooManyCodeAttempts);
            if (row is null || row.Consumed || row.ExpiresAtUtc <= nowAtUtc)
                return Result.Failure<EmailChallengeConfirmation>(ExpiredError(operation));
            row.ResetExpiredAttempts(nowAtUtc);
            if (!row.Matches(Hash(operation.ToString(), subject, code)))
            {
                if (db.Database.IsRelational())
                {
                    var changed = await db.EmailChallenges
                        .Where(candidate => candidate.Id == row.Id && !candidate.Consumed
                            && candidate.Attempts < AppConstants.MaxVerificationAttempts
                            && candidate.ExpiresAtUtc > nowAtUtc)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.Attempts,
                            candidate => candidate.Attempts + 1), cancellationToken);
                    if (changed == 0)
                        return Result.Failure<EmailChallengeConfirmation>(ErrorMessages.TooManyCodeAttempts);
                    await db.Entry(row).ReloadAsync(cancellationToken);
                }
                else
                {
                    row.RecordFailedAttempt();
                    await db.SaveChangesAsync(cancellationToken);
                }
                return Result.Failure<EmailChallengeConfirmation>(operation switch
                {
                    EmailChallengeOperation.AccountDeletion => ErrorMessages.InvalidDeletionCode.Format(AppConstants.MaxVerificationAttempts - row.Attempts),
                    EmailChallengeOperation.ApiKeyManagement => ErrorMessages.InvalidApiKeyCreationCode.Format(AppConstants.MaxVerificationAttempts - row.Attempts),
                    _ => ErrorMessages.InvalidVerificationCode,
                });
            }
            if (consume)
            {
                if (db.Database.IsRelational())
                {
                    var changed = await db.EmailChallenges
                        .Where(candidate => candidate.Id == row.Id && !candidate.Consumed
                            && candidate.Attempts < AppConstants.MaxVerificationAttempts
                            && candidate.ExpiresAtUtc > nowAtUtc)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.Consumed, true), cancellationToken);
                    if (changed == 0)
                        return Result.Failure<EmailChallengeConfirmation>(ExpiredError(operation));
                }
                else
                {
                    row.Consume();
                    await db.SaveChangesAsync(cancellationToken);
                }
            }
            return Result.Success(new EmailChallengeConfirmation(row.ExpiresAtUtc - nowAtUtc));
        }, cancellationToken);
    }

    public async Task AuthorizeOnceAsync(EmailChallengeOperation operation, Guid userId, TimeSpan lifetime)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("A grant requires a user.", nameof(userId));
        if (lifetime <= TimeSpan.Zero)
            return;
        await LockedAsync($"grant:{operation}", userId.ToString(), async () =>
        {
            var row = await FindAsync($"grant:{operation}", userId.ToString(), CancellationToken.None);
            var nowAtUtc = clock.GetUtcNow().UtcDateTime;
            if (row is null)
                db.EmailChallenges.Add(EmailChallenge.Create($"grant:{operation}", userId.ToString(), new byte[32], nowAtUtc, lifetime));
            else
                row.Issue(new byte[32], nowAtUtc, lifetime);
            await db.SaveChangesAsync();
            return true;
        }, CancellationToken.None);
    }

    public async Task<bool> TryConsumeAuthorizationAsync(EmailChallengeOperation operation, Guid userId)
    {
        var nowAtUtc = clock.GetUtcNow().UtcDateTime;
        var query = db.EmailChallenges.Where(row => row.Operation == $"grant:{operation}"
            && row.Subject == userId.ToString() && row.ExpiresAtUtc > nowAtUtc);
        if (db.Database.IsRelational())
            return await query.ExecuteDeleteAsync() == 1;
        var row = await query.SingleOrDefaultAsync();
        if (row is null)
            return false;
        db.EmailChallenges.Remove(row);
        await db.SaveChangesAsync();
        return true;
    }

    public Task<bool> HasAuthorizationAsync(EmailChallengeOperation operation, Guid userId)
    {
        var nowAtUtc = clock.GetUtcNow().UtcDateTime;
        return db.EmailChallenges.AsNoTracking().AnyAsync(row => row.Operation == $"grant:{operation}"
            && row.Subject == userId.ToString() && row.ExpiresAtUtc > nowAtUtc);
    }

    public Task<bool> TryThrottleAsync(string operation, string subject, TimeSpan lifetime, CancellationToken cancellationToken) =>
        LockedAsync(operation, subject, async () =>
        {
            var nowAtUtc = clock.GetUtcNow().UtcDateTime;
            var row = await FindAsync(operation, subject, cancellationToken);
            if (row is not null && row.ExpiresAtUtc > nowAtUtc)
                return false;
            if (row is null)
                db.EmailChallenges.Add(EmailChallenge.Create(operation, subject, new byte[32], nowAtUtc, lifetime));
            else
                row.Issue(new byte[32], nowAtUtc, lifetime);
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }, cancellationToken);

    private async Task<EmailChallenge?> FindAsync(string operation, string subject, CancellationToken cancellationToken)
    {
        var row = await db.EmailChallenges.SingleOrDefaultAsync(row => row.Operation == operation && row.Subject == subject, cancellationToken);
        if (row is not null)
            await db.Entry(row).ReloadAsync(cancellationToken);
        return row;
    }

    private Task<T> LockedAsync<T>(string operation, string subject, Func<Task<T>> action, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            await unitOfWork.AcquireAdvisoryLockAsync($"email-challenge:{operation}:{subject}", token);
            return await action();
        }, cancellationToken);

    private static AppError ExpiredError(EmailChallengeOperation operation) => operation switch
    {
        EmailChallengeOperation.AccountDeletion => ErrorMessages.DeletionCodeExpired,
        EmailChallengeOperation.ApiKeyManagement => ErrorMessages.ApiKeyCreationCodeExpired,
        _ => ErrorMessages.VerificationCodeExpired,
    };
}
