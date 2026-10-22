using System.Text.Json;

namespace Orbit.Domain.Models;

public sealed record ReadBlockMetadata(
    Guid QueryId, string QueryFamily, JsonElement Scope, string Revision,
    string Completeness, string RefreshToken, string? NextCursor = null,
    int? TotalCount = null, string? Recovery = null);

public sealed record QueryReadBlock(string Kind, object Block, ReadBlockMetadata Read);
public sealed record ReadQueryTicket(Guid UserId, Guid QueryId, string Tool, JsonElement Arguments,
    string Kind, string Revision, DateTime ExpiresAtUtc, int Offset = 0);
