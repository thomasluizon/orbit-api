using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Api.OAuth;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Tests.Controllers;

public class OAuthControllerTests : IDisposable
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly OAuthAuthorizationStore _authStore = new();
    private readonly IGenericRepository<ApiKey> _apiKeyRepo = Substitute.For<IGenericRepository<ApiKey>>();
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();
    private readonly ILogger<OAuthController> _logger = Substitute.For<ILogger<OAuthController>>();
    private readonly OAuthController _controller;

    private static readonly Guid UserId = Guid.NewGuid();

    public OAuthControllerTests()
    {
        var googleSettings = Options.Create(new GoogleSettings { ClientId = "test-google-client-id" });
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OAuth:AllowedRedirectHosts:0"] = "claude.ai",
                ["OAuth:AllowedRedirectHosts:1"] = "claude.com"
            })
            .Build();

        _controller = new OAuthController(
            _mediator, _authStore, _apiKeyRepo, _userRepo, _unitOfWork,
            _httpClientFactory, googleSettings, config, _logger);

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

    // --- GetMetadata ---

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

    // --- Register ---

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

    // --- GetProtectedResourceMetadata ---

    [Fact]
    public void GetProtectedResourceMetadata_ReturnsOkWithMcpResource()
    {
        var result = _controller.GetProtectedResourceMetadata();

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("/mcp");
        json.Should().Contain("bearer_methods_supported");
    }

    // --- Authorize ---

    [Fact]
    public void Authorize_ValidParams_ReturnsHtmlContent()
    {
        var result = _controller.Authorize(
            "client-123", "https://claude.ai/callback", "code",
            "state-abc", "challenge-xyz", "S256");

        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.ContentType.Should().Be("text/html");
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

    // --- SendCode ---

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
    public async Task SendCode_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<SendCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Rate limited"));

        var request = new OAuthController.SendCodeRequest("test@example.com");
        var result = await _controller.SendCode(request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
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

    // --- VerifyCode ---

    [Fact]
    public async Task VerifyCode_Success_ReturnsOkWithRedirectUrl()
    {
        var loginResponse = new LoginResponse(UserId, "jwt-token", "Thomas", "test@example.com");
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
    public async Task VerifyCode_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<VerifyCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<LoginResponse>("Invalid code"));

        var request = new OAuthController.VerifyCodeRequest(
            "test@example.com", "000000", "state-abc",
            "challenge-xyz", "https://claude.ai/callback", "client-123");

        var result = await _controller.VerifyCode(request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
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
        var loginResponse = new LoginResponse(UserId, "jwt-token", "Thomas", "test@example.com");
        _mediator.Send(Arg.Any<VerifyCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(loginResponse));

        var request = new OAuthController.VerifyCodeRequest(
            "test@example.com", "123456", "state-abc",
            "challenge-xyz", "https://claude.ai/callback?existing=1", "client-123");

        var result = await _controller.VerifyCode(request, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var json = JsonSerializer.Serialize(ok.Value);
        // JSON serializer encodes '&' as '\u0026'
        json.Should().Contain("callback?existing=1\\u0026code=");
    }

    // --- GoogleAuth ---

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
        json.Should().Contain("Invalid or expired Google sign-in token");
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
        json.Should().Contain("Could not retrieve email");
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
        json.Should().Contain("not issued for this application");
    }

    [Fact]
    public async Task GoogleAuth_ExistingUser_ReturnsRedirectUrl()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        var mockHandler = new MockHttpMessageHandler(HttpStatusCode.OK,
            """{"email":"test@example.com","aud":"test-google-client-id","name":"Thomas"}""");
        var httpClient = new HttpClient(mockHandler);
        _httpClientFactory.CreateClient().Returns(httpClient);

        _userRepo.FindOneTrackedAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(), Arg.Any<CancellationToken>())
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
    public async Task GoogleAuth_NewUser_CreatesUserAndReturnsRedirect()
    {
        var mockHandler = new MockHttpMessageHandler(HttpStatusCode.OK,
            """{"email":"new@example.com","aud":"test-google-client-id","name":"New User"}""");
        var httpClient = new HttpClient(mockHandler);
        _httpClientFactory.CreateClient().Returns(httpClient);

        _userRepo.FindOneTrackedAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(), Arg.Any<CancellationToken>())
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

        _userRepo.FindOneTrackedAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(), Arg.Any<CancellationToken>())
            .Returns((User?)null);

        var request = new OAuthController.GoogleAuthRequest(
            "valid-token", "state-abc", "challenge-xyz",
            "https://claude.ai/callback", "client-123");

        var result = await _controller.GoogleAuth(request, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        await _userRepo.Received(1).AddAsync(
            Arg.Is<User>(u => u.Name == "newuser"), Arg.Any<CancellationToken>());
    }

    // --- Token ---

    [Fact]
    public async Task Token_UnsupportedGrantType_ReturnsBadRequest()
    {
        var result = await _controller.Token(
            "client_credentials", "code-abc", "verifier-xyz",
            "client-123", "https://claude.ai/callback", CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var json = JsonSerializer.Serialize(bad.Value);
        json.Should().Contain("unsupported_grant_type");
    }

    [Fact]
    public async Task Token_InvalidCode_ReturnsBadRequest()
    {
        var result = await _controller.Token(
            "authorization_code", "nonexistent-code", "verifier-xyz",
            "client-123", "https://claude.ai/callback", CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var json = JsonSerializer.Serialize(bad.Value);
        json.Should().Contain("invalid_grant");
    }

    [Fact]
    public async Task Token_InvalidRedirectUri_ReturnsBadRequest()
    {
        var result = await _controller.Token(
            "authorization_code", "nonexistent-code", "verifier-xyz",
            "client-123", "https://evil.com/callback", CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        JsonSerializer.Serialize(bad.Value).Should().Contain("invalid_redirect_uri");
    }

    [Fact]
    public async Task Token_ValidCodeExchange_ReturnsAccessToken()
    {
        // Create a valid PKCE code challenge/verifier pair
        var codeVerifier = "test-verifier-that-is-long-enough-for-pkce-validation";
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        var codeChallenge = Convert.ToBase64String(hash)
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');

        var authCode = _authStore.CreateCode(
            UserId, codeChallenge, "https://claude.ai/callback", "client-123");

        var result = await _controller.Token(
            "authorization_code", authCode, codeVerifier,
            "client-123", "https://claude.ai/callback", CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("access_token");
        json.Should().Contain("Bearer");
        await _apiKeyRepo.Received(1).AddAsync(Arg.Any<ApiKey>(), Arg.Any<CancellationToken>());
    }

    // --- Helper: MockHttpMessageHandler ---

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
}
