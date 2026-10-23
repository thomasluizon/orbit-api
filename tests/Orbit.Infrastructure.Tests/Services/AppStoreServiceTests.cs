using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public sealed class AppStoreServiceTests : IDisposable
{
    private readonly AppleSigningFixture _signer = new();
    private readonly ECDsa _apiKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly Guid _userId = Guid.NewGuid();
    private AppStoreSettings Settings => new()
    {
        KeyId = "KEY",
        IssuerId = "ISSUER",
        PrivateKey = _apiKey.ExportPkcs8PrivateKeyPem(),
        BundleId = "org.useorbit.app",
        AppAppleId = 123,
        MonthlyProductId = "pro.monthly",
        YearlyProductId = "pro.yearly"
    };

    private object Transaction(string bundleId = "org.useorbit.app", string productId = "pro.monthly", bool revoked = false) => new
    {
        originalTransactionId = "1000000001",
        transactionId = "1000000002",
        appAccountToken = _userId.ToString(),
        bundleId,
        productId,
        environment = "Sandbox",
        type = "Auto-Renewable Subscription",
        inAppOwnershipType = "PURCHASED",
        signedDate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        expiresDate = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds(),
        revocationDate = revoked ? (long?)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : null
    };

    private string SignedTransaction(string bundleId = "org.useorbit.app", string productId = "pro.monthly", bool revoked = false)
    {
        var json = JsonSerializer.SerializeToElement(Transaction(bundleId, productId, revoked));
        var fields = json.EnumerateObject().Where(property => property.Value.ValueKind != JsonValueKind.Null)
            .ToDictionary(property => property.Name, property => property.Value);
        return _signer.Sign(fields);
    }

    private AppStoreService Service(HttpMessageHandler? handler = null) => new(new HttpClient(handler ?? new ResponseHandler(_ => new(HttpStatusCode.OK))),
        Options.Create(Settings), _signer.Verifier);

    [Fact]
    public void ProductionVerifier_UsesEmbeddedAppleRootAndRejectsTestChain()
    {
        var verifier = new AppStoreJwsVerifier();
        var act = () => verifier.Verify(SignedTransaction());
        act.Should().Throw<AppleVerificationException>();
    }

    [Fact]
    public void VerifyTransaction_TrustedChain_ReadsDocumentedClaims()
    {
        var result = Service().VerifyTransaction(SignedTransaction());
        result.AppAccountToken.Should().Be(_userId);
        result.Environment.Should().Be("Sandbox");
        result.IsRevoked.Should().BeFalse();
    }

    [Theory]
    [InlineData("other.bundle", "pro.monthly")]
    [InlineData("org.useorbit.app", "other.product")]
    public void VerifyTransaction_WrongIdentifiers_Rejects(string bundle, string product)
    {
        var act = () => Service().VerifyTransaction(SignedTransaction(bundle, product));
        act.Should().Throw<AppleVerificationException>();
    }

    [Fact]
    public void VerifyTransaction_ForgedSignature_Rejects()
    {
        var parts = SignedTransaction().Split('.');
        var signature = AppleJwt.Decode(parts[2]);
        signature[0] ^= 1;
        var act = () => Service().VerifyTransaction($"{parts[0]}.{parts[1]}.{AppleJwt.Encode(signature)}");
        act.Should().Throw<AppleVerificationException>();
    }

    [Fact]
    public void VerifyTransaction_UntrustedRoot_Rejects()
    {
        using var other = new AppleSigningFixture();
        var act = () => other.Verifier.Verify(SignedTransaction());
        act.Should().Throw<AppleVerificationException>();
    }

    [Theory]
    [InlineData(1, false, true)]
    [InlineData(4, false, true)]
    [InlineData(3, false, false)]
    [InlineData(5, true, false)]
    public async Task GetCurrentState_UsesVerifiedServerState(int status, bool revoked, bool grantsPro)
    {
        var signed = SignedTransaction(revoked: revoked);
        var renewal = _signer.Sign(new
        {
            originalTransactionId = "1000000001",
            environment = "Sandbox",
            productId = "pro.monthly",
            signedDate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            gracePeriodExpiresDate = DateTimeOffset.UtcNow.AddDays(3).ToUnixTimeMilliseconds(),
            autoRenewStatus = 1
        });
        var handler = new ResponseHandler(request =>
        {
            request.RequestUri!.AbsoluteUri.Should().Be("https://api.storekit-sandbox.apple.com/inApps/v1/subscriptions/1000000001");
            request.Headers.Authorization!.Scheme.Should().Be("Bearer");
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    environment = "Sandbox",
                    bundleId = "org.useorbit.app",
                    data = new[] { new { subscriptionGroupIdentifier = "1", lastTransactions = new[]
                { new { originalTransactionId = "1000000001", status, signedTransactionInfo = signed, signedRenewalInfo = renewal } } } }
                }))
            };
        });
        var service = Service(handler);
        var state = await service.GetCurrentStateAsync(service.VerifyTransaction(signed), default);
        state!.GrantsPro.Should().Be(grantsPro);
        state.IsInGracePeriod.Should().Be(status == 4);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task GetCurrentState_TransientFailure_ThrowsForRetry(HttpStatusCode status)
    {
        var service = Service(new ResponseHandler(_ => new(status)));
        var act = () => service.GetCurrentStateAsync(service.VerifyTransaction(SignedTransaction()), default);
        await act.Should().ThrowAsync<BillingProviderException>();
    }

    [Fact]
    public void VerifyNotification_VerifiesOuterAndNestedSignatures()
    {
        var uuid = Guid.NewGuid();
        var payload = _signer.Sign(new
        {
            notificationUUID = uuid.ToString(),
            notificationType = "REFUND",
            version = "2.0",
            signedDate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            data = new { environment = "Sandbox", bundleId = "org.useorbit.app", signedTransactionInfo = SignedTransaction(revoked: true) }
        });
        var result = Service().VerifyNotification(payload);
        result.NotificationUuid.Should().Be(uuid);
        result.Transaction!.IsRevoked.Should().BeTrue();
    }

    public void Dispose() { _signer.Dispose(); _apiKey.Dispose(); }
    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
