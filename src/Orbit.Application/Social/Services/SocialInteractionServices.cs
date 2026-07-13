namespace Orbit.Application.Social.Services;

/// <summary>Groups the shared social-graph collaborators the social write handlers use to keep their constructors small.</summary>
public record SocialInteractionServices(
    SocialAccessGuard AccessGuard,
    FriendGraphService FriendGraph,
    SocialNotificationDispatcher NotificationDispatcher);
