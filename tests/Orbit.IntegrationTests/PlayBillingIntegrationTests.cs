using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Orbit.Application.Common;
using Orbit.Domain.Enums;

namespace Orbit.IntegrationTests;

[Collection("Sequential")]
public class PlayBillingIntegrationTests : IAsyncLifetime
{
    private readonly IntegrationTestWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly string _email = $"play-test-{Guid.NewGuid()}@integration.test";
    private const string TestCode = "999999";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private Guid _userId;

    public PlayBillingIntegrationTests(IntegrationTestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        IntegrationTestHelpers.RegisterTestAccount(_email, TestCode);
    }

    public async Task InitializeAsync()
    {
        var login = await IntegrationTestHelpers.AuthenticateWithCodeAsync(_client, _email, TestCode, JsonOptions);
        _userId = login.UserId;
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task VerifyPlayPurchase_ActivePurchase_GrantsProAndStatusReflectsPlaySource()
    {
        _factory.PlayBilling.NextState = new PlaySubscriptionState(
            true, DateTime.UtcNow.AddMonths(1), SubscriptionInterval.Yearly, false, "orbit_pro", null, _userId.ToString());

        var response = await _client.PostAsJsonAsync(
            "/api/subscriptions/play/verify",
            new { productId = "orbit_pro", purchaseToken = $"tok_{Guid.NewGuid():N}" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PlayVerifyResult>(JsonOptions);
        body.Should().NotBeNull();
        body!.HasProAccess.Should().BeTrue();
        body.Source.Should().Be("play");
        _factory.PlayBilling.AcknowledgeCalled.Should().BeTrue();

        var status = await _client.GetFromJsonAsync<StatusResult>("/api/subscriptions/status", JsonOptions);
        status.Should().NotBeNull();
        status!.HasProAccess.Should().BeTrue();
        status.Source.Should().Be("play");
    }

    [Fact]
    public async Task VerifyPlayPurchase_InactivePurchase_DoesNotGrantPro()
    {
        _factory.PlayBilling.NextState = new PlaySubscriptionState(
            false, DateTime.UtcNow.AddMonths(-1), null, false, "orbit_pro", null, null);

        var response = await _client.PostAsJsonAsync(
            "/api/subscriptions/play/verify",
            new { productId = "orbit_pro", purchaseToken = $"tok_{Guid.NewGuid():N}" });

        response.IsSuccessStatusCode.Should().BeFalse();
    }

    [Fact]
    public async Task HandlePlayNotification_InvalidPushToken_ReturnsUnauthorized()
    {
        _factory.PushTokenValidator.IsValid = false;
        try
        {
            var response = await _client.PostAsync(
                "/api/subscriptions/play/rtdn",
                new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        finally
        {
            _factory.PushTokenValidator.IsValid = true;
        }
    }

    [Fact]
    public async Task HandlePlayNotification_ValidPushToken_CancelsEntitlementEndToEnd()
    {
        _factory.PushTokenValidator.IsValid = true;
        var token = $"tok_rtdn_{Guid.NewGuid():N}";

        _factory.PlayBilling.NextState = new PlaySubscriptionState(
            true, DateTime.UtcNow.AddMonths(1), SubscriptionInterval.Monthly, false, "orbit_pro", null, _userId.ToString());
        var verify = await _client.PostAsJsonAsync(
            "/api/subscriptions/play/verify",
            new { productId = "orbit_pro", purchaseToken = token });
        verify.StatusCode.Should().Be(HttpStatusCode.OK);

        _factory.PlayBilling.NextState = new PlaySubscriptionState(
            false, DateTime.UtcNow.AddDays(-1), null, false, "orbit_pro", null, null);
        var rtdn = await _client.PostAsync(
            "/api/subscriptions/play/rtdn",
            new StringContent(BuildRtdnEnvelope(token, $"msg_{Guid.NewGuid():N}"), System.Text.Encoding.UTF8, "application/json"));

        rtdn.StatusCode.Should().Be(HttpStatusCode.OK);
        var status = await _client.GetFromJsonAsync<StatusResult>("/api/subscriptions/status", JsonOptions);
        status.Should().NotBeNull();
        status!.Source.Should().BeNull();
    }

    private static string BuildRtdnEnvelope(string purchaseToken, string messageId)
    {
        var developerNotification = JsonSerializer.Serialize(new
        {
            version = "1.0",
            packageName = "org.useorbit.app",
            eventTimeMillis = "1700000000000",
            subscriptionNotification = new { version = "1.0", notificationType = 13, purchaseToken, subscriptionId = "orbit_pro" },
        });
        var data = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(developerNotification));
        return JsonSerializer.Serialize(new { message = new { data, messageId }, subscription = "projects/x/subscriptions/y" });
    }

    private record PlayVerifyResult(bool HasProAccess, string? Source, string? SubscriptionInterval, DateTime? PlanExpiresAt);
    private record StatusResult(string Plan, bool HasProAccess, string? Source);
}
