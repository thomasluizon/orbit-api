using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Orbit.Application.Auth.Jobs;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Queries;
using Orbit.Application.Behaviors;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Application.Common;

namespace Orbit.Application.Tests.Commands.Auth;

public class GoogleAuthCommandHandlerTests
{
    private readonly IAuthUserStore _userRepo = Substitute.For<IAuthUserStore>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IAuthSessionService _authSessionService = Substitute.For<IAuthSessionService>();
    private readonly IBackgroundJobClient _jobs = Substitute.For<IBackgroundJobClient>();
    private readonly FakeHttpMessageHandler _httpHandler = new();
    private readonly GoogleAuthCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private const string TestEmail = "google@example.com";

    public GoogleAuthCommandHandlerTests()
    {
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result<Orbit.Application.Auth.Queries.LoginResponse>>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task<Result<Orbit.Application.Auth.Queries.LoginResponse>>>>()(call.Arg<CancellationToken>()));
        var httpClient = new HttpClient(_httpHandler)
        {
            BaseAddress = new Uri("https://supabase.example.com")
        };
        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient("Supabase").Returns(httpClient);

        var flow = new GoogleSignInFlow(_userRepo, _unitOfWork, _authSessionService, _jobs);
        _handler = new GoogleAuthCommandHandler(httpFactory, flow);

        _userRepo.UpsertAsync(Arg.Any<User>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<User>());
        _authSessionService.CreateSessionAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new SessionTokens("jwt-token", "refresh-token")));
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
        await _userRepo.Received(1).UpsertAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NewUser_CreatesUserAndReturnsLoginResponse()
    {
        SetupGoogleTokenResponse(TestEmail, "New User");

        var command = new GoogleAuthCommand("valid-token");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Token.Should().Be("jwt-token");
        await _userRepo.Received(1).UpsertAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_InvalidToken_ReturnsFailure()
    {
        _httpHandler.SetResponse(HttpStatusCode.Unauthorized, "{}");

        var command = new GoogleAuthCommand("invalid-token");
        var behavior = new ConcurrencyRetryBehavior<GoogleAuthCommand, Result<LoginResponse>>(_unitOfWork);

        var result = await behavior.Handle(command, ct => _handler.Handle(command, ct), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.InvalidGoogleToken.Message);
        _httpHandler.RequestCount.Should().Be(1);
        _unitOfWork.DidNotReceive().ResetTracking();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        await _authSessionService.DidNotReceive().CreateSessionAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _userRepo.DidNotReceive().UpsertAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
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
        var staleUser = User.Create("Google User", TestEmail).Value;
        var currentUser = User.Create("Updated profile", TestEmail).Value;
        currentUser.SetSelectedCalendars(["calendar-1"]);
        currentUser.Deactivate(DateTime.UtcNow.AddDays(7));
        SetupGoogleTokenResponse(TestEmail, "Google User");
        var trackerWasReset = false;
        _unitOfWork.When(work => work.ResetTracking()).Do(_ => trackerWasReset = true);
        _userRepo.UpsertAsync(Arg.Any<User>(), Arg.Any<CancellationToken>())
            .Returns(_ => trackerWasReset ? currentUser : staleUser);
        var saves = 0;
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => ++saves == 1
                ? throw new DbUpdateConcurrencyException("stale user")
                : Task.FromResult(1));

        var command = new GoogleAuthCommand("valid-token", GoogleAccessToken: "gat-123", GoogleRefreshToken: "grt-456");
        var behavior = new ConcurrencyRetryBehavior<GoogleAuthCommand, Result<LoginResponse>>(_unitOfWork);

        var result = await behavior.Handle(command, ct => _handler.Handle(command, ct), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.WasReactivated.Should().BeTrue();
        result.Value.UserId.Should().Be(currentUser.Id);
        currentUser.Name.Should().Be("Updated profile");
        currentUser.GetSelectedCalendarIds().Should().Equal("calendar-1");
        currentUser.GoogleAccessToken.Should().Be("gat-123");
        currentUser.GoogleRefreshToken.Should().Be("grt-456");
        currentUser.IsDeactivated.Should().BeFalse();
        trackerWasReset.Should().BeTrue();
        saves.Should().Be(2);
        await _authSessionService.Received(1).CreateSessionAsync(currentUser.Id, currentUser.Email, Arg.Any<CancellationToken>());

    }

    [Fact]
    public async Task Handle_WithGoogleTokens_PersistentConflict_StopsAfterThreeAttempts()
    {
        var user = User.Create("Google User", TestEmail).Value;
        SetupGoogleTokenResponse(TestEmail, "Google User");
        SetupExistingUser(user);
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new DbUpdateConcurrencyException("stale user"));

        var command = new GoogleAuthCommand("valid-token", GoogleAccessToken: "gat-123", GoogleRefreshToken: "grt-456");
        var behavior = new ConcurrencyRetryBehavior<GoogleAuthCommand, Result<LoginResponse>>(_unitOfWork);

        var act = async () => await behavior.Handle(command, ct => _handler.Handle(command, ct), CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        await _unitOfWork.Received(3).SaveChangesAsync(Arg.Any<CancellationToken>());
        _unitOfWork.Received(2).ResetTracking();
        await _authSessionService.DidNotReceive().CreateSessionAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

    }

    [Fact]
    public async Task Handle_MixedCaseGoogleEmail_LogsIntoExistingLowercaseAccount()
    {
        var existingUser = User.Create("Existing", "google@example.com").Value;
        SetupGoogleTokenResponse("Google@Example.com", "Existing");
        _userRepo.UpsertAsync(Arg.Any<User>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<User>().Email == existingUser.Email ? existingUser : call.Arg<User>());

        var result = await _handler.Handle(new GoogleAuthCommand("valid-token"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Email.Should().Be("google@example.com");
        await _userRepo.Received(1).UpsertAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_AlwaysEnqueuesSignupDecision(bool exists)
    {
        SetupGoogleTokenResponse(TestEmail, "Google User");
        if (exists)
            SetupExistingUser(User.Create("Existing", TestEmail).Value);

        var result = await _handler.Handle(new GoogleAuthCommand("valid-token", "pt-BR", ReferralCode: "friend"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _jobs.Received(1).Create(Arg.Is<Job>(job => job.Type == typeof(CompleteSignupJob)
            && (Guid)job.Args[0] == result.Value.UserId && (bool)job.Args[1] == !exists
            && (string)job.Args[2] == "pt-BR" && (string)job.Args[3] == "friend"), Arg.Any<IState>());
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
        _userRepo.UpsertAsync(Arg.Any<User>(), Arg.Any<CancellationToken>())
            .Returns(user);
    }

    /// <summary>
    /// Fake HTTP handler for testing Google token validation via Supabase.
    /// </summary>
    internal class FakeHttpMessageHandler : HttpMessageHandler
    {
        private HttpStatusCode _statusCode = HttpStatusCode.OK;
        private string _content = "{}";

        public int RequestCount { get; private set; }

        public void SetResponse(HttpStatusCode statusCode, string content)
        {
            _statusCode = statusCode;
            _content = content;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_content, Encoding.UTF8, "application/json")
            });
        }
    }
}
