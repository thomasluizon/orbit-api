using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Orbit.Domain.Entities;
using Orbit.Api.OAuth;

namespace Orbit.Infrastructure.Tests.Controllers;

public partial class OAuthControllerTests
{
    [Fact]
    public async Task Token_AuthorizationCodeOlderThanFiveMinutes_ReturnsInvalidGrant()
    {
        var (verifier, challenge) = GeneratePkce();
        var code = _authStore.CreateCode(UserId, challenge, OAuthTestClient.RedirectUri, OAuthTestClient.ClientId);
        _timeProvider.Advance(TimeSpan.FromMinutes(6));
        AssertInvalidGrant(await _controller.Token("authorization_code", code, verifier, OAuthTestClient.RedirectUri,
            default, client_id: OAuthTestClient.ClientId));
        await AssertNoKeyAdded();
    }

    [Fact]
    public async Task Token_RefreshFamilyExpired_ReturnsInvalidGrant()
    {
        var token = _authStore.CreateRefreshToken(new(UserId, Guid.NewGuid(), OAuthTestClient.ClientId,
            Guid.NewGuid(), _timeProvider.GetUtcNow().UtcDateTime.AddSeconds(-1)));
        AssertInvalidGrant(await _controller.Token("refresh_token", null, null, null, default, token, OAuthTestClient.ClientId));
        await AssertNoKeyAdded();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Token_RefreshWithMissingOrForeignPreviousKey_ReturnsInvalidGrant(bool foreign)
    {
        var key = ApiKey.Create(Guid.NewGuid(), "Other", ["read_habits"]).Value.Entity;
        var token = _authStore.CreateRefreshToken(new(UserId, key.Id, OAuthTestClient.ClientId));
        _apiKeyRepo.GetByIdAsync(key.Id, Arg.Any<CancellationToken>()).Returns(foreign ? key : null);
        AssertInvalidGrant(await _controller.Token("refresh_token", null, null, null, default, token, OAuthTestClient.ClientId));
        await _apiKeyRepo.DidNotReceive().FindOneTrackedAsync(Arg.Any<Expression<Func<ApiKey, bool>>>(),
            Arg.Any<Func<IQueryable<ApiKey>, IQueryable<ApiKey>>?>(), Arg.Any<CancellationToken>());
        await AssertNoKeyAdded();
    }

    [Fact]
    public async Task Token_AllowedButDifferentRedirectUri_ReturnsInvalidGrant()
    {
        var (verifier, challenge) = GeneratePkce();
        var code = _authStore.CreateCode(UserId, challenge, OAuthTestClient.RedirectUri, OAuthTestClient.ClientId);
        AssertInvalidGrant(await _controller.Token("authorization_code", code, verifier, "https://claude.com/callback",
            default, client_id: OAuthTestClient.ClientId));
        await AssertNoKeyAdded();
    }

    private static void AssertInvalidGrant(IActionResult result) => JsonSerializer.SerializeToElement(
        result.Should().BeOfType<BadRequestObjectResult>().Subject.Value).GetProperty("error").GetString().Should().Be("invalid_grant");

    private Task AssertNoKeyAdded() => _apiKeyRepo.DidNotReceive().AddAsync(Arg.Any<ApiKey>(), Arg.Any<CancellationToken>());
}
