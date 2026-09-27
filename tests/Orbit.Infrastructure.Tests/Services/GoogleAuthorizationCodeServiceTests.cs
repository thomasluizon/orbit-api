using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class GoogleAuthorizationCodeServiceTests
{
    private const string RedirectUri = "https://app.test/auth/callback";
    private readonly RecordingHandler _handler = new();
    private readonly IGoogleIdTokenValidator _validator = Substitute.For<IGoogleIdTokenValidator>();
    private readonly GoogleAuthorizationCodeService _service;

    public GoogleAuthorizationCodeServiceTests()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(GoogleAuthorizationCodeService.HttpClientName).Returns(new HttpClient(_handler));
        _validator.ValidateAsync("signed-id-token")
            .Returns(Result.Success(("google@example.com", "Google User")));
        _service = new GoogleAuthorizationCodeService(factory,
            Options.Create(new GoogleSettings
            {
                ClientId = "google-client",
                ClientSecret = "google-secret",
                AllowedRedirectUris = [RedirectUri]
            }), _validator);
    }

    [Fact]
    public async Task AllowedRedirect_ExchangesCodeAndValidatesIdToken()
    {
        _handler.Respond(HttpStatusCode.OK,
            """{"access_token":"access","refresh_token":"refresh","id_token":"signed-id-token"}""");

        var result = await _service.ExchangeAsync("auth-code", "pkce-verifier", RedirectUri, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Email.Should().Be("google@example.com");
        result.Value.AccessToken.Should().Be("access");
        result.Value.RefreshToken.Should().Be("refresh");
        _handler.RequestCount.Should().Be(1);
        _handler.LastUri.Should().Be("https://oauth2.googleapis.com/token");
        _handler.LastBody.Should().Contain("code=auth-code")
            .And.Contain("code_verifier=pkce-verifier")
            .And.Contain("client_id=google-client")
            .And.Contain("client_secret=google-secret")
            .And.Contain("redirect_uri=https%3A%2F%2Fapp.test%2Fauth%2Fcallback")
            .And.Contain("grant_type=authorization_code");
        await _validator.Received(1).ValidateAsync("signed-id-token");
    }

    [Fact]
    public async Task RefusedRedirect_MakesNoGoogleCall()
    {
        var result = await _service.ExchangeAsync("auth-code", "verifier", "https://evil.test/callback", CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.GoogleRedirectUriNotAllowed);
        _handler.RequestCount.Should().Be(0);
        await _validator.DidNotReceive().ValidateAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task InvalidGrant_ReturnsExchangeError()
    {
        _handler.Respond(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");

        var result = await _service.ExchangeAsync("auth-code", "verifier", RedirectUri, CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.GoogleCodeExchangeFailed);
        await _validator.DidNotReceive().ValidateAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task InvalidIdToken_ReturnsValidatorError()
    {
        _handler.Respond(HttpStatusCode.OK, """{"id_token":"signed-id-token"}""");
        _validator.ValidateAsync("signed-id-token")
            .Returns(Result.Failure<(string, string)>(ErrorMessages.InvalidGoogleToken));

        var result = await _service.ExchangeAsync("auth-code", "verifier", RedirectUri, CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.InvalidGoogleToken);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private HttpStatusCode _status;
        private string _body = "";
        public int RequestCount { get; private set; }
        public string? LastUri { get; private set; }
        public string? LastBody { get; private set; }

        public void Respond(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastUri = request.RequestUri?.ToString();
            LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            };
        }
    }
}
