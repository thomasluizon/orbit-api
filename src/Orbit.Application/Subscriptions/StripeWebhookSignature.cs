using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Stripe;

namespace Orbit.Application.Subscriptions;

public static class StripeWebhookSignature
{
    public static void Verify(ReadOnlySpan<byte> body, string header, string secret)
    {
        var parts = header.Split(',').Select(part => part.Trim().Split('=', 2)).ToArray();
        var timestamp = parts.FirstOrDefault(part => part.Length == 2 && part[0] == "t")?.ElementAt(1);
        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || Math.Abs((decimal)DateTimeOffset.UtcNow.ToUnixTimeSeconds() - seconds) > EventUtility.DefaultTimeTolerance)
            throw new StripeException("Webhook timestamp is missing or outside the allowed tolerance.");

        using var hash = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(secret));
        hash.AppendData(Encoding.UTF8.GetBytes(timestamp + "."));
        hash.AppendData(body);
        var expected = hash.GetHashAndReset();
        Span<byte> signature = stackalloc byte[32];
        foreach (var part in parts.Where(part => part.Length == 2 && part[0] == "v1"))
        {
            if (Convert.FromHexString(part[1], signature, out var consumed, out var written) == System.Buffers.OperationStatus.Done
                && consumed == part[1].Length && written == expected.Length
                && CryptographicOperations.FixedTimeEquals(expected, signature))
                return;
        }
        throw new StripeException("Webhook signature did not match.");
    }
}
