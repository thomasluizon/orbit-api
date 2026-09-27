using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Services;

public sealed class SnsMessageVerifier(
    IHttpClientFactory httpClientFactory,
    IOptions<SesSettings> options)
{
    private readonly SesSettings _settings = options.Value;
    private X509Certificate2? _trustedRoot;

    internal SnsMessageVerifier(IHttpClientFactory httpClientFactory, IOptions<SesSettings> options, X509Certificate2 trustedRoot)
        : this(httpClientFactory, options) => _trustedRoot = trustedRoot;

    public async Task<bool> VerifyAsync(JsonElement envelope, CancellationToken cancellationToken)
    {
        if (!Read(envelope, "Type", out var type) || type is not ("Notification" or "SubscriptionConfirmation") ||
            !Read(envelope, "TopicArn", out var topicArn) || string.IsNullOrWhiteSpace(_settings.TopicArn) ||
            !string.Equals(topicArn, _settings.TopicArn, StringComparison.Ordinal) ||
            !Read(envelope, "SignatureVersion", out var version) || version is not ("1" or "2") ||
            !Read(envelope, "Signature", out var signatureText) ||
            !Read(envelope, "SigningCertURL", out var certificateUrl) ||
            !IsSnsUrl(certificateUrl, certificate: true))
            return false;

        byte[] signature;
        try { signature = Convert.FromBase64String(signatureText); }
        catch (FormatException) { return false; }

        var fields = type == "Notification"
            ? new[] { "Message", "MessageId", "Subject", "Timestamp", "TopicArn", "Type" }
            : new[] { "Message", "MessageId", "SubscribeURL", "Timestamp", "Token", "TopicArn", "Type" };
        var canonical = new StringBuilder();
        foreach (var field in fields)
        {
            if (!Read(envelope, field, out var value))
            {
                if (field == "Subject" && type == "Notification")
                    continue;
                return false;
            }
            canonical.Append(field).Append('\n').Append(value).Append('\n');
        }

        try
        {
            using var response = await httpClientFactory.CreateClient("SnsCertificate").GetAsync(certificateUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return false;
            var pem = await response.Content.ReadAsStringAsync(cancellationToken);
            using var certificate = X509Certificate2.CreateFromPem(pem);
            using var chain = new X509Chain();
            if (_trustedRoot is not null)
            {
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(_trustedRoot);
            }
            if (!chain.Build(certificate))
                return false;
            using var rsa = certificate.GetRSAPublicKey();
            return rsa is not null && rsa.VerifyData(
                Encoding.UTF8.GetBytes(canonical.ToString()), signature,
                version == "2" ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA1,
                RSASignaturePadding.Pkcs1);
        }
        catch (Exception ex) when (ex is HttpRequestException or CryptographicException or FormatException)
        {
            return false;
        }
    }

    public bool IsSnsUrl(string? value, bool certificate = false)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) ||
            url.Scheme != Uri.UriSchemeHttps || url.Port != 443 || !string.IsNullOrEmpty(url.UserInfo) ||
            !string.Equals(url.Host, $"sns.{_settings.Region}.amazonaws.com", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(url.Fragment))
            return false;

        if (certificate)
            return url.Query.Length == 0 &&
                System.Text.RegularExpressions.Regex.IsMatch(url.AbsolutePath, @"^/SimpleNotificationService-[0-9a-fA-F]+\.pem$");
        return url.AbsolutePath == "/";
    }

    public bool IsSubscriptionUrl(string? value, string topicArn, string token)
    {
        if (!IsSnsUrl(value) || !Uri.TryCreate(value, UriKind.Absolute, out var url))
            return false;
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = item.Split('=', 2);
            if (parts.Length != 2 || !parameters.TryAdd(Uri.UnescapeDataString(parts[0]), Uri.UnescapeDataString(parts[1])))
                return false;
        }
        return parameters.Count == 3 && parameters.TryGetValue("Action", out var action) && action == "ConfirmSubscription" &&
            parameters.TryGetValue("TopicArn", out var actualTopic) && actualTopic == topicArn &&
            parameters.TryGetValue("Token", out var actualToken) && actualToken == token;
    }

    private static bool Read(JsonElement element, string name, out string value)
    {
        value = "";
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString() ?? "";
        return !string.IsNullOrEmpty(value);
    }
}
