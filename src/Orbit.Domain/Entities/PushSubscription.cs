using System.Security.Cryptography;
using System.Text;
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

    /// <summary>
    /// True when a registration presents the credentials only the device holding this subscription can
    /// read. A Web Push endpoint alone is not enough: the request must also carry the browser-generated
    /// <see cref="P256dh"/> key and <see cref="Auth"/> secret. An FCM registration token is the only
    /// credential Firebase issues to one app install, so the matched token plus the FCM sentinel is the proof.
    /// </summary>
    public bool MatchesCredentials(string p256dh, string auth) =>
        Transport == PushTransport.Fcm
            ? ClassifyTransport(p256dh) == PushTransport.Fcm
            : FixedTimeEquals(P256dh, p256dh) && FixedTimeEquals(Auth, auth);

    /// <summary>
    /// Moves this device's subscription to the account now registering it, so the previous account
    /// stops listing, counting and pushing to a device it no longer receives on.
    /// </summary>
    public Result TransferTo(Guid userId)
    {
        if (userId == Guid.Empty)
            return Result.Failure(DomainErrors.UserIdRequired);

        UserId = userId;
        CreatedAtUtc = DateTime.UtcNow;
        return Result.Success();
    }

    private static bool FixedTimeEquals(string stored, string presented) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(stored), Encoding.UTF8.GetBytes(presented));
}
