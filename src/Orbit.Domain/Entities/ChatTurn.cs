using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public sealed class ChatTurn : Entity
{
    public Guid UserId { get; private set; }
    public string KeyHash { get; private set; } = "";
    public string Fingerprint { get; private set; } = "";
    public DateTime ExpiresAtUtc { get; private set; }
    public string? TerminalEvent { get; private set; }

    private ChatTurn() { }

    public static ChatTurn Create(Guid userId, string keyHash, string fingerprint, DateTime expiresAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(userId, Guid.Empty);
        if (!IsHash(keyHash) || !IsHash(fingerprint))
            throw new ArgumentException("Turn identity and fingerprint must be SHA256 hex digests.");
        if (expiresAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Turn expiry must be a UTC instant.", nameof(expiresAtUtc));
        return new ChatTurn { UserId = userId, KeyHash = keyHash, Fingerprint = fingerprint, ExpiresAtUtc = expiresAtUtc };
    }

    public void Complete(string terminalEvent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(terminalEvent);
        if (TerminalEvent is not null)
            throw new InvalidOperationException("A completed turn is immutable.");
        TerminalEvent = terminalEvent;
    }

    private static bool IsHash(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);
}
