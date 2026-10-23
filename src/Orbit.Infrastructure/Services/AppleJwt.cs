using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Orbit.Infrastructure.Services;

internal static class AppleJwt
{
    internal static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static byte[] Decode(string value) => Convert.FromBase64String(
        value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '='));

    internal static string Sign(string keyId, string privateKey, object claims)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKey);
        var content = $"{Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", kid = keyId, typ = "JWT" }))}.{Encode(JsonSerializer.SerializeToUtf8Bytes(claims))}";
        return $"{content}.{Encode(key.SignData(Encoding.ASCII.GetBytes(content), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))}";
    }
}
