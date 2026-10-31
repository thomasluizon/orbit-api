using System.Text.Json;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Domain.Entities;

public sealed class ConversationOffer : Entity
{
    public Guid UserId { get; private set; }
    public Guid ConversationId { get; private set; }
    public string OfferKey { get; private set; } = string.Empty;
    public string DefinitionJson { get; private set; } = string.Empty;
    public bool Dismissed { get; private set; }

    private ConversationOffer() { }

    public static Result<ConversationOffer> Create(Guid userId, Guid conversationId, string key, AstraOfferFact? fact = null)
    {
        if (userId == Guid.Empty || conversationId == Guid.Empty || string.IsNullOrWhiteSpace(key)
            || key.Length > 200 || key.Any(char.IsControl) || fact is not null && fact.Action.OfferKey != key)
            return Result.Failure<ConversationOffer>("invalid_offer");
        return Result.Success(new ConversationOffer
        {
            UserId = userId,
            ConversationId = conversationId,
            OfferKey = key,
            DefinitionJson = fact is null ? "{}" : JsonSerializer.Serialize(fact)
        });
    }

    public void Dismiss() => Dismissed = true;
}
