using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public class PushSubscription : Entity
{
    public Guid UserId { get; private set; }
    public string Endpoint { get; private set; } = null!;
    public string P256dh { get; private set; } = null!;
    public string Auth { get; private set; } = null!;
    public DateTime CreatedAtUtc { get; private set; }

    private PushSubscription() { }

    public static Result<PushSubscription> Create(
        Guid userId,
        string endpoint,
        string p256dh,
        string auth)
    {
        if (userId == Guid.Empty)
            return Result.Failure<PushSubscription>(DomainErrors.UserIdRequired);

        if (string.IsNullOrWhiteSpace(endpoint))
            return Result.Failure<PushSubscription>(DomainErrors.PushEndpointRequired);

        if (string.IsNullOrWhiteSpace(p256dh))
            return Result.Failure<PushSubscription>(DomainErrors.PushP256dhRequired);

        if (string.IsNullOrWhiteSpace(auth))
            return Result.Failure<PushSubscription>(DomainErrors.PushAuthKeyRequired);

        return Result.Success(new PushSubscription
        {
            UserId = userId,
            Endpoint = endpoint,
            P256dh = p256dh,
            Auth = auth,
            CreatedAtUtc = DateTime.UtcNow
        });
    }
}
