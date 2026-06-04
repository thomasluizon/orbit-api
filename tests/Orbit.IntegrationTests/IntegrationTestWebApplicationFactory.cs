using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Orbit.Application.Common;

namespace Orbit.IntegrationTests;

public sealed class IntegrationTestWebApplicationFactory : WebApplicationFactory<Program>
{
    private static int _clientCounter;

    // Connection string, Stripe price IDs, Encryption key, and JWT issuer/audience are validated
    // (or consumed by the DbContext) during the builder phase in Program.cs, so they are supplied
    // via environment variables read by the default config providers. This keeps the integration
    // host hermetic and independent of the gitignored appsettings.Development.json, which is absent
    // in git worktrees. The JWT secret is injected separately via UseSetting in ConfigureWebHost.
    static IntegrationTestWebApplicationFactory()
    {
        SetIfMissing("ConnectionStrings__DefaultConnection", "Host=localhost;Port=5432;Database=orbit_test;Username=postgres;Password=postgres");
        SetIfMissing("Jwt__Issuer", "OrbitTestApi");
        SetIfMissing("Jwt__Audience", "OrbitTestClient");
        SetIfMissing("Encryption__Key", "DdyUCjjdK326cB9lY00tyUvRDpCQcYJOJIpu21I1D8c=");
        SetIfMissing("Stripe__MonthlyPriceIdUsd", "price_test_monthly_usd");
        SetIfMissing("Stripe__YearlyPriceIdUsd", "price_test_yearly_usd");
        SetIfMissing("Stripe__MonthlyPriceIdBrl", "price_test_monthly_brl");
        SetIfMissing("Stripe__YearlyPriceIdBrl", "price_test_yearly_brl");
        SetIfMissing("Stripe__SuccessUrl", "https://app.test/success");
        SetIfMissing("Stripe__CancelUrl", "https://app.test/cancel");
    }

    /// <summary>
    /// Captures the price ID passed to checkout and serves fixed plan amounts so billing tests
    /// never reach live Stripe. Shared across the Sequential collection; pricing tests read it
    /// back immediately after their own request.
    /// </summary>
    public CapturingBillingService BillingService { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Jwt:SecretKey", "OrbitIntegrationTestSecretKey-0123456789-ABCDEF");

        builder.ConfigureTestServices(services =>
        {
            services.AddScoped<IBillingService>(_ => BillingService);
        });
    }

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);

        client.DefaultRequestHeaders.Remove("X-Forwarded-For");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Forwarded-For", BuildClientIpAddress());
    }

    private static void SetIfMissing(string name, string value)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
            Environment.SetEnvironmentVariable(name, value);
    }

    private static string BuildClientIpAddress()
    {
        var clientNumber = Interlocked.Increment(ref _clientCounter);
        var thirdOctet = (clientNumber / 254) % 254;
        var fourthOctet = clientNumber % 254;

        if (fourthOctet == 0)
            fourthOctet = 1;

        return $"198.51.{thirdOctet}.{fourthOctet}";
    }
}
