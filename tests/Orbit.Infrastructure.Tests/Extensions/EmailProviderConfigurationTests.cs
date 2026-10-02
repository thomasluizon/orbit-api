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
    [Fact]
    public void EmailAlwaysUsesSesWithoutProviderSetting()
    {
        var builder = Builder();
        OrbitServiceCollectionExtensions.AddEmailAndSupabaseClients(builder, TimeSpan.FromSeconds(5));
        builder.Services.Single(service => service.ServiceType == typeof(IEmailService))
            .ImplementationType.Should().Be(typeof(SesEmailService));
        using var services = builder.Services.BuildServiceProvider();
        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IEmailService>().Should().BeOfType<SesEmailService>();
    }

    [Theory]
    [InlineData("Ses:AccessKeyId")]
    [InlineData("Ses:SecretAccessKey")]
    [InlineData("Ses:Region")]
    [InlineData("Ses:FromEmail")]
    [InlineData("Ses:SupportEmail")]
    [InlineData("Ses:MarketingFromEmail")]
    [InlineData("Ses:TransactionalConfigurationSet")]
    [InlineData("Ses:MarketingConfigurationSet")]
    [InlineData("Ses:TopicArn")]
    public void MissingSesSettingFailsDuringRegistration(string key)
    {
        var builder = Builder();
        builder.Configuration[key] = null;
        var action = () => OrbitServiceCollectionExtensions.AddEmailAndSupabaseClients(builder, TimeSpan.FromSeconds(5));
        action.Should().Throw<InvalidOperationException>().WithMessage($"Configuration key '{key}' is missing or empty.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankSesCredentialsFailDuringRegistration(string value)
    {
        var builder = Builder();
        builder.Configuration["Ses:SecretAccessKey"] = value;
        var action = () => OrbitServiceCollectionExtensions.AddEmailAndSupabaseClients(builder, TimeSpan.FromSeconds(5));
        action.Should().Throw<InvalidOperationException>()
            .WithMessage("Configuration key 'Ses:SecretAccessKey' is missing or empty.");
    }

    [Fact]
    public void MissingSesSectionFailsDuringRegistration()
    {
        var builder = Builder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Supabase:Url"] = "https://example.supabase.co",
            ["Supabase:AnonKey"] = "anon",
            ["Supabase:SecretKey"] = "secret",
        });
        var action = () => OrbitServiceCollectionExtensions.AddEmailAndSupabaseClients(builder, TimeSpan.FromSeconds(5));
        action.Should().Throw<InvalidOperationException>()
            .WithMessage("Configuration key 'Ses:AccessKeyId' is missing or empty.");
    }

    private static WebApplicationBuilder Builder()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Supabase:Url"] = "https://example.supabase.co",
            ["Supabase:AnonKey"] = "anon",
            ["Supabase:SecretKey"] = "secret",
            ["Ses:AccessKeyId"] = "key",
            ["Ses:SecretAccessKey"] = "secret",
            ["Ses:Region"] = "us-east-2",
            ["Ses:FromEmail"] = "Orbit <noreply@send.useorbit.org>",
            ["Ses:SupportEmail"] = "contact@useorbit.org",
            ["Ses:MarketingFromEmail"] = "Orbit <news@updates.useorbit.org>",
            ["Ses:TransactionalConfigurationSet"] = "orbit-transactional",
            ["Ses:MarketingConfigurationSet"] = "orbit-marketing",
            ["Ses:TopicArn"] = "arn:aws:sns:us-east-2:713285551626:orbit-ses-events",
        });
        return builder;
    }
}
