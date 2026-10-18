using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Orbit.Domain.Interfaces;
using OrbitServiceCollectionExtensions = Orbit.Api.Extensions.ServiceCollectionExtensions;

namespace Orbit.Infrastructure.Tests.Extensions;

public class PushHttpClientConfigurationTests
{
    [Fact]
    public void RegisteredPushClient_DisablesRedirects()
    {
        var builder = WebApplication.CreateBuilder();
        OrbitServiceCollectionExtensions.AddPushAndReferralServices(builder, TimeSpan.FromSeconds(5));
        using var services = builder.Services.BuildServiceProvider();
        var handler = services.GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(nameof(IPushNotificationService));

        while (handler is DelegatingHandler delegatingHandler)
            handler = delegatingHandler.InnerHandler!;

        handler.Should().BeOfType<HttpClientHandler>().Which.AllowAutoRedirect.Should().BeFalse();
    }
}
