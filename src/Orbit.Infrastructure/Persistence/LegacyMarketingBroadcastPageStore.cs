using Microsoft.EntityFrameworkCore;
using Orbit.Application.Marketing.Commands;
using Orbit.Application.Marketing.Jobs;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Persistence;

public sealed class LegacyMarketingBroadcastPageStore(OrbitDbContext db, IUnitOfWork unitOfWork)
    : ILegacyMarketingBroadcastPageStore
{
    public Task ImportAsync(Guid broadcastId, SendMarketingBroadcastCommand content, MarketingRecipient[] recipients,
        DateTime notBeforeAtUtc, CancellationToken cancellationToken) => unitOfWork.ExecuteOnceInTransactionAsync(async token =>
    {
        await unitOfWork.AcquireAdvisoryLockAsync($"marketing-legacy-import:{broadcastId}", token);
        var recipientIds = recipients.Select(recipient => recipient.UserId).Distinct().ToArray();
        var delivered = await db.Database.SqlQuery<Guid>($"""
            SELECT "RecipientId" AS "Value" FROM "MarketingBroadcastDeliveries" WHERE "BroadcastId" = {broadcastId}
            """).Where(id => recipientIds.Contains(id)).ToListAsync(token);
        var imported = await db.MarketingDeliveries.Where(row => row.BroadcastId == broadcastId
            && recipientIds.Contains(row.RecipientId)).Select(row => row.RecipientId).ToListAsync(token);
        var existing = delivered.Concat(imported).ToHashSet();
        foreach (var recipient in recipients.DistinctBy(recipient => recipient.UserId).Where(recipient => !existing.Contains(recipient.UserId)))
            db.MarketingDeliveries.Add(MarketingDelivery.Create(broadcastId, recipient.UserId, recipient.Email, recipient.Language,
                content.SubjectEn, content.SubjectPt, content.BodyHtmlEn, content.BodyHtmlPt, notBeforeAtUtc));
        await unitOfWork.SaveChangesAsync(token);
    }, cancellationToken);
}
