using System.Linq.Expressions;
using System.Net;
using System.Text;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Auth.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Auth;

public class GoogleAuthCommandHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ITokenService _tokenService = Substitute.For<ITokenService>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly FakeHttpMessageHandler _httpHandler = new();
    private readonly GoogleAuthCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private const string TestEmail = "google@example.com";

    public GoogleAuthCommandHandlerTests()
    {
        var httpClient = new HttpClient(_httpHandler)
        {
            BaseAddress = new Uri("https://supabase.example.com")
        };
        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient("Supabase").Returns(httpClient);

        _handler = new GoogleAuthCommandHandler(
            _userRepo, _unitOfWork, _tokenService, httpFactory, _emailService, _mediator,
            Substitute.For<ILogger<GoogleAuthCommandHandler>>());

        _tokenService.GenerateToken(Arg.Any<Guid>(), Arg.Any<string>())
            .Returns("jwt-token");
    }

    [Fact]
    public async Task Handle_ExistingUser_ReturnsLoginResponse()
    {
        var user = User.Create("Google User", TestEmail).Value;
        SetupGoogleTokenResponse(TestEmail, "Google User");
        SetupExistingUser(user);

        var command = new GoogleAuthCommand("valid-token");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Token.Should().Be("jwt-token");
        result.Value.Email.Should().Be(TestEmail);
        result.Value.WasReactivated.Should().BeFalse();
        await _userRepo.DidNotReceive().AddAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NewUser_CreatesUserAndReturnsLoginResponse()
    {
        SetupGoogleTokenResponse(TestEmail, "New User");
        // No existing user -- FindOneTrackedAsync returns null by default

        var command = new GoogleAuthCommand("valid-token");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Token.Should().Be("jwt-token");
        await _userRepo.Received(1).AddAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_InvalidToken_ReturnsFailure()
    {
        _httpHandler.SetResponse(HttpStatusCode.Unauthorized, "{}");

        var command = new GoogleAuthCommand("invalid-token");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Invalid");
        await _userRepo.DidNotReceive().AddAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_DeactivatedUser_ReactivatesAndReturnsFlag()
    {
        var user = User.Create("Deactivated User", TestEmail).Value;
        user.Deactivate(DateTime.UtcNow.AddDays(7));

        SetupGoogleTokenResponse(TestEmail, "Deactivated User");
        SetupExistingUser(user);

        var command = new GoogleAuthCommand("valid-token");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.WasReactivated.Should().BeTrue();
        user.IsDeactivated.Should().BeFalse();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WithGoogleTokens_StoresTokensOnUser()
    {
        var user = User.Create("Google User", TestEmail).Value;
        SetupGoogleTokenResponse(TestEmail, "Google User");
        SetupExistingUser(user);

        var command = new GoogleAuthCommand("valid-token", GoogleAccessToken: "gat-123", GoogleRefreshToken: "grt-456");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.GoogleAccessToken.Should().Be("gat-123");
        user.GoogleRefreshToken.Should().Be("grt-456");
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    private void SetupGoogleTokenResponse(string email, string name)
    {
        var json = $$"""
            {
                "email": "{{email}}",
                "user_metadata": {
                    "full_name": "{{name}}"
                }
            }
            """;
        _httpHandler.SetResponse(HttpStatusCode.OK, json);
    }

    private void SetupExistingUser(User user)
    {
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);
    }

    /// <summary>
    /// Fake HTTP handler for testing Google token validation via Supabase.
    /// </summary>
    internal class FakeHttpMessageHandler : HttpMessageHandler
    {
        private HttpStatusCode _statusCode = HttpStatusCode.OK;
        private string _content = "{}";

        public void SetResponse(HttpStatusCode statusCode, string content)
        {
            _statusCode = statusCode;
            _content = content;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_content, Encoding.UTF8, "application/json")
            });
        }
    }
}
