using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Api.Authentication;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Tests.Authentication;

public class ApiKeyAuthenticationHandlerTests
{
    private static readonly Guid TestUserId = Guid.NewGuid();

    private static async Task<AuthenticateResult> RunHandler(string? authorizationHeader)
    {
        var apiKeyRepo = Substitute.For<IGenericRepository<ApiKey>>();
        var unitOfWork = Substitute.For<IUnitOfWork>();

        // Set up service provider
        var services = new ServiceCollection();
        services.AddSingleton(apiKeyRepo);
        services.AddSingleton(unitOfWork);
        var serviceProvider = services.BuildServiceProvider();

        var optionsMonitor = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        optionsMonitor.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());

        var loggerFactory = new NullLoggerFactory();
        var encoder = UrlEncoder.Default;

        var handler = new ApiKeyAuthenticationHandler(optionsMonitor, loggerFactory, encoder, serviceProvider);

        var scheme = new AuthenticationScheme("ApiKey", "ApiKey", typeof(ApiKeyAuthenticationHandler));
        var httpContext = new DefaultHttpContext();

        if (authorizationHeader is not null)
            httpContext.Request.Headers.Authorization = authorizationHeader;

        await handler.InitializeAsync(scheme, httpContext);
        return await handler.AuthenticateAsync();
    }

    [Fact]
    public async Task HandleAuthenticateAsync_MissingHeader_ReturnsFail()
    {
        var result = await RunHandler(null);

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Contain("Not an API key");
    }

    [Fact]
    public async Task HandleAuthenticateAsync_EmptyHeader_ReturnsFail()
    {
        var result = await RunHandler("");

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAuthenticateAsync_NonApiKeyBearer_ReturnsFail()
    {
        // A standard JWT token does not start with "orb_"
        var result = await RunHandler("Bearer eyJhbGciOiJIUzI1NiJ9.test");

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Contain("Not an API key");
    }

    [Fact]
    public async Task HandleAuthenticateAsync_TooShortApiKey_ReturnsFail()
    {
        var result = await RunHandler("Bearer orb_short");

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Contain("Invalid API key format");
    }

    [Fact]
    public async Task HandleAuthenticateAsync_ValidFormatButNoMatch_ReturnsFail()
    {
        var apiKeyRepo = Substitute.For<IGenericRepository<ApiKey>>();
        var unitOfWork = Substitute.For<IUnitOfWork>();

        // Return empty list - no matching keys
        apiKeyRepo.FindTrackedAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<ApiKey, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<ApiKey>());

        var services = new ServiceCollection();
        services.AddSingleton(apiKeyRepo);
        services.AddSingleton(unitOfWork);
        var serviceProvider = services.BuildServiceProvider();

        var optionsMonitor = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        optionsMonitor.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());

        var handler = new ApiKeyAuthenticationHandler(
            optionsMonitor, new NullLoggerFactory(), UrlEncoder.Default, serviceProvider);

        var scheme = new AuthenticationScheme("ApiKey", "ApiKey", typeof(ApiKeyAuthenticationHandler));
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Authorization = "Bearer orb_testkey12345678";

        await handler.InitializeAsync(scheme, httpContext);
        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Contain("Invalid API key");
    }
}
