using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.IntegrationTests;

public sealed class IntegrationTestWebApplicationFactory : WebApplicationFactory<Program>
{
    private static int _clientCounter;

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

    public CapturingEmailService Email { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Jwt:SecretKey", "OrbitIntegrationTestSecretKey-0123456789-ABCDEF");

        builder.ConfigureTestServices(services =>
        {
            services.AddScoped<IBillingService>(_ => BillingService);
            services.RemoveAll<IEmailService>();
            services.AddSingleton<IEmailService>(Email);
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

public sealed class CapturingEmailService : IEmailService
{
    public string? LastVerificationCode { get; private set; }

    public Task SendWelcomeEmailAsync(string toEmail, string userName, string language = "en", CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task SendVerificationCodeAsync(string toEmail, string code, string language = "en", CancellationToken cancellationToken = default)
    {
        LastVerificationCode = code;
        return Task.CompletedTask;
    }

    public Task SendSupportEmailAsync(string fromName, string fromEmail, string subject, string message, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task SendAccountDeletionCodeAsync(string toEmail, string code, string language = "en", CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
