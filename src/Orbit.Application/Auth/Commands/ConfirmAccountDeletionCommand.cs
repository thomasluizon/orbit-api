using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Behaviors;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using System.Security.Cryptography;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Commands;

public record ConfirmAccountDeletionCommand(Guid UserId, string Code) : IRequest<Result<DateTime>>, IConcurrencyRetryable;

public class ConfirmAccountDeletionCommandHandler(
    IMemoryCache cache,
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<ConfirmAccountDeletionCommand, Result<DateTime>>
{
    public async Task<Result<DateTime>> Handle(ConfirmAccountDeletionCommand request, CancellationToken cancellationToken)
    {
        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure<DateTime>(ErrorMessages.UserNotFound);

        var email = user.Email.ToLowerInvariant();
        var cacheKey = $"delete:{email}";

        if (CountFailedAttempts(email) >= AppConstants.MaxVerificationAttempts)
            return Result.Failure<DateTime>(ErrorMessages.TooManyCodeAttempts);

        if (!cache.TryGetValue(cacheKey, out VerificationEntry? entry) || entry is null)
            return Result.Failure<DateTime>(ErrorMessages.DeletionCodeExpired);

        if (!CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(entry.Code),
            System.Text.Encoding.UTF8.GetBytes(request.Code)))
        {
            RecordFailedAttempt(email);
            return Result.Failure<DateTime>(ErrorMessages.InvalidDeletionCode);
        }

        cache.Remove(cacheKey);

        var nowAtUtc = DateTime.UtcNow;
        var scheduledDate = user.HasProAccess && user.PlanExpiresAt.HasValue && user.PlanExpiresAt.Value > nowAtUtc
            ? user.PlanExpiresAt.Value.AddDays(7)
            : nowAtUtc.AddDays(7);

        user.Deactivate(scheduledDate);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(scheduledDate);
    }

    private int CountFailedAttempts(string email) =>
        cache.TryGetValue(FailedAttemptCacheKey(email), out int attempts) ? attempts : 0;

    private void RecordFailedAttempt(string email)
    {
        var attempts = CountFailedAttempts(email) + 1;
        cache.Set(FailedAttemptCacheKey(email), attempts, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(AppConstants.VerificationAttemptWindowMinutes)
        });
    }

    private static string FailedAttemptCacheKey(string email) => $"delete-attempts:{email}";
}
