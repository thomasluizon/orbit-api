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

namespace Orbit.Infrastructure.Tests.Controllers;

public class StagingEvalAuthorizationTests
{
    private static readonly Guid Owner = Guid.NewGuid();

    [Theory]
    [InlineData("Staging", "owner", "jwt", true, true)]
    [InlineData("Production", "owner", "jwt", true, false)]
    [InlineData("Development", "owner", "jwt", true, false)]
    [InlineData("Staging", null, "jwt", true, false)]
    [InlineData("Staging", "", "jwt", true, false)]
    [InlineData("Staging", "invalid", "jwt", true, false)]
    [InlineData("Staging", "other", "jwt", true, false)]
    [InlineData("Staging", "owner", "api_key", true, false)]
    [InlineData("Staging", "owner", "oauth", true, false)]
    [InlineData("Staging", "owner", "jwt", false, false)]
    public async Task Boundary_RequiresStagingAndAnExplicitlyAllowedSession(
        string environmentName, string? allowed, string authMethod, bool authenticated, bool expected)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(environmentName);
        var value = allowed switch { "owner" => $"invalid, {Owner}, {Guid.NewGuid()}", "other" => Guid.NewGuid().ToString(), _ => allowed };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [StagingEvalRequirement.AccountIdsKey] = value }).Build();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, Owner.ToString()), new Claim("auth_method", authMethod)
        ], authenticated ? "JwtBearer" : null));
        var requirement = new StagingEvalRequirement();
        var context = new AuthorizationHandlerContext([requirement], principal, null);

        await new StagingEvalAuthorizationHandler(environment, configuration).HandleAsync(context);

        context.HasSucceeded.Should().Be(expected);
    }

    [Fact]
    public void Controller_RequiresTheRestrictedPolicyAndJwtSchemeForBothRateLimitedActions()
    {
        var controller = typeof(StagingEvalController);
        var authorization = controller.GetCustomAttribute<AuthorizeAttribute>()!;
        authorization.Policy.Should().Be(StagingEvalRequirement.PolicyName);
        authorization.AuthenticationSchemes.Should().Be("JwtBearer");
        controller.GetCustomAttribute<Microsoft.AspNetCore.Mvc.ResponseCacheAttribute>()!.NoStore.Should().BeTrue();
        foreach (var action in new[] { nameof(StagingEvalController.GetPendingOperations), nameof(StagingEvalController.ClearPendingOperations) })
            controller.GetMethod(action)!.GetCustomAttribute<DistributedRateLimitAttribute>().Should().NotBeNull();
    }
}
