namespace Orbit.Domain.Models;

public sealed record PushDelivery(Guid UserId, string Title, string Body, string? Url = null,
    IReadOnlyDictionary<string, string>? Data = null);
