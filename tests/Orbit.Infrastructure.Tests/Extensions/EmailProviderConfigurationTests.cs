using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrbitServiceCollectionExtensions = Orbit.Api.Extensions.ServiceCollectionExtensions;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Extensions;

public sealed class EmailProviderConfigurationTests
{
    [Theory]
    [InlineData(null, typeof(ResendEmailService))]
    [InlineData("Resend", typeof(ResendEmailService))]
    [InlineData("Ses", typeof(SesEmailService))]
    public void ProviderSelectsEmailImplementation(string? provider, Type expected)
    {
        var builder = Builder(provider);
        OrbitServiceCollectionExtensions.AddEmailAndSupabaseClients(builder, TimeSpan.FromSeconds(5));
        builder.Services.Single(service => service.ServiceType == typeof(IEmailService))
            .ImplementationType.Should().Be(expected);
    }

    [Fact]
    public void SesRequiresCredentials()
    {
        var builder = Builder("Ses", credentials: false);
        var action = () => OrbitServiceCollectionExtensions.AddEmailAndSupabaseClients(builder, TimeSpan.FromSeconds(5));
        action.Should().Throw<InvalidOperationException>().WithMessage("SES credentials are required*");
    }

    private static WebApplicationBuilder Builder(string? provider, bool credentials = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.Sources.Clear();
        var values = new Dictionary<string, string?>
        {
            ["Supabase:Url"] = "https://example.supabase.co",
            ["Supabase:AnonKey"] = "anon",
            ["Supabase:SecretKey"] = "secret",
            ["Email:Provider"] = provider,
        };
        if (credentials)
        {
            values["Ses:AccessKeyId"] = "key";
            values["Ses:SecretAccessKey"] = "secret";
        }
        builder.Configuration.AddInMemoryCollection(values);
        return builder;
    }
}
