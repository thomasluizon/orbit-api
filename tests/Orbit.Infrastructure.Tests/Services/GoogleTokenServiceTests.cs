using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class GoogleTokenServiceTests
{
    private readonly StubHttpMessageHandler _handler = new();
    private readonly GoogleTokenService _sut;

    public GoogleTokenServiceTests()
    {
        var httpClient = new HttpClient(_handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(GoogleTokenService.HttpClientName).Returns(httpClient);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Google:ClientId"] = "client-id",
                ["Google:ClientSecret"] = "client-secret",
                ["Google:TokenUrl"] = "https://oauth2.googleapis.com/token",
            })
            .Build();

        _sut = new GoogleTokenService(factory, configuration, NullLogger<GoogleTokenService>.Instance);
    }

    [Fact]
    public async Task TryRefreshAsync_TimeoutWhileCallerTokenLive_ReturnsTransientFailureWithoutThrowing()
    {
        _handler.ExceptionToThrow = new TaskCanceledException("the request timed out");
        var user = UserWithRefreshToken();

        var outcome = await _sut.TryRefreshAsync(user, CancellationToken.None);

        outcome.Result.Should().Be(GoogleTokenRefreshResult.TransientFailure);
        outcome.AccessToken.Should().BeNull();
        user.GoogleAccessToken.Should().Be("access-old");
    }

    [Fact]
    public async Task TryRefreshAsync_TransportFailure_ReturnsTransientFailureWithoutThrowing()
    {
        _handler.ExceptionToThrow = new HttpRequestException("connection refused");
        var user = UserWithRefreshToken();

        var outcome = await _sut.TryRefreshAsync(user, CancellationToken.None);

        outcome.Result.Should().Be(GoogleTokenRefreshResult.TransientFailure);
    }

    [Fact]
    public async Task TryRefreshAsync_RevokedRefreshToken_ReturnsRefreshTokenInvalid()
    {
        _handler.Response = JsonResponse(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");
        var user = UserWithRefreshToken();

        var outcome = await _sut.TryRefreshAsync(user, CancellationToken.None);

        outcome.Result.Should().Be(GoogleTokenRefreshResult.RefreshTokenInvalid);
        outcome.ErrorCode.Should().Be("invalid_grant");
    }

    [Fact]
    public async Task TryRefreshAsync_SuccessfulRefresh_RotatesTokensAndReturnsAccessToken()
    {
        _handler.Response = JsonResponse(HttpStatusCode.OK,
            """{"access_token":"access-new","refresh_token":"refresh-new"}""");
        var user = UserWithRefreshToken();

        var outcome = await _sut.TryRefreshAsync(user, CancellationToken.None);

        outcome.Result.Should().Be(GoogleTokenRefreshResult.Success);
        outcome.AccessToken.Should().Be("access-new");
        user.GoogleAccessToken.Should().Be("access-new");
        user.GoogleRefreshToken.Should().Be("refresh-new");
    }

    [Fact]
    public async Task TryRefreshAsync_NoRefreshToken_ReturnsTransientFailureWithoutCallingGoogle()
    {
        var user = User.Create("Test", "test@example.com").Value;
        user.SetGoogleTokens("access-only", null);

        var outcome = await _sut.TryRefreshAsync(user, CancellationToken.None);

        outcome.Result.Should().Be(GoogleTokenRefreshResult.TransientFailure);
        outcome.ErrorCode.Should().Be(GoogleTokenErrorCodes.NoRefreshToken);
        _handler.WasCalled.Should().BeFalse();
    }

    private static User UserWithRefreshToken()
    {
        var user = User.Create("Test", "test@example.com").Value;
        user.SetGoogleTokens("access-old", "refresh-old");
        return user;
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string body) =>
        new(statusCode) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        public HttpResponseMessage? Response { get; set; }
        public Exception? ExceptionToThrow { get; set; }
        public bool WasCalled { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            WasCalled = true;
            cancellationToken.ThrowIfCancellationRequested();

            if (ExceptionToThrow is not null)
                throw ExceptionToThrow;

            return Task.FromResult(Response ?? new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
