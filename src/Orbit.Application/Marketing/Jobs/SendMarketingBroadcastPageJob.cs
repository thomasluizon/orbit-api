using FluentValidation;
using Hangfire;
using Orbit.Application.Marketing.Commands;
using Orbit.Application.Marketing.Validators;

namespace Orbit.Application.Marketing.Jobs;

public sealed record MarketingRecipient(Guid UserId, string Email, string Language);

public interface ILegacyMarketingBroadcastPageStore
{
    Task ImportAsync(Guid broadcastId, SendMarketingBroadcastCommand content, MarketingRecipient[] recipients,
        DateTime notBeforeAtUtc, CancellationToken cancellationToken);
}

// https://github.com/thomasluizon/orbit-tickets/issues/1956: Remove this transition shim only when no legacy page job remains in Hangfire storage (thomasluizon/orbit-tickets#1956).
public sealed class SendMarketingBroadcastPageJob(ILegacyMarketingBroadcastPageStore store, TimeProvider clock)
{
    [Queue("marketing")]
    [DisableConcurrentExecution(600)]
    public async Task ExecuteAsync(Guid broadcastId, int pageNumber, SendMarketingBroadcastCommand content,
        MarketingRecipient[] recipients, DateTime expiresAtUtc, CancellationToken cancellationToken)
    {
        if (broadcastId == Guid.Empty) throw new ArgumentException("Broadcast is required.", nameof(broadcastId));
        ArgumentOutOfRangeException.ThrowIfNegative(pageNumber);
        if (recipients.Length > 500) throw new ArgumentException("A broadcast page contains at most 500 recipients.", nameof(recipients));
        if (expiresAtUtc.Kind != DateTimeKind.Utc || expiresAtUtc == default)
            throw new ArgumentException("Broadcast expiry must be a UTC instant.", nameof(expiresAtUtc));
        cancellationToken.ThrowIfCancellationRequested();
        var nowAtUtc = clock.GetUtcNow().UtcDateTime;
        if (nowAtUtc >= expiresAtUtc) return;
        await new SendMarketingBroadcastCommandValidator().ValidateAndThrowAsync(content, cancellationToken);
        var validator = new MarketingRecipientDeliveryValidator();
        foreach (var recipient in recipients)
            await validator.ValidateAndThrowAsync(new(broadcastId, recipient.UserId, recipient.Email, recipient.Language), cancellationToken);
        await store.ImportAsync(broadcastId, content, recipients, nowAtUtc, cancellationToken);
    }
}
