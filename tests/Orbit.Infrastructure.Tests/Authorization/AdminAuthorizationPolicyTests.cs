using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Orbit.Application.Common;

namespace Orbit.Infrastructure.Tests.Authorization;

public class AdminAuthorizationPolicyTests
{
    private static IAuthorizationService BuildAuthorizationService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(options =>
            options.AddPolicy(AdminPolicy.Name, policy =>
                policy.RequireClaim(AdminPolicy.ClaimType, AdminPolicy.ClaimValue)));

        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "Test"));

    [Fact]
    public async Task AdminClaim_IsAllowed()
    {
        var principal = Principal(
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(AdminPolicy.ClaimType, AdminPolicy.ClaimValue));

        var result = await BuildAuthorizationService().AuthorizeAsync(principal, resource: null, AdminPolicy.Name);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task AuthenticatedNonAdmin_IsDenied()
    {
        var principal = Principal(new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()));

        var result = await BuildAuthorizationService().AuthorizeAsync(principal, resource: null, AdminPolicy.Name);

        result.Succeeded.Should().BeFalse();
    }
}
