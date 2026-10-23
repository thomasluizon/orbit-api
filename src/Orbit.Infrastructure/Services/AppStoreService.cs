using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Domain.Enums;

namespace Orbit.Infrastructure.Services;

public sealed class AppStoreService(HttpClient http, IOptions<AppStoreSettings> settings, AppStoreJwsVerifier verifier) : IAppStoreService
{
    private readonly AppStoreSettings _settings = settings.Value;
    public bool IsConfigured => _settings.IsConfigured;

    public AppStoreTransaction VerifyTransaction(string signedTransaction)
    {
        EnsureConfigured();
        using var payload = verifier.Verify(signedTransaction);
        try
        {
            var json = payload.RootElement;
            var productId = json.GetProperty("productId").GetString() ?? "";
            var interval = _settings.IntervalFor(productId);
            var environment = json.GetProperty("environment").GetString() ?? "";
            var originalId = json.GetProperty("originalTransactionId").GetString() ?? "";
            var transactionId = json.GetProperty("transactionId").GetString() ?? "";
            if (json.GetProperty("bundleId").GetString() != _settings.BundleId || interval is null
                || environment is not ("Sandbox" or "Production") || originalId.Length is 0 or > 255 || transactionId.Length is 0 or > 255
                || json.GetProperty("type").GetString() != "Auto-Renewable Subscription"
                || json.GetProperty("inAppOwnershipType").GetString() != "PURCHASED"
                || !Guid.TryParse(json.GetProperty("appAccountToken").GetString(), out var accountToken) || accountToken == Guid.Empty)
                throw new AppleVerificationException();
            return new(originalId, transactionId, accountToken, environment, productId,
                Instant(json, "expiresDate"), Instant(json, "signedDate"), json.TryGetProperty("revocationDate", out _), interval.Value);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            throw new AppleVerificationException();
        }
    }

    public AppStoreNotification VerifyNotification(string signedPayload)
    {
        EnsureConfigured();
        using var payload = verifier.Verify(signedPayload);
        try
        {
            var json = payload.RootElement;
            var uuid = Guid.Parse(json.GetProperty("notificationUUID").GetString()!);
            if (uuid == Guid.Empty || json.GetProperty("version").GetString() != "2.0")
                throw new AppleVerificationException();
            var data = json.GetProperty("data");
            var environment = data.GetProperty("environment").GetString() ?? "";
            if (environment is not ("Sandbox" or "Production") || data.GetProperty("bundleId").GetString() != _settings.BundleId
                || environment == "Production" && data.GetProperty("appAppleId").GetInt64() != _settings.AppAppleId)
                throw new AppleVerificationException();
            var transaction = data.TryGetProperty("signedTransactionInfo", out var signed)
                ? VerifyTransaction(signed.GetString()!) : null;
            if (transaction is not null && transaction.Environment != environment)
                throw new AppleVerificationException();
            return new(uuid, json.GetProperty("notificationType").GetString() ?? "", environment, transaction);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            throw new AppleVerificationException();
        }
    }

    public async Task<AppStoreSubscriptionState?> GetCurrentStateAsync(AppStoreTransaction transaction, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var host = transaction.Environment == "Sandbox" ? "api.storekit-sandbox.apple.com" : "api.storekit.apple.com";
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/inApps/v1/subscriptions/{Uri.EscapeDataString(transaction.OriginalTransactionId)}");
        var now = DateTimeOffset.UtcNow;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AppleJwt.Sign(_settings.KeyId, _settings.PrivateKey,
            new { iss = _settings.IssuerId, iat = now.ToUnixTimeSeconds(), exp = now.AddMinutes(5).ToUnixTimeSeconds(), aud = "appstoreconnect-v1", bid = _settings.BundleId }));
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new BillingProviderException("App Store subscription lookup failed.");
            using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var json = payload.RootElement;
            if (json.GetProperty("bundleId").GetString() != _settings.BundleId || json.GetProperty("environment").GetString() != transaction.Environment)
                throw new AppleVerificationException();
            foreach (var group in json.GetProperty("data").EnumerateArray())
                foreach (var item in group.GetProperty("lastTransactions").EnumerateArray())
                {
                    if (item.GetProperty("originalTransactionId").GetString() != transaction.OriginalTransactionId)
                        continue;
                    var current = VerifyTransaction(item.GetProperty("signedTransactionInfo").GetString()!);
                    if (current.OriginalTransactionId != transaction.OriginalTransactionId || current.Environment != transaction.Environment
                        || current.AppAccountToken != transaction.AppAccountToken)
                        throw new AppleVerificationException();
                    using var renewal = verifier.Verify(item.GetProperty("signedRenewalInfo").GetString()!);
                    var info = renewal.RootElement;
                    if (info.GetProperty("originalTransactionId").GetString() != current.OriginalTransactionId
                        || info.GetProperty("environment").GetString() != current.Environment
                        || _settings.IntervalFor(info.GetProperty("productId").GetString() ?? "") is null)
                        throw new AppleVerificationException();
                    var status = item.GetProperty("status").GetInt32();
                    var grace = status == 4;
                    var expiresAtUtc = grace ? Instant(info, "gracePeriodExpiresDate") : current.ExpiresAtUtc;
                    var grantsPro = status is 1 or 4 && !current.IsRevoked && expiresAtUtc > now.UtcDateTime;
                    var signedAtUtc = Instant(info, "signedDate");
                    return new(current, grantsPro, expiresAtUtc, signedAtUtc > current.SignedAtUtc ? signedAtUtc : current.SignedAtUtc,
                        grace, info.GetProperty("autoRenewStatus").GetInt32() == 1);
                }
            return null;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested
            && exception is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            throw new BillingProviderException("App Store subscription lookup could not complete.");
        }
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
            throw new BillingProviderException("App Store is not configured.");
    }

    private static DateTime Instant(JsonElement json, string name) => DateTimeOffset.FromUnixTimeMilliseconds(json.GetProperty(name).GetInt64()).UtcDateTime;
}
