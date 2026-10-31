using System.Text.Json;

namespace Orbit.Domain.Models;

public sealed record ReadBlockMetadata(
    Guid QueryId, string QueryFamily, JsonElement Scope, string Revision,
    string Completeness, string RefreshToken, string? NextCursor = null,
    int? TotalCount = null, string? Recovery = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    AstraNextAction? NextAction = null);

public sealed record AstraNextAction(string OfferKey, string Kind, string Label,
    string? Surface = null, string? Prompt = null, Guid? EntityId = null,
    DateOnly? Date = null);

public sealed record AstraOfferFact(string Id, string Family, string Evidence,
    AstraNextAction Action, Guid QueryId, string Revision, DateOnly LocalDate)
{
    public ReadBlockMetadata? Source { get; init; }
    public string? SourceKind { get; init; }
    public Guid? PendingOperationId { get; init; }
}

public sealed record QueryReadBlock(string Kind, object Block, ReadBlockMetadata Read);
public sealed record ReadQueryTicket(Guid UserId, Guid QueryId, string Tool, JsonElement Arguments,
    string Kind, string Revision, DateTime ExpiresAtUtc, int Offset = 0);
