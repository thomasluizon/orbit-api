using Orbit.Domain.Common;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Entities;

public class PushSubscription : Entity
{
    /// <summary>
    /// Wire/storage sentinel persisted in <see cref="P256dh"/> when a subscription is an FCM
    /// device token rather than a Web Push key. The native client sends this literal; classification
    /// flows through <see cref="ClassifyTransport"/> / <see cref="Transport"/> instead of comparing
    /// the bare string at call sites.
    /// </summary>
    public const string FcmSentinel = "fcm";

    public Guid UserId { get; private set; }
    public string Endpoint { get; private set; } = null!;
    public string P256dh { get; private set; } = null!;
    public string Auth { get; private set; } = null!;
    public DateTime CreatedAtUtc { get; private set; }

    public PushTransport Transport => ClassifyTransport(P256dh);

    public static PushTransport ClassifyTransport(string p256dh) =>
        string.Equals(p256dh, FcmSentinel, StringComparison.Ordinal)
            ? PushTransport.Fcm
            : PushTransport.WebPush;

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
