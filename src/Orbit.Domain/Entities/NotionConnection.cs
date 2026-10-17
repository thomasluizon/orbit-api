using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public enum NotionConnectionStatus { Idle, ReconnectRequired, TransientError }

public sealed class NotionConnection : Entity
{
    public Guid UserId { get; private set; }
    public string AccessToken { get; private set; } = string.Empty;
    public string? RefreshToken { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public string? WorkspaceName { get; private set; }
    public Guid BotId { get; private set; }
    public NotionConnectionStatus Status { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? LastPollAtUtc { get; private set; }
    public string SharedDatabasesJson { get; private set; } = "[]";

    private NotionConnection() { }

    public static NotionConnection Create(Guid userId, string accessToken, string? refreshToken,
        Guid workspaceId, string? workspaceName, Guid botId)
    {
        if (userId == Guid.Empty || workspaceId == Guid.Empty || botId == Guid.Empty)
            throw new ArgumentException("A user, workspace and bot are required.");
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        if (refreshToken is not null) ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        return new NotionConnection
        {
            UserId = userId,
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            WorkspaceId = workspaceId,
            WorkspaceName = workspaceName,
            BotId = botId,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    public void CompletePoll(string sharedDatabasesJson, DateTime polledAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedDatabasesJson);
        if (polledAtUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("A UTC timestamp is required.");
        SharedDatabasesJson = sharedDatabasesJson;
        LastPollAtUtc = polledAtUtc;
        Status = NotionConnectionStatus.Idle;
    }

    public void FailPoll(bool reconnectRequired, DateTime polledAtUtc)
    {
        if (polledAtUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("A UTC timestamp is required.");
        LastPollAtUtc = polledAtUtc;
        Status = reconnectRequired ? NotionConnectionStatus.ReconnectRequired : NotionConnectionStatus.TransientError;
    }
}
