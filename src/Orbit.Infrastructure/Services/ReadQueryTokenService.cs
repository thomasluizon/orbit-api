using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Services;

public sealed class ReadQueryTokenService(IOptions<JwtSettings> settings) : IReadQueryTokenService
{
    private byte[] Key => HMACSHA256.HashData(Encoding.UTF8.GetBytes(settings.Value.SecretKey),
        Encoding.UTF8.GetBytes("orbit.exact-read.v1"));

    public string Issue(ReadQueryTicket ticket)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(ticket);
        return Convert.ToBase64String(data) + "." + Convert.ToBase64String(HMACSHA256.HashData(Key, data));
    }

    public ReadQueryTicket? Read(string token, Guid userId)
    {
        if (token.Length > 32768) return null;
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 2) return null;
            var data = Convert.FromBase64String(parts[0]);
            var signature = Convert.FromBase64String(parts[1]);
            if (!CryptographicOperations.FixedTimeEquals(signature, HMACSHA256.HashData(Key, data))) return null;
            var ticket = JsonSerializer.Deserialize<ReadQueryTicket>(data);
            return ticket?.UserId == userId && ticket.ExpiresAtUtc > TimeProvider.System.GetUtcNow().UtcDateTime
                ? ticket : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }
}
