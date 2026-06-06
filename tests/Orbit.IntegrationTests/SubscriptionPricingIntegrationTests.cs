using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Orbit.IntegrationTests;

[Collection("Sequential")]
public class SubscriptionPricingIntegrationTests : IAsyncLifetime
{
    private readonly IntegrationTestWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly string _email = $"pricing-test-{Guid.NewGuid()}@integration.test";
    private const string TestCode = "999999";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public SubscriptionPricingIntegrationTests(IntegrationTestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        IntegrationTestHelpers.RegisterTestAccount(_email, TestCode);
    }

    public async Task InitializeAsync()
    {
        await IntegrationTestHelpers.AuthenticateWithCodeAsync(_client, _email, TestCode, JsonOptions);
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData("BR", "yearly", "price_test_yearly_brl")]
    [InlineData("BR", "monthly", "price_test_monthly_brl")]
    [InlineData("US", "yearly", "price_test_yearly_usd")]
    [InlineData("US", "monthly", "price_test_monthly_usd")]
    public async Task Checkout_ResolvesPriceIdForCountryAndInterval(string countryCode, string interval, string expectedPriceId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/checkout")
        {
            Content = JsonContent.Create(new { interval })
        };
        request.Headers.Add("X-Orbit-Country-Code", countryCode);

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.BillingService.LastCheckoutPriceId.Should().Be(expectedPriceId);
    }

    [Theory]
    [InlineData("BR", "brl")]
    [InlineData("US", "usd")]
    public async Task Plans_ReturnsCurrencyForCountry(string countryCode, string expectedCurrency)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/subscriptions/plans");
        request.Headers.Add("X-Orbit-Country-Code", countryCode);

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var plans = await response.Content.ReadFromJsonAsync<PlansResponse>(JsonOptions);
        plans.Should().NotBeNull();
        plans!.Currency.Should().Be(expectedCurrency);
        plans.Monthly.Currency.Should().Be(expectedCurrency);
        plans.Yearly.Currency.Should().Be(expectedCurrency);
    }

    [Fact]
    public async Task Plans_NoCountrySignal_DefaultsToUsd()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Remove("X-Forwarded-For");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Forwarded-For", "10.0.0.1");
        await IntegrationTestHelpers.AuthenticateWithCodeAsync(client, _email, TestCode, JsonOptions);

        var response = await client.GetAsync("/api/subscriptions/plans");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var plans = await response.Content.ReadFromJsonAsync<PlansResponse>(JsonOptions);
        plans!.Currency.Should().Be("usd");
    }

    private record PlanPriceDto(long UnitAmount, string Currency);
    private record PlansResponse(PlanPriceDto Monthly, PlanPriceDto Yearly, int SavingsPercent, int? CouponPercentOff, string Currency);
}
