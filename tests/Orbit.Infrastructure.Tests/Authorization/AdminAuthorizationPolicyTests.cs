using System.Linq.Expressions;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using NSubstitute;
using Orbit.Api.Authorization;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Tests.Authorization;

public class AdminAuthorizationPolicyTests
{
    private readonly IGenericRepository<User> _userRepository = Substitute.For<IGenericRepository<User>>();
    private readonly AdminAuthorizationHandler _handler;

    public AdminAuthorizationPolicyTests()
    {
        _handler = new AdminAuthorizationHandler(_userRepository);
    }

    [Fact]
    public async Task AdminUser_Succeeds()
    {
        _userRepository.AnyIgnoringFiltersAsync(Arg.Any<Expression<Func<User, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var context = ContextFor(Principal(new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())));

        await _handler.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task AuthenticatedNonAdmin_DoesNotSucceed()
    {
        _userRepository.AnyIgnoringFiltersAsync(Arg.Any<Expression<Func<User, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(false);
        var context = ContextFor(Principal(new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())));

        await _handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task MissingNameIdentifier_DoesNotSucceed_AndSkipsRepository()
    {
        var context = ContextFor(Principal());

        await _handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
        await _userRepository.DidNotReceive()
            .AnyIgnoringFiltersAsync(Arg.Any<Expression<Func<User, bool>>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnparseableNameIdentifier_DoesNotSucceed_AndSkipsRepository()
    {
        var context = ContextFor(Principal(new Claim(ClaimTypes.NameIdentifier, "not-a-guid")));

        await _handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
        await _userRepository.DidNotReceive()
            .AnyIgnoringFiltersAsync(Arg.Any<Expression<Func<User, bool>>>(), Arg.Any<CancellationToken>());
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "Test"));

    private static AuthorizationHandlerContext ContextFor(ClaimsPrincipal principal) =>
        new([new AdminRequirement()], principal, resource: null);
}
