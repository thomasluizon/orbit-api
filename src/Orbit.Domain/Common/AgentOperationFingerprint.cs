using System.Security.Cryptography;
using System.Text;

namespace Orbit.Domain.Common;

/// <summary>
/// Builds the deterministic dedupe/confirmation key for an agent mutation: the SHA-256 of the
/// operation identity and raw arguments, hex-encoded (64 chars). Hashing keeps arbitrarily large
/// tool payloads (e.g. bulk creates) inside the fingerprint column's 256-char bound. Every
/// surface that creates or consumes pending agent operations MUST build fingerprints through
/// this helper so confirmation matching stays consistent.
/// </summary>
public static class AgentOperationFingerprint
{
    public static string Compute(string operationIdentity, string argumentsJson)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{operationIdentity}:{argumentsJson}"));
        return Convert.ToHexString(bytes);
    }
}
