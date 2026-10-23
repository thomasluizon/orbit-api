using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public sealed partial class AppStoreServiceTests
{
    private string ChangedTransaction(string field, JsonNode? value)
    {
        var payload = JsonSerializer.SerializeToNode(Transaction())!;
        payload.AsObject().Remove("revocationDate");
        payload[field] = value;
        return _signer.Sign(payload);
    }

    [Theory]
    [InlineData("environment", "Xcode")]
    [InlineData("originalTransactionId", "")]
    [InlineData("transactionId", "")]
    [InlineData("type", "Consumable")]
    [InlineData("inAppOwnershipType", "FAMILY_SHARED")]
    [InlineData("appAccountToken", "invalid")]
    [InlineData("appAccountToken", "00000000-0000-0000-0000-000000000000")]
    public void Transaction_UnsupportedOrUnboundPurchase_Rejects(string field, string value)
    {
        FluentActions.Invoking(() => Service().VerifyTransaction(ChangedTransaction(field, JsonValue.Create(value))))
            .Should().Throw<AppleVerificationException>();
    }

    [Theory]
    [InlineData("bundleId")]
    [InlineData("expiresDate")]
    public void Transaction_MissingRequiredClaim_Rejects(string field)
    {
        var payload = JsonSerializer.SerializeToNode(Transaction())!;
        payload.AsObject().Remove(field);
        FluentActions.Invoking(() => Service().VerifyTransaction(_signer.Sign(payload))).Should().Throw<AppleVerificationException>();
    }

    [Fact]
    public void Transaction_YearlyProduct_UsesYearlyInterval()
    {
        Service().VerifyTransaction(SignedTransaction(productId: Settings.YearlyProductId)).Interval
            .Should().Be(Orbit.Domain.Enums.SubscriptionInterval.Yearly);
    }

    private string Notification(string environment = "Sandbox", string version = "2.0", string bundleId = "org.useorbit.app",
        Guid? uuid = null, string? signedTransaction = null, long appleId = 123) => _signer.Sign(new
        {
            notificationUUID = (uuid ?? Guid.NewGuid()).ToString(),
            notificationType = "REFUND_REVERSED",
            version,
            signedDate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            data = new { environment, bundleId, appAppleId = appleId, signedTransactionInfo = signedTransaction }
        });

    [Theory]
    [InlineData("Xcode", "2.0", "org.useorbit.app", 123)]
    [InlineData("Sandbox", "1.0", "org.useorbit.app", 123)]
    [InlineData("Sandbox", "2.0", "other.app", 123)]
    [InlineData("Production", "2.0", "org.useorbit.app", 999)]
    public void Notification_InvalidEnvelope_Rejects(string environment, string version, string bundleId, long appleId)
    {
        FluentActions.Invoking(() => Service().VerifyNotification(Notification(environment, version, bundleId, appleId: appleId)))
            .Should().Throw<AppleVerificationException>();
    }

    [Fact]
    public void Notification_EmptyUuidAndMismatchedTransactionEnvironment_Rejects()
    {
        FluentActions.Invoking(() => Service().VerifyNotification(Notification(uuid: Guid.Empty))).Should().Throw<AppleVerificationException>();
        FluentActions.Invoking(() => Service().VerifyNotification(Notification(environment: "Production", signedTransaction: SignedTransaction())))
            .Should().Throw<AppleVerificationException>();
    }

    [Fact]
    public void Notification_WithoutTransaction_IsVerifiedWithoutPurchaseLookup()
    {
        var payload = new
        {
            notificationUUID = Guid.NewGuid().ToString(),
            notificationType = "TEST",
            version = "2.0",
            signedDate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            data = new { environment = "Sandbox", bundleId = Settings.BundleId }
        };
        Service().VerifyNotification(_signer.Sign(payload)).Transaction.Should().BeNull();
        var production = new
        {
            notificationUUID = Guid.NewGuid().ToString(),
            notificationType = "TEST",
            version = "2.0",
            signedDate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            data = new { environment = "Production", bundleId = Settings.BundleId, appAppleId = Settings.AppAppleId }
        };
        Service().VerifyNotification(_signer.Sign(production)).Environment.Should().Be("Production");
    }

    private string Renewal(string originalId = "1000000001", string environment = "Sandbox", string productId = "pro.monthly") => _signer.Sign(new
    {
        originalTransactionId = originalId,
        environment,
        productId,
        signedDate = DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeMilliseconds(),
        gracePeriodExpiresDate = DateTimeOffset.UtcNow.AddDays(3).ToUnixTimeMilliseconds(),
        autoRenewStatus = 0
    });

    private HttpResponseMessage StatusResponse(string signed, string renewal, string originalId = "1000000001", string bundle = "org.useorbit.app", string environment = "Sandbox") => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            bundleId = bundle,
            environment,
            data = new[]
        { new { subscriptionGroupIdentifier = "1", lastTransactions = new[] { new { originalTransactionId = originalId, status = 1, signedTransactionInfo = signed, signedRenewalInfo = renewal } } } }
        }))
    };

    [Theory]
    [InlineData("bundle")]
    [InlineData("environment")]
    [InlineData("transaction-original")]
    [InlineData("transaction-account")]
    [InlineData("transaction-environment")]
    [InlineData("renewal-original")]
    [InlineData("renewal-environment")]
    [InlineData("renewal-product")]
    public async Task CurrentState_RejectsMismatchedServerBinding(string mismatch)
    {
        var signed = mismatch switch
        {
            "transaction-original" => ChangedTransaction("originalTransactionId", JsonValue.Create("another-original")),
            "transaction-account" => ChangedTransaction("appAccountToken", JsonValue.Create(Guid.NewGuid().ToString())),
            "transaction-environment" => ChangedTransaction("environment", JsonValue.Create("Production")),
            _ => SignedTransaction()
        };
        var renewal = Renewal(mismatch == "renewal-original" ? "another-original" : "1000000001",
            mismatch == "renewal-environment" ? "Production" : "Sandbox", mismatch == "renewal-product" ? "other.product" : "pro.monthly");
        var service = Service(new ResponseHandler(_ => StatusResponse(signed, renewal,
            bundle: mismatch == "bundle" ? "other.app" : Settings.BundleId, environment: mismatch == "environment" ? "Production" : "Sandbox")));
        await FluentActions.Awaiting(() => service.GetCurrentStateAsync(service.VerifyTransaction(SignedTransaction()), default))
            .Should().ThrowAsync<AppleVerificationException>();
    }

    [Fact]
    public async Task CurrentState_UnrelatedOriginal_DoesNotGrantAccess()
    {
        var service = Service(new ResponseHandler(_ => StatusResponse(SignedTransaction(), Renewal(), originalId: "another-original")));
        (await service.GetCurrentStateAsync(service.VerifyTransaction(SignedTransaction()), default)).Should().BeNull();
    }

    [Fact]
    public async Task CurrentState_ExpiredTransactionCannotGrantAndUsesLatestSignedState()
    {
        var signed = ChangedTransaction("expiresDate", JsonValue.Create(DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds()));
        var service = Service(new ResponseHandler(_ => StatusResponse(signed, Renewal())));
        var state = await service.GetCurrentStateAsync(service.VerifyTransaction(signed), default);
        state!.GrantsPro.Should().BeFalse();
        state.AutoRenews.Should().BeFalse();
        state.SignedAtUtc.Should().BeAfter(state.Transaction.SignedAtUtc);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("{}")]
    public async Task CurrentState_MalformedServerResponse_ThrowsForRetry(string json)
    {
        var service = Service(new ResponseHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(json) }));
        await FluentActions.Awaiting(() => service.GetCurrentStateAsync(service.VerifyTransaction(SignedTransaction()), default))
            .Should().ThrowAsync<BillingProviderException>();
    }

    [Theory]
    [InlineData("not-a-jws")]
    [InlineData("e30.e30.invalid")]
    [InlineData("invalid.invalid.invalid")]
    public void Jws_MalformedInput_RejectsWithoutExposingPayload(string signed)
    {
        FluentActions.Invoking(() => _signer.Verifier.Verify(signed)).Should().Throw<AppleVerificationException>()
            .Which.Message.Should().NotContain(signed);
    }

    [Fact]
    public void Jws_FutureSignedDate_Rejects()
    {
        var signed = _signer.Sign(new { signedDate = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds() });
        FluentActions.Invoking(() => _signer.Verifier.Verify(signed)).Should().Throw<AppleVerificationException>();
    }

    [Fact]
    public void MissingConfiguration_DisablesVerification()
    {
        var service = new AppStoreService(new HttpClient(), Options.Create(new AppStoreSettings()), _signer.Verifier);
        service.IsConfigured.Should().BeFalse();
        FluentActions.Invoking(() => service.VerifyTransaction(SignedTransaction())).Should().Throw<BillingProviderException>();
    }
}
