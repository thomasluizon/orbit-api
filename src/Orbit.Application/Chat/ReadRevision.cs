using System.Security.Cryptography;
using System.Text.Json;

namespace Orbit.Application.Chat;

public static class ReadRevision
{
    public static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}
