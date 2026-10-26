using System.Net;
using System.Reflection;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Api.Extensions;
using Orbit.Api.RateLimiting;
using Orbit.Api.Middleware;
using Microsoft.AspNetCore.Mvc.Controllers;
using System.Security.Claims;
using Orbit.Application.Onboarding.Commands;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Controllers;

public class OnboardingControllerTests
{
    [Fact]
    public async Task Onboarding_IgnoresAccountCredentialsBeforeAuthenticationCanWriteUsage()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer orb_test-secret-key";
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "account-id")], "test"));
        context.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(new ControllerActionDescriptor
        {
            ControllerTypeInfo = typeof(OnboardingController).GetTypeInfo()
        }), "onboarding"));
        var nextCalled = false;
        await new AnonymousOnboardingMiddleware(current =>
        {
            nextCalled = true;
            current.Request.Headers.Authorization.Should().BeEmpty();
            current.User.Identity!.IsAuthenticated.Should().BeFalse();
            current.Response.Headers.CacheControl.ToString().Should().Be("no-store");
            return Task.CompletedTask;
        }).InvokeAsync(context);
        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task AnonymousEndpoint_ReturnsExistingScheduleContractAndRotatedHeader()
    {
        typeof(OnboardingController).GetCustomAttribute<AllowAnonymousAttribute>().Should().NotBeNull();
        var schedule = new HabitSetupSuggestion(null, FrequencyUnit.Day, 1, [], false, null, null, [], []);
        var sender = Substitute.For<ISender>();
        sender.Send(Arg.Any<SuggestOnboardingSetupCommand>(), Arg.Any<CancellationToken>())
            .Returns(new OnboardingSetupResult(schedule, "next-id", null));
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");
        context.Request.Headers["X-Forwarded-For"] = "spoofed";
        var controller = new OnboardingController(sender, new BffClientIpResolver(Options.Create(new BffForwardingSettings()))) { ControllerContext = new ControllerContext { HttpContext = context } };
        var response = await controller.SuggestSetup(new("Read", "en", new("web", "token")), "signed-id", default);
        response.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(schedule);
        context.Response.Headers[OnboardingController.InstallHeader].ToString().Should().Be("next-id");
        await sender.Received(1).Send(Arg.Is<SuggestOnboardingSetupCommand>(request => request.ClientIp == "192.0.2.1"), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("ONBOARDING_LIMIT", 429)]
    [InlineData("ONBOARDING_UNAVAILABLE", 503)]
    public async Task Failures_AreTypedCodesWithoutCopy(string code, int status)
    {
        var sender = Substitute.For<ISender>();
        sender.Send(Arg.Any<SuggestOnboardingSetupCommand>(), Arg.Any<CancellationToken>())
            .Returns(new OnboardingSetupResult(null, null, code));
        var controller = new OnboardingController(sender, new BffClientIpResolver(Options.Create(new BffForwardingSettings()))) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        var result = (ObjectResult)await controller.SuggestSetup(new("Read", "en", null), null, default);
        result.StatusCode.Should().Be(status);
        result.Value.Should().Be(new OnboardingController.FallbackResponse(code));
    }

    [Fact]
    public void Cors_ContainsOnlyAppLandingAndDesktopOrigins()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OnboardingSecurity:AllowedOrigins:0"] = "https://app.example",
            ["OnboardingSecurity:AllowedOrigins:1"] = "https://landing.example",
            ["OnboardingSecurity:AllowedOrigins:2"] = "https://desktop.example",
            ["Cors:ThirdPartyOrigins:0"] = "https://other.example"
        });
        Orbit.Api.Extensions.ServiceCollectionExtensions.AddCorsPolicies(builder);
        using var provider = builder.Services.BuildServiceProvider();
        var policy = provider.GetRequiredService<IOptions<CorsOptions>>().Value.GetPolicy("Onboarding")!;
        policy.Origins.Should().BeEquivalentTo("https://app.example", "https://landing.example", "https://desktop.example");
        policy.Methods.Should().BeEquivalentTo("POST", "OPTIONS");
        policy.Headers.Should().Contain(OnboardingController.InstallHeader);
        policy.AllowAnyOrigin.Should().BeFalse();
    }

    [Fact]
    public async Task ExceptionAndValidationResponses_AreTypedAndNeverCached()
    {
        var action = new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor());
        var filter = new OnboardingEndpointFilter();
        var exception = new ExceptionContext(action, []) { Exception = new HttpRequestException("sensitive input") };
        await filter.OnExceptionAsync(exception);
        exception.ExceptionHandled.Should().BeTrue();
        var result = new ResultExecutingContext(action, [], exception.Result!, new object());
        filter.OnResultExecuting(result);
        action.HttpContext.Response.Headers.CacheControl.ToString().Should().Be("no-store");
        ((ObjectResult)result.Result).Value.Should().Be(new OnboardingController.FallbackResponse("ONBOARDING_LIMIT"));
    }
}
