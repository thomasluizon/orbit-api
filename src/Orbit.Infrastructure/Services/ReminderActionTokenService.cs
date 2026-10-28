using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Services;

public sealed class ReminderActionTokenService(IOptions<JwtSettings> options) : IReminderActionTokenService
{
    private readonly string _key = "orbit:reminder-action:v1:" + options.Value.SecretKey;

    public string Sign(ReminderActionClaims claims) => SignedTokenCodec.Encode(JsonSerializer.SerializeToUtf8Bytes(claims), _key);

    public bool TryRead(string token, [NotNullWhen(true)] out ReminderActionClaims? claims)
    {
        claims = null;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 2048 || !SignedTokenCodec.TryDecode(token, _key, out var bytes))
            return false;
        try
        {
            var value = JsonSerializer.Deserialize<ReminderActionClaims>(bytes);
            if (value is null || value.UserId == Guid.Empty || value.HabitId == Guid.Empty || value.ReminderId == Guid.Empty
                || value.ReminderAtUtc.Kind != DateTimeKind.Utc || value.ExpiresAtUtc.Kind != DateTimeKind.Utc
                || value.ExpiresAtUtc <= value.ReminderAtUtc)
                return false;
            claims = value;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
