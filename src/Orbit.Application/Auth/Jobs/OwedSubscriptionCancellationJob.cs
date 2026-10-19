using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Application.Auth.Validators;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Jobs;

public sealed class OwedSubscriptionCancellationJob(
    IGenericRepository<OwedSubscriptionCancellation> cancellations,
    IUnitOfWork unitOfWork,
    IBillingService stripe,
    IPlayBillingService play,
    IOptions<GooglePlaySettings> playSettings,
    ILogger<OwedSubscriptionCancellationJob> logger,
    TimeProvider clock)
{
    public async Task<Guid> RecordAsync(SubscriptionSource provider, string identifier, CancellationToken ct)
    {
        await new OwedSubscriptionCancellationValidator().ValidateAndThrowAsync(new(provider, identifier), ct);
        return await unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            await unitOfWork.AcquireAdvisoryLockAsync($"owed-cancellation:{provider}:{identifier}", token);
            var existing = await cancellations.FindOneTrackedAsync(
                record => record.Provider == provider && record.ProviderIdentifier == identifier, cancellationToken: token);
            if (existing is not null)
                return existing.Id;
            var record = OwedSubscriptionCancellation.Create(provider, identifier);
            await cancellations.AddAsync(record, token);
            try
            {
                await unitOfWork.SaveChangesAsync(token);
                return record.Id;
            }
            catch (DbUpdateException ex) when (DbUniqueViolation.IsUniqueViolation(ex))
            {
                cancellations.Remove(record);
                existing = await cancellations.FindOneTrackedAsync(
                    item => item.Provider == provider && item.ProviderIdentifier == identifier, cancellationToken: token);
                if (existing is null)
                    throw;
                return existing.Id;
            }
        }, ct);
    }

    public async Task ExecuteAsync(Guid recordId, CancellationToken ct)
    {
        if (unitOfWork.HasActiveTransaction)
            throw new InvalidOperationException("Owed cancellation attempts require an independent transaction.");
        unitOfWork.ResetTracking();
        var claimed = await unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            await unitOfWork.AcquireAdvisoryLockAsync($"owed-cancellation-attempt:{recordId}", token);
            var record = await cancellations.FindOneTrackedAsync(record => record.Id == recordId, cancellationToken: token);
            var attemptedAtUtc = clock.GetUtcNow().UtcDateTime;
            if (record is null || record.NextAttemptAtUtc > attemptedAtUtc)
                return null;
            record.RecordAttempt(attemptedAtUtc);
            await unitOfWork.SaveChangesAsync(token);
            return record;
        }, ct);
        if (claimed is null)
            return;
        var attempt = claimed.Attempts;
        if (claimed.ExhaustedAtUtc.HasValue)
            AlertExhaustion(claimed);

        var succeeded = false;
        try
        {
            await CancelProviderAsync(claimed, ct);
            succeeded = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            if (!claimed.ExhaustedAtUtc.HasValue)
                logger.LogWarning("Provider cancellation {CancellationId} for {Provider} failed on attempt {Attempt}",
                    claimed.Id, claimed.Provider, attempt);
        }

        unitOfWork.ResetTracking();
        await unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            await unitOfWork.AcquireAdvisoryLockAsync($"owed-cancellation-attempt:{recordId}", token);
            var record = await cancellations.FindOneTrackedAsync(record => record.Id == recordId, cancellationToken: token);
            if (record is null)
                return;
            if (succeeded)
                cancellations.Remove(record);
            else if (record.Attempts == attempt)
                record.RecordFailure(clock.GetUtcNow().UtcDateTime);
            await unitOfWork.SaveChangesAsync(token);
        }, ct);
    }

    private async Task CancelProviderAsync(OwedSubscriptionCancellation record, CancellationToken ct)
    {
        if (record.Provider == SubscriptionSource.Stripe)
        {
            var subscription = await stripe.GetSubscriptionDetailsAsync(record.ProviderIdentifier, ct);
            if (subscription is not null && subscription.Status is not ("canceled" or "incomplete_expired") && !subscription.CancelAtPeriodEnd)
                await stripe.SetSubscriptionCancelAtPeriodEndAsync(record.ProviderIdentifier, true, ct);
        }
        else
        {
            var purchase = await play.VerifyAsync(playSettings.Value.ProductId, record.ProviderIdentifier, ct);
            if (purchase is { CanRenew: true })
                await play.CancelAsync(record.ProviderIdentifier, ct);
        }
    }

    private void AlertExhaustion(OwedSubscriptionCancellation record) =>
        logger.LogError("Provider cancellation {CancellationId} for {Provider} exhausted {Attempts} attempts; renewal may still charge and requires billing reconciliation",
            record.Id, record.Provider, record.Attempts);
}
