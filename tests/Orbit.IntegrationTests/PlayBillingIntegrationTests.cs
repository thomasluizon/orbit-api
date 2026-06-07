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

    private record PlayVerifyResult(bool HasProAccess, string? Source, string? SubscriptionInterval, DateTime? PlanExpiresAt);
    private record StatusResult(string Plan, bool HasProAccess, string? Source);
}
