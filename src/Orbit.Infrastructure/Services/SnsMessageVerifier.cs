using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Services;

public sealed partial class SnsMessageVerifier(
    IHttpClientFactory httpClientFactory,
    IOptions<SesSettings> options,
    IMemoryCache certificateCache)
{
    private const string TopicArnField = "TopicArn";
    private static readonly TimeSpan CertificateCacheDuration = TimeSpan.FromHours(1);
    private static readonly SemaphoreSlim[] CertificateFetchLocks =
        Enumerable.Range(0, 32).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly SesSettings _settings = options.Value;
    private readonly X509Certificate2? _trustedRoot;

    internal SnsMessageVerifier(IHttpClientFactory httpClientFactory, IOptions<SesSettings> options,
        IMemoryCache certificateCache, X509Certificate2 trustedRoot)
        : this(httpClientFactory, options, certificateCache) => _trustedRoot = trustedRoot;

    public async Task<bool> VerifyAsync(JsonElement envelope, CancellationToken cancellationToken)
    {
        if (!TryReadEnvelope(envelope, out var type, out var version, out var signatureText, out var certificateUrl))
            return false;

        byte[] signature;
        try { signature = Convert.FromBase64String(signatureText); }
        catch (FormatException) { return false; }

        if (!TryBuildCanonicalMessage(envelope, type, out var canonical))
            return false;

        try
        {
            var pem = await GetCertificatePemAsync(certificateUrl, cancellationToken);
            if (pem is null)
                return false;
            using var certificate = X509Certificate2.CreateFromPem(pem);
            if (!IsTrustedCertificate(certificate))
                return false;
            using var rsa = certificate.GetRSAPublicKey();
            return rsa is not null && rsa.VerifyData(
                Encoding.UTF8.GetBytes(canonical), signature,
                version == "2" ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA1,
                RSASignaturePadding.Pkcs1);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return false;
        }
    }

    private bool TryReadEnvelope(JsonElement envelope, out string type, out string version,
        out string signature, out string certificateUrl)
    {
        type = version = signature = certificateUrl = string.Empty;
        return Read(envelope, "Type", out type) && type is "Notification" or "SubscriptionConfirmation" &&
            Read(envelope, TopicArnField, out var topicArn) && !string.IsNullOrWhiteSpace(_settings.TopicArn) &&
            string.Equals(topicArn, _settings.TopicArn, StringComparison.Ordinal) &&
            Read(envelope, "SignatureVersion", out version) && version is "1" or "2" &&
            Read(envelope, "Signature", out signature) &&
            Read(envelope, "SigningCertURL", out certificateUrl) && IsSnsUrl(certificateUrl, certificate: true);
    }

    private static bool TryBuildCanonicalMessage(JsonElement envelope, string type, out string canonical)
    {
        var fields = type == "Notification"
            ? new[] { "Message", "MessageId", "Subject", "Timestamp", TopicArnField, "Type" }
            : new[] { "Message", "MessageId", "SubscribeURL", "Timestamp", "Token", TopicArnField, "Type" };
        var builder = new StringBuilder();
        foreach (var field in fields)
        {
            if (!Read(envelope, field, out var value))
            {
                if (field == "Subject" && type == "Notification")
                    continue;
                canonical = string.Empty;
                return false;
            }
            builder.Append(field).Append('\n').Append(value).Append('\n');
        }
        canonical = builder.ToString();
        return true;
    }

    private async Task<string?> GetCertificatePemAsync(string url, CancellationToken cancellationToken)
    {
        if (certificateCache.TryGetValue(url, out string? cached))
            return cached;

        var fetchLock = CertificateFetchLocks[(int)((uint)url.GetHashCode() % CertificateFetchLocks.Length)];
        await fetchLock.WaitAsync(cancellationToken);
        try
        {
            if (certificateCache.TryGetValue(url, out cached))
                return cached;
            try
            {
                using var response = await httpClientFactory.CreateClient("SnsCertificate").GetAsync(url, cancellationToken);
                response.EnsureSuccessStatusCode();
                var pem = await response.Content.ReadAsStringAsync(cancellationToken);
                certificateCache.Set(url, pem, CertificateCacheDuration);
                return pem;
            }
            catch (HttpRequestException ex)
            {
                throw new SnsCertificateFetchException("SNS signing certificate is unavailable", ex);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new SnsCertificateFetchException("SNS signing certificate is unavailable", ex);
            }
        }
        finally
        {
            fetchLock.Release();
        }
    }

    private bool IsTrustedCertificate(X509Certificate2 certificate)
    {
        using var chain = new X509Chain();
        if (_trustedRoot is not null)
        {
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(_trustedRoot);
        }
        return chain.Build(certificate);
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
                CertificatePathRegex().IsMatch(url.AbsolutePath);
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
            parameters.TryGetValue(TopicArnField, out var actualTopic) && actualTopic == topicArn &&
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

    [GeneratedRegex(@"^/SimpleNotificationService-[0-9a-fA-F]+\.pem$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex CertificatePathRegex();
}

public sealed class SnsCertificateFetchException(string message, Exception innerException)
    : Exception(message, innerException);
