using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Orbit.Api.Authorization;
using Orbit.Api.Controllers;
using Orbit.Api.RateLimiting;
using Orbit.Application.Common;

namespace Orbit.Infrastructure.Tests.Controllers;

public class EvalAccountAuthorizationTests
{
    private static readonly Guid Owner = Guid.NewGuid();

    [Theory]
    [InlineData("Staging", "owner", "jwt", true, true)]
    [InlineData("Production", "owner", "jwt", true, true)]
    [InlineData("Production", "other", "jwt", true, false)]
    [InlineData("Production", null, "jwt", true, false)]
    [InlineData("Production", "", "jwt", true, false)]
    [InlineData("Production", "invalid", "jwt", true, false)]
    [InlineData("Production", "owner", "api_key", true, false)]
    [InlineData("Production", "owner", "oauth", true, false)]
    [InlineData("Production", "owner", "jwt", false, false)]
    [InlineData("Custom", "owner", "jwt", true, false)]
    [InlineData("Development", "owner", "jwt", true, false)]
    [InlineData("Staging", null, "jwt", true, false)]
    [InlineData("Staging", "", "jwt", true, false)]
    [InlineData("Staging", "invalid", "jwt", true, false)]
    [InlineData("Staging", "other", "jwt", true, false)]
    [InlineData("Staging", "owner", "api_key", true, false)]
    [InlineData("Staging", "owner", "oauth", true, false)]
    [InlineData("Staging", "owner", "jwt", false, false)]
    public async Task Boundary_RequiresStagingOrProductionAndAnExplicitlyAllowedJwtSession(
        string environmentName, string? allowed, string authMethod, bool authenticated, bool expected)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(environmentName);
        var value = allowed switch { "owner" => $"invalid, {Owner}, {Guid.NewGuid()}", "other" => Guid.NewGuid().ToString(), _ => allowed };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [EvalAccountAccess.AccountIdsKey] = value }).Build();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, Owner.ToString()), new Claim("auth_method", authMethod)
        ], authenticated ? "JwtBearer" : null));
        var requirement = new EvalAccountRequirement();
        var context = new AuthorizationHandlerContext([requirement], principal, null);

        await new EvalAccountAuthorizationHandler(environment, configuration).HandleAsync(context);

        context.HasSucceeded.Should().Be(expected);
    }

    [Fact]
    public void Controller_RequiresTheRestrictedPolicyAndJwtSchemeForBothRateLimitedActions()
    {
        var controller = typeof(EvalAccountController);
        var authorization = controller.GetCustomAttribute<AuthorizeAttribute>()!;
        authorization.Policy.Should().Be(EvalAccountRequirement.PolicyName);
        authorization.AuthenticationSchemes.Should().Be("JwtBearer");
        controller.GetCustomAttribute<Microsoft.AspNetCore.Mvc.ResponseCacheAttribute>()!.NoStore.Should().BeTrue();
        foreach (var action in new[] { nameof(EvalAccountController.GetPendingOperations), nameof(EvalAccountController.ClearPendingOperations) })
            controller.GetMethod(action)!.GetCustomAttribute<DistributedRateLimitAttribute>().Should().NotBeNull();
    }
}
