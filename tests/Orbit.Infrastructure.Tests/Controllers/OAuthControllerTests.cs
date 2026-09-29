using NSubstitute.ExceptionExtensions;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Data.Common;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Api.OAuth;
using Orbit.Api.RateLimiting;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Queries;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Tests.Controllers;

public class OAuthControllerTests : IDisposable
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly MutableTimeProvider _timeProvider = new();
    private readonly OAuthAuthorizationStore _authStore;
    private readonly IGenericRepository<ApiKey> _apiKeyRepo = Substitute.For<IGenericRepository<ApiKey>>();
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();
    private readonly ILogger<OAuthController> _logger = Substitute.For<ILogger<OAuthController>>();
    private readonly OAuthController _controller;

    private static readonly Guid UserId = Guid.NewGuid();

    public OAuthControllerTests()
    {
        _authStore = new OAuthAuthorizationStore(NullLogger<OAuthAuthorizationStore>.Instance, _timeProvider);
        var googleSettings = Options.Create(new GoogleSettings
        {
            ClientId = "test-google-client-id",
            AllowedRedirectUris = ["https://api.useorbit.org/oauth/google/callback"]
        });
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OAuth:AllowedRedirectHosts:0"] = "claude.ai",
                ["OAuth:AllowedRedirectHosts:1"] = "claude.com"
            })
            .Build();

        _controller = new OAuthController(
            _mediator, _authStore, _apiKeyRepo, _userRepo, _unitOfWork, _httpClientFactory,
            googleSettings, config, _logger);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("api.useorbit.org");
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    public void Dispose()
    {
        _authStore.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void GetMetadata_ReturnsOkWithEndpoints()
    {
        var result = _controller.GetMetadata();

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("authorization_endpoint");
        json.Should().Contain("token_endpoint");
        json.Should().Contain("registration_endpoint");
    }

    [Fact]
    public void GetMetadata_UsesXForwardedProto_WhenPresent()
    {
        _controller.ControllerContext.HttpContext.Request.Headers["X-Forwarded-Proto"] = "https";
        _controller.ControllerContext.HttpContext.Request.Scheme = "http";

        var result = _controller.GetMetadata();

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("https://");
    }

    [Fact]
    public void Register_WithClientName_Returns201WithClientId()
    {
        var body = JsonSerializer.Deserialize<JsonElement>(
            """{"client_name":"My MCP","redirect_uris":["https://claude.ai/callback"]}""");

        var result = _controller.Register(body);

        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(201);
        var json = JsonSerializer.Serialize(obj.Value);
        json.Should().Contain("My MCP");
        json.Should().Contain("client_id");
    }

    [Fact]
    public void Register_WithoutClientName_DefaultsToMcpClient()
    {
        var body = JsonSerializer.Deserialize<JsonElement>("{}");

        var result = _controller.Register(body);

        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(201);
        var json = JsonSerializer.Serialize(obj.Value);
        json.Should().Contain("MCP Client");
    }

    [Fact]
    public void GetProtectedResourceMetadata_ReturnsOkWithMcpResource()
    {
        var result = _controller.GetProtectedResourceMetadata();

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("/mcp");
        json.Should().Contain("bearer_methods_supported");
    }

    [Fact]
    public void Authorize_ValidParams_ReturnsHtmlContent()
    {
        var result = _controller.Authorize(
            "client-123", "https://claude.ai/callback", "code",
            "state-abc", "challenge-xyz", "S256");

        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.ContentType.Should().Be("text/html");
        content.Content.Should().Contain("/oauth/google/start?client_id=");
        content.Content.Should().NotContain("google.accounts.id");
    }

    [Fact]
    public async Task GoogleStart_RedirectsToGoogleWithFreshStateAndItsOwnPkce()
    {
        var url = StartGoogle(nonce: "mcp-nonce").Should().BeOfType<RedirectResult>().Subject.Url!;

        url.Should().StartWith("https://accounts.google.com/o/oauth2/v2/auth?");
        ExtractQueryParam(url, "client_id").Should().Be("test-google-client-id");
        ExtractQueryParam(url, "redirect_uri").Should().Be("https://api.useorbit.org/oauth/google/callback");
        ExtractQueryParam(url, "response_type").Should().Be("code");
        ExtractQueryParam(url, "scope").Should().Be("openid email profile");
        ExtractQueryParam(url, "code_challenge_method").Should().Be("S256");
        ExtractQueryParam(url, "prompt").Should().Be("select_account");
        ExtractQueryParam(url, "state").Should().NotBe("client-state");
        url.Should().NotContain("mcp-challenge");

        _mediator.Send(Arg.Any<GoogleCodeAuthCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new LoginResponse(UserId, "jwt", "Alex", "alex@example.com")));
        await _controller.GoogleCallback(
            ExtractQueryParam(url, "state"), "google-code", null, CancellationToken.None);

        await _mediator.Received(1).Send(Arg.Is<GoogleCodeAuthCommand>(command =>
            Challenge(command.CodeVerifier) == ExtractQueryParam(url, "code_challenge")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void GoogleStart_AllocatesExactlyOneSingleUseRequest()
    {
        var url = StartGoogle().Should().BeOfType<RedirectResult>().Subject.Url!;
        var googleState = ExtractQueryParam(url, "state");

        _authStore.ConsumeGoogleRequest(googleState).Should().NotBeNull();
        _authStore.ConsumeGoogleRequest(googleState).Should().BeNull();
    }

    [Theory]
    [InlineData("", "https://claude.ai/callback", "client-state", "mcp-challenge", "S256")]
    [InlineData("client-123", "https://claude.ai/callback", "", "mcp-challenge", "S256")]
    [InlineData("client-123", "https://claude.ai/callback", "client-state", "", "S256")]
    [InlineData("client-123", "https://claude.ai/callback", "client-state", "mcp-challenge", "plain")]
    [InlineData("client-123", "https://evil.example/callback", "client-state", "mcp-challenge", "S256")]
    [InlineData("client-123", "http://claude.ai/callback", "client-state", "mcp-challenge", "S256")]
    public void GoogleStart_RevalidatesTheRequestBeforeAllocating(
        string clientId, string redirectUri, string clientState, string challenge, string method)
    {
        _controller.GoogleStart(clientId, redirectUri, clientState, challenge, method)
            .Should().BeOfType<ContentResult>().Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public void GoogleStart_CapReachedReturnsUnavailableAndKeepsEmailSignIn()
    {
        for (var i = 0; i < OAuthAuthorizationStore.MaxPendingGoogleRequests; i++)
        {
            _authStore.TryCreateGoogleRequest("client", "https://claude.ai/callback", "client-state",
                "mcp-challenge", null, "https://api.useorbit.org/oauth/google/callback", "en")
                .Should().NotBeNull();
        }

        var redirect = StartGoogle().Should().BeOfType<RedirectResult>().Subject;

        ExtractQueryParam(redirect.Url!, "google_error").Should().Be("unavailable");
        var page = _controller.Authorize("client-123", "https://claude.ai/callback", "code",
            "client-state", "mcp-challenge", "S256", google_error: "unavailable")
            .Should().BeOfType<ContentResult>().Subject;
        page.Content.Should().Contain("Google sign-in is unavailable");
        page.Content.Should().Contain("/oauth/send-code");
    }

    [Fact]
    public async Task GoogleCallback_RejectsUnknownAndReusedState()
    {
        var state = CreatePendingGoogleRequest().GoogleState;
        (await _controller.GoogleCallback("forged-state", "code", null, CancellationToken.None))
            .Should().BeOfType<ContentResult>().Which.StatusCode.Should().Be(400);

        (await _controller.GoogleCallback(state, null, "access_denied", CancellationToken.None))
            .Should().BeOfType<RedirectResult>();
        (await _controller.GoogleCallback(state, "code", null, CancellationToken.None))
            .Should().BeOfType<ContentResult>().Which.StatusCode.Should().Be(400);
        await _mediator.DidNotReceive().Send(Arg.Any<GoogleCodeAuthCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GoogleCallback_RejectsExpiredState()
    {
        var state = CreatePendingGoogleRequest().GoogleState;
        _timeProvider.Advance(TimeSpan.FromMinutes(6));

        (await _controller.GoogleCallback(state, "google-code", null, CancellationToken.None))
            .Should().BeOfType<ContentResult>().Which.StatusCode.Should().Be(400);
        await _mediator.DidNotReceive().Send(Arg.Any<GoogleCodeAuthCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GoogleCallback_SuccessIssuesMcpCodeAcceptedForOriginalVerifierAndRedirectUri()
    {
        var (verifier, challenge) = GeneratePkce();
        var pending = CreatePendingGoogleRequest(challenge, "client-state&code=forged", "mcp-nonce");
        _mediator.Send(Arg.Any<GoogleCodeAuthCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new LoginResponse(UserId, "jwt", "Alex", "alex@example.com")));

        var result = await _controller.GoogleCallback(
            pending.GoogleState, "google-code", null, CancellationToken.None);

        var url = result.Should().BeOfType<RedirectResult>().Subject.Url!;
        url.Should().StartWith("https://claude.ai/callback?code=");
        ExtractQueryParam(url, "state").Should().Be("client-state&code=forged");
        url.Should().NotContain("&code=forged");
        await _mediator.Received(1).Send(Arg.Is<GoogleCodeAuthCommand>(command =>
            command.Code == "google-code"
            && command.CodeVerifier == pending.GoogleCodeVerifier
            && command.RedirectUri == pending.GoogleRedirectUri
            && !command.PersistGoogleTokens), Arg.Any<CancellationToken>());

        var token = await _controller.Token("authorization_code", ExtractQueryParam(url, "code"),
            verifier, "https://claude.ai/callback", CancellationToken.None);
        token.Should().BeOfType<OkObjectResult>();
        JsonSerializer.Serialize(((OkObjectResult)token).Value).Should().Contain("mcp-nonce");
        (await _controller.Token("authorization_code", ExtractQueryParam(url, "code"),
            verifier, "https://claude.ai/callback", CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task GoogleCallback_CancelledConsentReturnsLocalizedErrorWithEmailPath()
    {
        _controller.HttpContext.Request.Headers.AcceptLanguage = "pt-BR,pt;q=0.9";
        var state = CreatePendingGoogleRequest().GoogleState;

        var redirect = (await _controller.GoogleCallback(state, null, "access_denied", CancellationToken.None))
            .Should().BeOfType<RedirectResult>().Subject;
        ExtractQueryParam(redirect.Url!, "google_error").Should().Be("cancelled");
        var page = _controller.Authorize("client-123", "https://claude.ai/callback", "code",
            "client-state", "mcp-challenge", "S256", google_error: "cancelled")
            .Should().BeOfType<ContentResult>().Subject;
        page.Content.Should().Contain("O acesso com Google foi cancelado");
        page.Content.Should().Contain("/oauth/send-code");
        page.Content.Should().Contain("if (initialError) showError(initialError)");
    }

    [Fact]
    public async Task GoogleCallback_ExchangeFailureReturnsAuthorizePageWithError()
    {
        var state = CreatePendingGoogleRequest().GoogleState;
        _mediator.Send(Arg.Any<GoogleCodeAuthCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<LoginResponse>(ErrorMessages.GoogleCodeExchangeFailed));

        var redirect = (await _controller.GoogleCallback(state, "bad-code", null, CancellationToken.None))
            .Should().BeOfType<RedirectResult>().Subject;

        ExtractQueryParam(redirect.Url!, "google_error").Should().Be("failed");
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("test-google-client-id", false)]
    public void GoogleStart_MissingConfigurationReturnsUnavailable(string clientId, bool callbackAllowed)
    {
        var settings = Options.Create(new GoogleSettings
        {
            ClientId = clientId,
            AllowedRedirectUris = callbackAllowed ? ["https://api.useorbit.org/oauth/google/callback"] : []
        });
        var controller = new OAuthController(_mediator, _authStore, _apiKeyRepo,
            _userRepo, _unitOfWork, _httpClientFactory, settings, new ConfigurationBuilder().Build(), _logger)
        {
            ControllerContext = _controller.ControllerContext
        };

        var redirect = controller.GoogleStart("client-123", "https://claude.ai/callback", "client-state",
            "mcp-challenge", "S256", "preserved-nonce").Should().BeOfType<RedirectResult>().Subject;
        ExtractQueryParam(redirect.Url!, "google_error").Should().Be("unavailable");
        ExtractQueryParam(redirect.Url!, "nonce").Should().Be("preserved-nonce");
    }

    [Theory]
    [InlineData("en", "failed", "Google sign-in failed")]
    [InlineData("pt-BR", "failed", "Não foi possível entrar com Google")]
    [InlineData("en", "unavailable", "Google sign-in is unavailable")]
    [InlineData("pt-BR", "unavailable", "O acesso com Google está indisponível")]
    public void Authorize_GoogleErrorRendersLocalizedMessageAndEmailPath(
        string language, string error, string message)
    {
        _controller.HttpContext.Request.Headers.AcceptLanguage = language;

        var page = _controller.Authorize("client-123", "https://claude.ai/callback", "code",
            "client-state", "mcp-challenge", "S256", google_error: error)
            .Should().BeOfType<ContentResult>().Subject;

        var encodedError = System.Text.RegularExpressions.Regex.Match(page.Content!,
            @"const initialError = (.*);").Groups[1].Value;
        JsonSerializer.Deserialize<string>(encodedError).Should().StartWith(message);
        page.Content.Should().Contain("/oauth/send-code");
    }

    [Fact]
    public async Task GoogleCallback_RejectsStoredRedirectUriThatIsNoLongerAllowed()
    {
        var state = _authStore.TryCreateGoogleRequest("client-123", "https://evil.example/callback",
            "client-state", "mcp-challenge", null,
            "https://api.useorbit.org/oauth/google/callback", "en")!.GoogleState;
        _mediator.Send(Arg.Any<GoogleCodeAuthCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new LoginResponse(UserId, "jwt", "Alex", "alex@example.com")));

        var result = await _controller.GoogleCallback(state, "google-code", null, CancellationToken.None);

        result.Should().BeOfType<ContentResult>().Which.StatusCode.Should().Be(400);
        await _mediator.DidNotReceive().Send(Arg.Any<GoogleCodeAuthCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void GoogleAuth_RouteRemainsAvailableAndDeprecated()
    {
        var route = typeof(OAuthController).GetMethods()
            .SingleOrDefault(method => method.GetCustomAttributes(typeof(HttpPostAttribute), false)
                .Cast<HttpPostAttribute>().Any(attribute => attribute.Template == "/oauth/google"));

        route.Should().NotBeNull();
        route!.GetCustomAttributes(typeof(ObsoleteAttribute), false).Should().ContainSingle();
    }

    [Fact]
    public void Authorize_UnsupportedResponseType_ReturnsBadRequest()
    {
        var result = _controller.Authorize(
            "client-123", "https://claude.ai/callback", "token",
            "state-abc", "challenge-xyz", "S256");

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var json = JsonSerializer.Serialize(bad.Value);
        json.Should().Contain("unsupported_response_type");
    }

    [Fact]
    public void Authorize_MissingCodeChallenge_ReturnsBadRequest()
    {
        var result = _controller.Authorize(
            "client-123", "https://claude.ai/callback", "code",
            "state-abc", "", "S256");

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void Authorize_WrongCodeChallengeMethod_ReturnsBadRequest()
    {
        var result = _controller.Authorize(
            "client-123", "https://claude.ai/callback", "code",
            "state-abc", "challenge-xyz", "plain");

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void Authorize_DisallowedRedirectHost_ReturnsBadRequest()
    {
        var result = _controller.Authorize(
            "client-123", "https://evil.com/callback", "code",
            "state-abc", "challenge-xyz", "S256");

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var json = JsonSerializer.Serialize(bad.Value);
        json.Should().Contain("invalid_redirect_uri");
    }

    [Fact]
    public void Authorize_InvalidUri_ReturnsBadRequest()
    {
        var result = _controller.Authorize(
            "client-123", "not-a-valid-uri", "code",
            "state-abc", "challenge-xyz", "S256");

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task SendCode_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<SendCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var request = new OAuthController.SendCodeRequest("test@example.com");
        var result = await _controller.SendCode(request, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task SendCode_Failure_ReturnsBadRequestWithErrorCode()
    {
        _mediator.Send(Arg.Any<SendCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure(ErrorMessages.TooManyRequests));

        var request = new OAuthController.SendCodeRequest("test@example.com");
        var result = await _controller.SendCode(request, CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(400);
        objectResult.Value.Should().BeEquivalentTo(new
        {
            Error = ErrorMessages.TooManyRequests.Message,
            ErrorCode = ErrorMessages.TooManyRequests.Code
        });
    }

    [Fact]
    public async Task SendCode_NullLanguage_DefaultsToEn()
    {
        _mediator.Send(Arg.Any<SendCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var request = new OAuthController.SendCodeRequest("test@example.com", null);
        var result = await _controller.SendCode(request, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        await _mediator.Received(1).Send(
            Arg.Is<SendCodeCommand>(c => c.Language == "en"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyCode_Success_ReturnsOkWithRedirectUrl()
    {
        var loginResponse = new LoginResponse(UserId, "jwt-token", "Alex", "test@example.com");
        _mediator.Send(Arg.Any<VerifyCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(loginResponse));

        var request = new OAuthController.VerifyCodeRequest(
            "test@example.com", "123456", "state-abc",
            "challenge-xyz", "https://claude.ai/callback", "client-123");

        var result = await _controller.VerifyCode(request, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("redirectUrl");
        json.Should().Contain("claude.ai/callback");
        json.Should().Contain("state=state-abc");
    }

    [Fact]
    public async Task VerifyCode_Failure_ReturnsBadRequestWithErrorCode()
    {
        _mediator.Send(Arg.Any<VerifyCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<LoginResponse>(ErrorMessages.InvalidVerificationCode));

        var request = new OAuthController.VerifyCodeRequest(
            "test@example.com", "000000", "state-abc",
            "challenge-xyz", "https://claude.ai/callback", "client-123");

        var result = await _controller.VerifyCode(request, CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(400);
        objectResult.Value.Should().BeEquivalentTo(new
        {
            Error = ErrorMessages.InvalidVerificationCode.Message,
            ErrorCode = ErrorMessages.InvalidVerificationCode.Code
        });
    }

    [Fact]
    public async Task VerifyCode_InvalidRedirectUri_ReturnsBadRequest()
    {
        var request = new OAuthController.VerifyCodeRequest(
            "test@example.com", "123456", "state-abc",
            "challenge-xyz", "https://evil.com/callback", "client-123");

        var result = await _controller.VerifyCode(request, CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        JsonSerializer.Serialize(bad.Value).Should().Contain("invalid_redirect_uri");
    }

    [Fact]
    public async Task VerifyCode_RedirectUriWithQueryParam_UseAmpersandSeparator()
    {
        var loginResponse = new LoginResponse(UserId, "jwt-token", "Alex", "test@example.com");
        _mediator.Send(Arg.Any<VerifyCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(loginResponse));

        var request = new OAuthController.VerifyCodeRequest(
            "test@example.com", "123456", "state-abc",
            "challenge-xyz", "https://claude.ai/callback?existing=1", "client-123");

        var result = await _controller.VerifyCode(request, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("callback?existing=1\\u0026code=");
    }

    [Fact]
    public async Task GoogleAuth_InvalidToken_ReturnsBadRequest()
    {
        var mockHandler = new MockHttpMessageHandler(HttpStatusCode.Unauthorized, "{}");
        var httpClient = new HttpClient(mockHandler);
        _httpClientFactory.CreateClient().Returns(httpClient);

        var request = new OAuthController.GoogleAuthRequest(
            "invalid-token", "state-abc", "challenge-xyz",
            "https://claude.ai/callback", "client-123");

        var result = await _controller.GoogleAuth(request, CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var json = JsonSerializer.Serialize(bad.Value);
        json.Should().Contain(ErrorMessages.InvalidGoogleToken.Message);
    }

    [Fact]
    public async Task GoogleAuth_InvalidRedirectUri_ReturnsBadRequest()
    {
        var request = new OAuthController.GoogleAuthRequest(
            "valid-token", "state-abc", "challenge-xyz",
            "https://evil.com/callback", "client-123");

        var result = await _controller.GoogleAuth(request, CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        JsonSerializer.Serialize(bad.Value).Should().Contain("invalid_redirect_uri");
    }

    [Fact]
    public async Task GoogleAuth_NoEmailInToken_ReturnsBadRequest()
    {
        var mockHandler = new MockHttpMessageHandler(HttpStatusCode.OK, """{"aud":"test-google-client-id"}""");
        var httpClient = new HttpClient(mockHandler);
        _httpClientFactory.CreateClient().Returns(httpClient);

        var request = new OAuthController.GoogleAuthRequest(
            "valid-token", "state-abc", "challenge-xyz",
            "https://claude.ai/callback", "client-123");

        var result = await _controller.GoogleAuth(request, CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var json = JsonSerializer.Serialize(bad.Value);
        json.Should().Contain(ErrorMessages.GoogleEmailUnavailable.Message);
    }

    [Fact]
    public async Task GoogleAuth_WrongAudience_ReturnsBadRequest()
    {
        var mockHandler = new MockHttpMessageHandler(HttpStatusCode.OK,
            """{"email":"test@example.com","aud":"wrong-client-id"}""");
        var httpClient = new HttpClient(mockHandler);
        _httpClientFactory.CreateClient().Returns(httpClient);

        var request = new OAuthController.GoogleAuthRequest(
            "valid-token", "state-abc", "challenge-xyz",
            "https://claude.ai/callback", "client-123");

        var result = await _controller.GoogleAuth(request, CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var json = JsonSerializer.Serialize(bad.Value);
        json.Should().Contain(ErrorMessages.GoogleTokenAudienceMismatch.Message);
    }

    [Fact]
    public async Task GoogleAuth_ExistingUser_ReturnsRedirectUrl()
    {
        var user = User.Create("Alex", "test@example.com").Value;
        var mockHandler = new MockHttpMessageHandler(HttpStatusCode.OK,
            """{"email":"test@example.com","aud":"test-google-client-id","name":"Alex"}""");
        var httpClient = new HttpClient(mockHandler);
        _httpClientFactory.CreateClient().Returns(httpClient);

        _userRepo.FindOneTrackedIgnoringFiltersAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<User, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(user);

        var request = new OAuthController.GoogleAuthRequest(
            "valid-token", "state-abc", "challenge-xyz",
            "https://claude.ai/callback", "client-123");

        var result = await _controller.GoogleAuth(request, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("redirectUrl");
        json.Should().Contain("claude.ai/callback");
    }

    [Fact]
    public async Task GoogleAuth_DeactivatedUser_ReactivatesAndReturnsRedirect()
    {
        var user = User.Create("Alex", "test@example.com").Value;
        user.Deactivate(DateTime.UtcNow.AddDays(7));
        var mockHandler = new MockHttpMessageHandler(HttpStatusCode.OK,
            """{"email":"test@example.com","aud":"test-google-client-id","name":"Alex"}""");
        _httpClientFactory.CreateClient().Returns(new HttpClient(mockHandler));

        _userRepo.FindOneTrackedIgnoringFiltersAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<User, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(user);

        var request = new OAuthController.GoogleAuthRequest(
            "valid-token", "state-abc", "challenge-xyz",
            "https://claude.ai/callback", "client-123");

        var result = await _controller.GoogleAuth(request, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        user.IsDeactivated.Should().BeFalse();
        await _userRepo.DidNotReceive().AddAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GoogleAuth_NewUser_CreatesUserAndReturnsRedirect()
    {
        var mockHandler = new MockHttpMessageHandler(HttpStatusCode.OK,
            """{"email":"new@example.com","aud":"test-google-client-id","name":"New User"}""");
        var httpClient = new HttpClient(mockHandler);
        _httpClientFactory.CreateClient().Returns(httpClient);

        _userRepo.FindOneTrackedIgnoringFiltersAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<User, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns((User?)null);

        var request = new OAuthController.GoogleAuthRequest(
            "valid-token", "state-abc", "challenge-xyz",
            "https://claude.ai/callback", "client-123");

        var result = await _controller.GoogleAuth(request, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        await _userRepo.Received(1).AddAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GoogleAuth_NewUserWithoutName_UsesEmailPrefix()
    {
        var mockHandler = new MockHttpMessageHandler(HttpStatusCode.OK,
            """{"email":"newuser@example.com","aud":"test-google-client-id"}""");
        var httpClient = new HttpClient(mockHandler);
        _httpClientFactory.CreateClient().Returns(httpClient);

        _userRepo.FindOneTrackedIgnoringFiltersAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<User, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns((User?)null);

        var request = new OAuthController.GoogleAuthRequest(
            "valid-token", "state-abc", "challenge-xyz",
            "https://claude.ai/callback", "client-123");

        var result = await _controller.GoogleAuth(request, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        await _userRepo.Received(1).AddAsync(
            Arg.Is<User>(u => u.Name == "newuser"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GoogleAuth_MixedCaseEmail_LogsIntoExistingLowercaseAccount()
    {
        var existingUser = User.Create("Alex", "test@example.com").Value;
        var mockHandler = new MockHttpMessageHandler(HttpStatusCode.OK,
            """{"email":"Test@Example.com","aud":"test-google-client-id","name":"Alex"}""");
        _httpClientFactory.CreateClient().Returns(new HttpClient(mockHandler));

        _userRepo.FindOneTrackedIgnoringFiltersAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<User, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var predicate = callInfo.Arg<System.Linq.Expressions.Expression<Func<User, bool>>>().Compile();
                return predicate(existingUser) ? existingUser : null;
            });

        var request = new OAuthController.GoogleAuthRequest(
            "valid-token", "state-abc", "challenge-xyz",
            "https://claude.ai/callback", "client-123");

        var result = await _controller.GoogleAuth(request, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        await _userRepo.DidNotReceive().AddAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GoogleAuth_ConcurrentFirstLogin_ResolvesToExistingUserWithout500()
    {
        var racedUser = User.Create("Raced", "new@example.com").Value;
        var mockHandler = new MockHttpMessageHandler(HttpStatusCode.OK,
            """{"email":"new@example.com","aud":"test-google-client-id","name":"Raced"}""");
        _httpClientFactory.CreateClient().Returns(new HttpClient(mockHandler));

        _userRepo.FindOneTrackedIgnoringFiltersAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<User, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns((User?)null, racedUser);
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new DbUpdateException("duplicate", new FakeUniqueViolationException()));

        var request = new OAuthController.GoogleAuthRequest(
            "valid-token", "state-abc", "challenge-xyz",
            "https://claude.ai/callback", "client-123");

        var result = await _controller.GoogleAuth(request, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        await _userRepo.Received(1).AddAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Token_UnsupportedGrantType_ReturnsBadRequest()
    {
        var result = await _controller.Token(
            "client_credentials", "code-abc", "verifier-xyz",
            "https://claude.ai/callback", CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var json = JsonSerializer.Serialize(bad.Value);
        json.Should().Contain("unsupported_grant_type");
    }

    [Fact]
    public async Task Token_InvalidCode_ReturnsBadRequest()
    {
        var result = await _controller.Token(
            "authorization_code", "nonexistent-code", "verifier-xyz",
            "https://claude.ai/callback", CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var json = JsonSerializer.Serialize(bad.Value);
        json.Should().Contain("invalid_grant");
    }

    [Fact]
    public async Task Token_InvalidRedirectUri_ReturnsBadRequest()
    {
        var result = await _controller.Token(
            "authorization_code", "nonexistent-code", "verifier-xyz",
            "https://evil.com/callback", CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        JsonSerializer.Serialize(bad.Value).Should().Contain("invalid_redirect_uri");
    }

    [Fact]
    public async Task Token_ValidCodeExchange_ReturnsAccessToken()
    {
        var codeVerifier = "test-verifier-that-is-long-enough-for-pkce-validation";
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        var codeChallenge = Convert.ToBase64String(hash)
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');

        var authCode = _authStore.CreateCode(
            UserId, codeChallenge, "https://claude.ai/callback", "client-123");

        var result = await _controller.Token(
            "authorization_code", authCode, codeVerifier,
            "https://claude.ai/callback", CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("access_token");
        json.Should().Contain("Bearer");
        await _apiKeyRepo.Received(1).AddAsync(Arg.Any<ApiKey>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Token_TakesNoClientId_AndBindsOnPkceAndRedirectUriInstead()
    {
        var (verifier, challenge) = GeneratePkce();
        var code = _authStore.CreateCode(UserId, challenge, "https://claude.ai/callback", "client-registered-at-authorize");

        var result = await _controller.Token(
            "authorization_code", code, verifier,
            "https://claude.ai/callback", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();

        typeof(OAuthController).GetMethod(nameof(OAuthController.Token))!
            .GetParameters().Select(p => p.Name)
            .Should().NotContain("client_id");
    }

    [Fact]
    public async Task Token_WrongCodeVerifier_ReturnsInvalidGrant()
    {
        var (_, challenge) = GeneratePkce();
        var (otherVerifier, _) = GeneratePkce();
        var code = _authStore.CreateCode(UserId, challenge, "https://claude.ai/callback", "client-123");

        var result = await _controller.Token(
            "authorization_code", code, otherVerifier,
            "https://claude.ai/callback", CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        JsonSerializer.Serialize(bad.Value).Should().Contain("invalid_grant");
        await _apiKeyRepo.DidNotReceive().AddAsync(Arg.Any<ApiKey>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Token_ValidExchange_CreatesReadWriteClaudeKeyWithDefaultScopes()
    {
        var (verifier, challenge) = GeneratePkce();
        var code = _authStore.CreateCode(UserId, challenge, "https://claude.ai/callback", "client-123");

        ApiKey? created = null;
        _apiKeyRepo.When(r => r.AddAsync(Arg.Any<ApiKey>(), Arg.Any<CancellationToken>()))
            .Do(callInfo => created = callInfo.Arg<ApiKey>());

        var result = await _controller.Token(
            "authorization_code", code, verifier,
            "https://claude.ai/callback", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        created.Should().NotBeNull();
        created!.UserId.Should().Be(UserId);
        created.Name.Should().Be("Claude.ai");
        created.IsReadOnly.Should().BeFalse();
        created.IsRevoked.Should().BeFalse();
        created.Scopes.Should().BeEquivalentTo(AgentScopes.ClaudeDefaultScopes);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());

        var ok = (OkObjectResult)result;
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        doc.RootElement.GetProperty("scope").GetString()
            .Should().Be(string.Join(' ', AgentScopes.ClaudeDefaultScopes));
    }

    [Fact]
    public async Task Token_ValidExchange_RevokesPriorClaudeKeysBeforeIssuingNew()
    {
        var (verifier, challenge) = GeneratePkce();
        var code = _authStore.CreateCode(UserId, challenge, "https://claude.ai/callback", "client-123");

        var priorKey = ApiKey.Create(UserId, "Claude.ai", AgentScopes.ClaudeDefaultScopes).Value.Entity;
        _apiKeyRepo.FindTrackedAsync(
                Arg.Any<System.Linq.Expressions.Expression<Func<ApiKey, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<ApiKey> { priorKey });

        var result = await _controller.Token(
            "authorization_code", code, verifier,
            "https://claude.ai/callback", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        priorKey.IsRevoked.Should().BeTrue();
        await _apiKeyRepo.Received(1).AddAsync(
            Arg.Is<ApiKey>(k => k.Name == "Claude.ai"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Register_WithAttackerRedirectUri_ReturnsBadRequest()
    {
        var body = JsonSerializer.Deserialize<JsonElement>(
            """{"client_name":"Malicious MCP","redirect_uris":["https://attacker.com/callback"]}""");

        var result = _controller.Register(body);

        var obj = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var json = JsonSerializer.Serialize(obj.Value);
        json.Should().Contain("invalid_redirect_uri");
        json.Should().Contain("not in the allowlist");
    }

    [Fact]
    public void Register_WithMixedValidAndInvalidRedirectUris_ReturnsBadRequest()
    {
        var body = JsonSerializer.Deserialize<JsonElement>(
            """{"client_name":"Test","redirect_uris":["https://claude.ai/callback","https://attacker.com/callback"]}""");

        var result = _controller.Register(body);

        var obj = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var json = JsonSerializer.Serialize(obj.Value);
        json.Should().Contain("invalid_redirect_uri");
    }

    [Fact]
    public void Authorize_WithAttackerDomain_ReturnsBadRequest()
    {
        var result = _controller.Authorize(
            "client-123", "https://attacker.com/callback", "code",
            "state-abc", "challenge-xyz", "S256");

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var json = JsonSerializer.Serialize(bad.Value);
        json.Should().Contain("invalid_redirect_uri");
    }

    [Fact]
    public async Task VerifyCode_WithAttackerDomainRedirectUri_ReturnsBadRequest()
    {
        var request = new OAuthController.VerifyCodeRequest(
            "test@example.com", "123456", "state-abc",
            "challenge-xyz", "https://attacker.com/callback", "client-123");

        var result = await _controller.VerifyCode(request, CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var json = JsonSerializer.Serialize(bad.Value);
        json.Should().Contain("invalid_redirect_uri");
    }

    [Fact]
    public async Task GoogleAuth_WithAttackerDomainRedirectUri_ReturnsBadRequest()
    {
        var request = new OAuthController.GoogleAuthRequest(
            "valid-token", "state-abc", "challenge-xyz",
            "https://attacker.com/callback", "client-123");

        var result = await _controller.GoogleAuth(request, CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var json = JsonSerializer.Serialize(bad.Value);
        json.Should().Contain("invalid_redirect_uri");
    }


    [Fact]
    public async Task Token_WithAttackerDomainRedirectUri_ReturnsBadRequest()
    {
        var result = await _controller.Token(
            "authorization_code", "any-code", "verifier-xyz",
            "https://attacker.com/callback", CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var json = JsonSerializer.Serialize(bad.Value);
        json.Should().Contain("invalid_redirect_uri");
    }

    [Fact]
    public void Authorize_WithInvalidSchemeRedirectUri_ReturnsBadRequest()
    {
        var result = _controller.Authorize(
            "client-123", "javascript:alert('xss')", "code",
            "state-abc", "challenge-xyz", "S256");

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void Authorize_WithHttpSchemeRedirectUri_ReturnsBadRequest()
    {
        var result = _controller.Authorize(
            "client-123", "http://claude.ai/callback", "code",
            "state-abc", "challenge-xyz", "S256");

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void Authorize_WithFtpSchemeRedirectUri_ReturnsBadRequest()
    {
        var result = _controller.Authorize(
            "client-123", "ftp://claude.ai/callback", "code",
            "state-abc", "challenge-xyz", "S256");

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void Authorize_WithSubdomainAttempt_ReturnsBadRequest()
    {
        var result = _controller.Authorize(
            "client-123", "https://attacker.claude.ai/callback", "code",
            "state-abc", "challenge-xyz", "S256");

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var json = JsonSerializer.Serialize(bad.Value);
        json.Should().Contain("invalid_redirect_uri");
    }

    [Theory]
    [InlineData("https://attacker.com/callback")]
    [InlineData("https://evil.org/callback")]
    [InlineData("https://malicious.net/callback")]
    public async Task VerifyCode_RejectsUnallowlistedHosts(string redirectUri)
    {
        var request = new OAuthController.VerifyCodeRequest(
            "test@example.com", "123456", "state-abc",
            "challenge-xyz", redirectUri, "client-123");

        var result = await _controller.VerifyCode(request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var json = JsonSerializer.Serialize(bad.Value);
        json.Should().Contain("invalid_redirect_uri");
    }

    [Fact]
    public void Authorize_MissingState_ReturnsBadRequest()
    {
        var result = _controller.Authorize(
            "client-123", "https://claude.ai/callback", "code",
            "", "challenge-xyz", "S256");

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        JsonSerializer.Serialize(bad.Value).Should().Contain("invalid_request");
    }

    [Fact]
    public async Task VerifyCode_MissingState_ReturnsBadRequestAndDoesNotVerifyCode()
    {
        var request = new OAuthController.VerifyCodeRequest(
            "test@example.com", "123456", "",
            "challenge-xyz", "https://claude.ai/callback", "client-123");

        var result = await _controller.VerifyCode(request, CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        JsonSerializer.Serialize(bad.Value).Should().Contain("invalid_request");
        await _mediator.DidNotReceive().Send(Arg.Any<VerifyCodeCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GoogleAuth_MissingState_ReturnsBadRequestBeforeCallingGoogle()
    {
        var request = new OAuthController.GoogleAuthRequest(
            "valid-token", "", "challenge-xyz",
            "https://claude.ai/callback", "client-123");

        var result = await _controller.GoogleAuth(request, CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        JsonSerializer.Serialize(bad.Value).Should().Contain("invalid_request");
        _httpClientFactory.DidNotReceive().CreateClient();
    }


    [Fact]
    public async Task VerifyCode_EchoesStateVerbatim_SoClientCanDetectMismatch()
    {
        var state = "state-" + Guid.NewGuid().ToString("N");
        var loginResponse = new LoginResponse(UserId, "jwt-token", "Alex", "test@example.com");
        _mediator.Send(Arg.Any<VerifyCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(loginResponse));

        var request = new OAuthController.VerifyCodeRequest(
            "test@example.com", "123456", state,
            "challenge-xyz", "https://claude.ai/callback", "client-123");

        var redirectUrl = ExtractRedirectUrl(await _controller.VerifyCode(request, CancellationToken.None));

        ExtractQueryParam(redirectUrl, "state").Should().Be(state);
        redirectUrl.Should().NotContain("tampered-state");
    }

    [Fact]
    public async Task VerifyCode_StateWithQueryInjection_IsPercentEncodedSoNoParamSmuggling()
    {
        var tamperedState = "benign&code=forged-code&x=";
        var loginResponse = new LoginResponse(UserId, "jwt-token", "Alex", "test@example.com");
        _mediator.Send(Arg.Any<VerifyCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(loginResponse));

        var request = new OAuthController.VerifyCodeRequest(
            "test@example.com", "123456", tamperedState,
            "challenge-xyz", "https://claude.ai/callback", "client-123");

        var redirectUrl = ExtractRedirectUrl(await _controller.VerifyCode(request, CancellationToken.None));

        redirectUrl.Should().Contain("state=benign%26code%3Dforged-code");
        redirectUrl.Should().NotContain("&code=forged-code");
        ExtractQueryParam(redirectUrl, "code").Should().NotBe("forged-code");
        ExtractQueryParam(redirectUrl, "state").Should().Be(tamperedState);
    }

    [Fact]
    public async Task Token_WithNonce_EchoesNonceInResponse()
    {
        var (verifier, challenge) = GeneratePkce();
        var code = _authStore.CreateCode(
            UserId, challenge, "https://claude.ai/callback", "client-123", "replay-nonce");

        var result = await _controller.Token(
            "authorization_code", code, verifier,
            "https://claude.ai/callback", CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("nonce");
        json.Should().Contain("replay-nonce");
    }

    [Fact]
    public async Task Token_WithoutNonce_OmitsNonceFromResponse()
    {
        var (verifier, challenge) = GeneratePkce();
        var code = _authStore.CreateCode(
            UserId, challenge, "https://claude.ai/callback", "client-123");

        var result = await _controller.Token(
            "authorization_code", code, verifier,
            "https://claude.ai/callback", CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        JsonSerializer.Serialize(ok.Value).Should().NotContain("nonce");
    }

    [Fact]
    public async Task VerifyCode_WithNonce_BindsNonceRetrievableAtTokenExchange()
    {
        var (verifier, challenge) = GeneratePkce();
        var loginResponse = new LoginResponse(UserId, "jwt-token", "Alex", "test@example.com");
        _mediator.Send(Arg.Any<VerifyCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(loginResponse));

        var request = new OAuthController.VerifyCodeRequest(
            "test@example.com", "123456", "state-abc", challenge,
            "https://claude.ai/callback", "client-123", Nonce: "verify-nonce-7");
        var code = ExtractQueryParam(
            ExtractRedirectUrl(await _controller.VerifyCode(request, CancellationToken.None)), "code");

        var tokenResult = await _controller.Token(
            "authorization_code", code, verifier,
            "https://claude.ai/callback", CancellationToken.None);

        var ok = tokenResult.Should().BeOfType<OkObjectResult>().Subject;
        JsonSerializer.Serialize(ok.Value).Should().Contain("verify-nonce-7");
    }

    [Fact]
    public async Task GoogleAuth_WithNonce_BindsNonceRetrievableAtTokenExchange()
    {
        var (verifier, challenge) = GeneratePkce();
        var user = User.Create("Alex", "test@example.com").Value;
        var mockHandler = new MockHttpMessageHandler(HttpStatusCode.OK,
            """{"email":"test@example.com","aud":"test-google-client-id","name":"Alex"}""");
        _httpClientFactory.CreateClient().Returns(new HttpClient(mockHandler));
        _userRepo.FindOneTrackedIgnoringFiltersAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<User, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(user);

        var request = new OAuthController.GoogleAuthRequest(
            "valid-token", "state-abc", challenge,
            "https://claude.ai/callback", "client-123", Nonce: "google-nonce-42");
        var code = ExtractQueryParam(
            ExtractRedirectUrl(await _controller.GoogleAuth(request, CancellationToken.None)), "code");

        var tokenResult = await _controller.Token(
            "authorization_code", code, verifier,
            "https://claude.ai/callback", CancellationToken.None);

        var ok = tokenResult.Should().BeOfType<OkObjectResult>().Subject;
        JsonSerializer.Serialize(ok.Value).Should().Contain("google-nonce-42");
    }

    [Fact]
    public void Authorize_PageViewAllocatesNoPendingGoogleRequest()
    {
        var page = _controller.Authorize("client-123", "https://claude.ai/callback", "code",
            "client-state", "mcp-challenge", "S256", "mcp-nonce")
            .Should().BeOfType<ContentResult>().Subject;

        var link = System.Text.RegularExpressions.Regex.Match(page.Content!,
            "/oauth/google/start\\?([^\"]+)").Groups[1].Value;
        link.Should().NotBeEmpty();

        foreach (var pair in System.Net.WebUtility.HtmlDecode(link).Split('&'))
        {
            var value = Uri.UnescapeDataString(pair.Split('=', 2)[1]);
            _authStore.ConsumeGoogleRequest(value).Should().BeNull();
        }
    }

    [Theory]
    [InlineData("/oauth/authorize")]
    [InlineData("/oauth/google/start")]
    public void OAuthBrowserRoutes_CarryTheAuthRateLimit(string template)
    {
        var action = typeof(OAuthController).GetMethods()
            .SingleOrDefault(method => method.GetCustomAttributes(typeof(HttpGetAttribute), false)
                .Cast<HttpGetAttribute>().Any(attribute => attribute.Template == template));

        action.Should().NotBeNull();
        var limit = CustomAttributeData.GetCustomAttributes(action!)
            .Should().ContainSingle(data => data.AttributeType == typeof(DistributedRateLimitAttribute)).Subject;
        limit.ConstructorArguments[0].Value.Should().Be("auth");
    }

    private IActionResult StartGoogle(string challenge = "mcp-challenge",
        string clientState = "client-state", string? nonce = null) =>
        _controller.GoogleStart("client-123", "https://claude.ai/callback", clientState, challenge, "S256", nonce);

    private GoogleAuthorizationRequest CreatePendingGoogleRequest(string challenge = "mcp-challenge",
        string clientState = "client-state", string? nonce = null) =>
        _authStore.TryCreateGoogleRequest("client-123", "https://claude.ai/callback", clientState,
            challenge, nonce, "https://api.useorbit.org/oauth/google/callback", "en")!;

    private static string Challenge(string verifier) => Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
        .Replace("+", "-").Replace("/", "_").TrimEnd('=');

    private static (string verifier, string challenge) GeneratePkce()
    {
        var verifier = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        var challenge = Convert.ToBase64String(hash)
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');
        return (verifier, challenge);
    }

    private static string ExtractRedirectUrl(IActionResult result)
    {
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        return doc.RootElement.GetProperty("redirectUrl").GetString()!;
    }

    private static string ExtractQueryParam(string url, string key)
    {
        var query = url[(url.IndexOf('?') + 1)..];
        foreach (var pair in query.Split('&'))
        {
            var parts = pair.Split('=', 2);
            if (parts[0] == key)
                return parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
        }
        return string.Empty;
    }

    private sealed class FakeUniqueViolationException : DbException
    {
        public override string SqlState => "23505";
    }

    private sealed class MockHttpMessageHandler(HttpStatusCode statusCode, string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }

}
