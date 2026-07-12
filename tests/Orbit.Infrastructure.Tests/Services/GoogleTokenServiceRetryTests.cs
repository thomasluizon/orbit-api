using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class GoogleTokenServiceRetryTests
{
    private static (GoogleTokenService Sut, SequencedHttpMessageHandler Handler, User User) Build(SequencedHttpMessageHandler handler)
    {
        var client = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(GoogleTokenService.HttpClientName).Returns(client);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Google:ClientId"] = "client-id",
                ["Google:ClientSecret"] = "client-secret",
            })
            .Build();

        var user = User.Create("Google Tester", "google@useorbit.org").Value;
        user.SetGoogleTokens("stale-access-token", "refresh-token");

        var sut = new GoogleTokenService(factory, configuration, NullLogger<GoogleTokenService>.Instance);
        return (sut, handler, user);
    }

    [Fact]
    public async Task RetriesThenSucceeds_OnTransient5xx()
    {
        var (sut, handler, user) = Build(new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Status(HttpStatusCode.ServiceUnavailable),
            SequencedHttpMessageHandler.Status(HttpStatusCode.OK, "{\"access_token\":\"fresh-access-token\"}")));

        var outcome = await sut.TryRefreshAsync(user);

        handler.CallCount.Should().Be(2);
        outcome.Result.Should().Be(GoogleTokenRefreshResult.Success);
        outcome.AccessToken.Should().Be("fresh-access-token");
    }

    [Fact]
    public async Task RetriesThenSucceeds_OnTransportException()
    {
        var (sut, handler, user) = Build(new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Throws(),
            SequencedHttpMessageHandler.Status(HttpStatusCode.OK, "{\"access_token\":\"fresh-access-token\"}")));

        var outcome = await sut.TryRefreshAsync(user);

        handler.CallCount.Should().Be(2);
        outcome.Result.Should().Be(GoogleTokenRefreshResult.Success);
    }

    [Fact]
    public async Task DoesNotRetry_OnRevokedRefreshToken()
    {
        var (sut, handler, user) = Build(new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Status(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}")));

        var outcome = await sut.TryRefreshAsync(user);

        handler.CallCount.Should().Be(1);
        outcome.Result.Should().Be(GoogleTokenRefreshResult.RefreshTokenInvalid);
    }

    [Fact]
    public async Task GivesUpAfterMaxRetries_OnPersistent5xx()
    {
        var (sut, handler, user) = Build(new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Status(HttpStatusCode.InternalServerError, "{}")));

        var outcome = await sut.TryRefreshAsync(user);

        handler.CallCount.Should().Be(3);
        outcome.Result.Should().Be(GoogleTokenRefreshResult.TransientFailure);
    }
}
