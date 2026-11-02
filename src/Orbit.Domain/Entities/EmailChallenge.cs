using System.Security.Cryptography;
using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public sealed class EmailChallenge : Entity
{
    public string Operation { get; private set; } = string.Empty;
    public string Subject { get; private set; } = string.Empty;
    public byte[] CodeHash { get; private set; } = [];
    public int Attempts { get; private set; }
    public DateTime AttemptWindowEndsAtUtc { get; private set; }
    public DateTime IssuedAtUtc { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public bool Consumed { get; private set; }

    private EmailChallenge() { }

    public static EmailChallenge Create(string operation, string subject, byte[] hash, DateTime nowAtUtc, TimeSpan lifetime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        if (operation.Length > 64 || subject.Length > 320)
            throw new ArgumentException("Challenge keys exceed their storage limits.");
        if (hash.Length != 32 || lifetime <= TimeSpan.Zero)
            throw new ArgumentException("A challenge requires a SHA-256 hash and a positive lifetime.");
        var challenge = new EmailChallenge { Operation = operation, Subject = subject };
        challenge.Issue(hash, nowAtUtc, lifetime);
        return challenge;
    }

    public void Issue(byte[] hash, DateTime nowAtUtc, TimeSpan lifetime)
    {
        if (hash.Length != 32 || lifetime <= TimeSpan.Zero)
            throw new ArgumentException("A challenge requires a SHA-256 hash and a positive lifetime.");
        if (nowAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Challenge instants must use UTC.", nameof(nowAtUtc));
        ResetExpiredAttempts(nowAtUtc);
        CodeHash = hash.ToArray();
        IssuedAtUtc = nowAtUtc;
        ExpiresAtUtc = nowAtUtc.Add(lifetime);
        Consumed = false;
    }

    public void ResetExpiredAttempts(DateTime nowAtUtc)
    {
        if (AttemptWindowEndsAtUtc > nowAtUtc)
            return;
        Attempts = 0;
        AttemptWindowEndsAtUtc = nowAtUtc.AddMinutes(15);
    }

    public bool Matches(byte[] hash) => CryptographicOperations.FixedTimeEquals(CodeHash, hash);
    public void RecordFailedAttempt() => Attempts = Math.Min(3, Attempts + 1);
    public void Consume() => Consumed = true;
}
