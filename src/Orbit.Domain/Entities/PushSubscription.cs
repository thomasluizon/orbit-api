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
    public const string ApnsSentinel = "apns";
    public const string WnsSentinel = "wns";

    public static bool IsAllowedApnsTopic(string? topic) =>
        topic is null or "org.useorbit.desktop" or "org.useorbit.desktop.staging";

    public Guid UserId { get; private set; }
    public string Endpoint { get; private set; } = null!;
    public string P256dh { get; private set; } = null!;
    public string Auth { get; private set; } = null!;
    public DateTime CreatedAtUtc { get; private set; }
    public string? ApnsTopic { get; private set; }
    public Guid? DeviceId { get; private set; }

    public PushTransport Transport => ClassifyTransport(P256dh);

    public static PushTransport ClassifyTransport(string p256dh) =>
        p256dh switch
        {
            FcmSentinel => PushTransport.Fcm,
            ApnsSentinel => PushTransport.Apns,
            WnsSentinel => PushTransport.Wns,
            _ => PushTransport.WebPush
        };

    private PushSubscription() { }

    public static Result<PushSubscription> Create(
        Guid userId,
        string endpoint,
        string p256dh,
        string auth,
        string? apnsTopic = null,
        Guid? deviceId = null)
    {
        if (userId == Guid.Empty)
            return Result.Failure<PushSubscription>(DomainErrors.UserIdRequired);

        if (string.IsNullOrWhiteSpace(endpoint))
            return Result.Failure<PushSubscription>(DomainErrors.PushEndpointRequired);

        if (string.IsNullOrWhiteSpace(p256dh))
            return Result.Failure<PushSubscription>(DomainErrors.PushP256dhRequired);

        if (string.IsNullOrWhiteSpace(auth))
            return Result.Failure<PushSubscription>(DomainErrors.PushAuthKeyRequired);

        if (ClassifyTransport(p256dh) == PushTransport.WebPush && !WebPushEndpointPolicy.IsAllowed(endpoint))
            return Result.Failure<PushSubscription>(DomainErrors.PushEndpointInvalid);

        if (ClassifyTransport(p256dh) == PushTransport.Wns && !WnsEndpointPolicy.IsAllowed(endpoint))
            return Result.Failure<PushSubscription>(DomainErrors.WnsEndpointInvalid);

        if (!IsAllowedApnsTopic(apnsTopic) || (apnsTopic is not null && ClassifyTransport(p256dh) != PushTransport.Apns))
            return Result.Failure<PushSubscription>(DomainErrors.PushApnsTopicInvalid);

        if (deviceId == Guid.Empty || (ClassifyTransport(p256dh) == PushTransport.Wns && deviceId is null)
            || (deviceId is not null && ClassifyTransport(p256dh) is not (PushTransport.Wns or PushTransport.Apns)))
            return Result.Failure<PushSubscription>(DomainErrors.PushDeviceIdInvalid);

        return Result.Success(new PushSubscription
        {
            UserId = userId,
            Endpoint = endpoint,
            P256dh = p256dh,
            Auth = auth,
            ApnsTopic = apnsTopic,
            DeviceId = deviceId,
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
        Transport is PushTransport.Fcm or PushTransport.Apns or PushTransport.Wns
            ? ClassifyTransport(p256dh) == Transport
            : FixedTimeEquals(P256dh, p256dh) && FixedTimeEquals(Auth, auth);

    public Result RenewRegistration(string endpoint, string auth, string? apnsTopic, Guid? deviceId)
    {
        var registration = Create(UserId, endpoint, P256dh, auth, apnsTopic, deviceId);
        if (registration.IsFailure)
            return Result.Failure(registration.Error, registration.ErrorCode!);

        Endpoint = endpoint;
        Auth = auth;
        ApnsTopic = apnsTopic;
        DeviceId = deviceId;
        CreatedAtUtc = DateTime.UtcNow;
        return Result.Success();
    }

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
