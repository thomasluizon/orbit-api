using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public sealed class ConversationOfferStore(OrbitDbContext db, IEncryptionService encryption) : IConversationOfferStore
{
    public async Task<IReadOnlyList<AstraOfferFact>> IssuedAsync(Guid userId, Guid conversationId, CancellationToken ct) =>
        (await db.ConversationOffers.AsNoTracking().Where(offer => offer.UserId == userId
            && offer.ConversationId == conversationId && !offer.Dismissed)
            .OrderBy(offer => offer.Id).Take(100).Select(offer => offer.DefinitionJson).ToListAsync(ct))
            .Select(json => JsonSerializer.Deserialize<AstraOfferFact>(json)).OfType<AstraOfferFact>().ToArray();

    public async Task<IReadOnlySet<string>> DismissedAsync(Guid userId, Guid conversationId, CancellationToken ct) =>
        (await db.ConversationOffers.AsNoTracking().Where(offer => offer.UserId == userId
            && offer.ConversationId == conversationId && offer.Dismissed)
            .Select(offer => offer.OfferKey).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);

    public async Task IssueAsync(Guid userId, Guid conversationId, AstraOfferFact fact, CancellationToken ct)
    {
        var created = ConversationOffer.Create(userId, conversationId, fact.Action.OfferKey, fact);
        if (created.IsFailure) throw new ArgumentException(created.Error);
        var offer = created.Value;
        var definition = encryption.Encrypt(offer.DefinitionJson);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ConversationOffers" ("Id", "UserId", "ConversationId", "OfferKey", "DefinitionJson", "Dismissed")
            VALUES ({offer.Id}, {offer.UserId}, {offer.ConversationId}, {offer.OfferKey}, {definition}, FALSE)
            ON CONFLICT ("UserId", "ConversationId", "OfferKey") DO NOTHING
            """, ct);
    }

    public async Task DismissAsync(Guid userId, Guid conversationId, IReadOnlyList<string> keys, CancellationToken ct)
    {
        foreach (var key in keys.Distinct(StringComparer.Ordinal))
        {
            var created = ConversationOffer.Create(userId, conversationId, key);
            if (created.IsFailure) throw new ArgumentException(created.Error);
            var offer = created.Value;
            var definition = encryption.Encrypt(offer.DefinitionJson);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "ConversationOffers" ("Id", "UserId", "ConversationId", "OfferKey", "DefinitionJson", "Dismissed")
                VALUES ({offer.Id}, {offer.UserId}, {offer.ConversationId}, {offer.OfferKey}, {definition}, TRUE)
                ON CONFLICT ("UserId", "ConversationId", "OfferKey") DO UPDATE SET "Dismissed" = TRUE
                """, ct);
        }
    }
}
