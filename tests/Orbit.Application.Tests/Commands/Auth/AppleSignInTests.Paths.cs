using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Queries;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Auth;

public partial class AppleSignInTests
{
    [Fact]
    public async Task NativeHandler_VerifiedIdentity_CompletesSessionAndJoinsNames()
    {
        _apple.AuthenticateNativeAsync("identity", "code", "nonce", Arg.Any<CancellationToken>()).Returns(Identity());
        var result = await new AppleNativeSignInCommandHandler(_apple, _signIn)
            .Handle(new("identity", "code", "nonce", "  First", "Last  ", "pt", "referral"), default);
        result.IsSuccess.Should().BeTrue();
        result.Value.Name.Should().Be("First Last");
        result.Value.Token.Should().Be("access");
        await _userStore.Received().UpsertAsync(Arg.Is<User>(user => user.Language == "pt" && user.SignupReferralCode == "referral"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NativeHandler_InvalidAppleIdentity_DoesNotCreateSession()
    {
        _apple.AuthenticateNativeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new AppleVerificationException());
        var result = await new AppleNativeSignInCommandHandler(_apple, _signIn).Handle(new("forged", "code", "nonce", null, null), default);
        result.ErrorCode.Should().Be(ErrorCodes.InvalidSession);
        await _sessions.DidNotReceive().CreateSessionAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DifferentSubjectOnEmailMatch_DoesNotReplaceIdentity()
    {
        var user = User.Create("Existing", "apple@example.com").Value;
        user.SetAppleIdentity("another-subject", "stored-refresh", "client");
        _userStore.UpsertAsync(Arg.Any<User>(), Arg.Any<CancellationToken>()).Returns(user);
        var result = await _signIn.CompleteAsync(Identity(), "Apple", "en", null, default);
        result.IsFailure.Should().BeTrue();
        user.AppleSubject.Should().Be("another-subject");
        user.AppleRefreshToken.Should().Be("stored-refresh");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-email")]
    public async Task NewSubject_InvalidEmail_CannotProvision(string? email)
    {
        var result = await _signIn.CompleteAsync(Identity() with { Email = email }, null, "en", null, default);
        result.IsFailure.Should().BeTrue();
        await _sessions.DidNotReceive().CreateSessionAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SessionFailure_PropagatesWithoutSuccessfulLogin()
    {
        _sessions.CreateSessionAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<SessionTokens>(ErrorMessages.InvalidSession));
        (await _signIn.CompleteAsync(Identity(), null, "en", null, default)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task SubjectMatch_ReactivatesDeactivatedAccount()
    {
        var user = User.Create("Existing", "apple@example.com").Value;
        user.SetAppleIdentity("subject", "old-refresh", "client");
        user.Deactivate(DateTime.UtcNow.AddDays(7));
        _users.FindOneTrackedIgnoringFiltersAsync(Arg.Any<Expression<Func<User, bool>>>(), Arg.Any<CancellationToken>()).Returns(user);
        var result = await _signIn.CompleteAsync(Identity(), null, "en", null, default);
        result.Value.WasReactivated.Should().BeTrue();
        user.IsDeactivated.Should().BeFalse();
    }

    [Fact]
    public async Task WebHandlers_CompleteCallbackAndConsumeHandoff()
    {
        var flow = AuthorizationFlow();
        _apple.AuthenticateWebAsync("code", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Identity());
        var url = await new StartAppleSignInCommandHandler(flow).Handle(new("client-state"), default);
        var callback = await new CompleteAppleCallbackCommandHandler(flow).Handle(new(StateFrom(url!), "code", "{\"name\":{\"firstName\":\"Apple\",\"lastName\":\"User\"}}"), default);
        callback.IsSuccess.Should().BeTrue();
        var handoff = System.Web.HttpUtility.ParseQueryString(new Uri(callback.Value).Query)["handoff"]!;
        var handler = new TakeAppleHandoffCommandHandler(flow);
        var login = await handler.Handle(new(handoff, "client-state"), default);
        login.Value.Name.Should().Be("Apple User");
        (await handler.Handle(new(handoff, "client-state"), default)).IsFailure.Should().BeTrue();
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{}")]
    [InlineData("{\"name\":{\"firstName\":12}}")]
    [InlineData("{\"name\":{\"firstName\":\"bad\\nname\"}}")]
    public async Task Callback_UnusableOptionalName_UsesVerifiedEmailName(string name)
    {
        var flow = AuthorizationFlow();
        _apple.AuthenticateWebAsync("code", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Identity());
        var result = await flow.CompleteAsync(StateFrom(flow.Start("client", "en", null)!), "code", name, false, default);
        result.Value.Login.Name.Should().Be("apple");
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("unknown-state")]
    [InlineData("expired")]
    [InlineData("mcp-mismatch")]
    [InlineData("invalid-identity")]
    [InlineData("login-failed")]
    public async Task Callback_InvalidStateOrIdentity_Rejects(string failure)
    {
        var flow = AuthorizationFlow();
        var state = StateFrom(flow.Start("client", "en", null)!);
        _apple.AuthenticateWebAsync("code", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Identity());
        if (failure == "disabled") _apple.IsConfigured.Returns(false);
        if (failure == "unknown-state") state = "unknown";
        if (failure == "expired") _clock.Now += TimeSpan.FromMinutes(6);
        if (failure == "invalid-identity")
            _apple.AuthenticateWebAsync("code", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new AppleVerificationException());
        if (failure == "login-failed")
            _apple.AuthenticateWebAsync("code", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Identity() with { EmailVerified = false });
        (await flow.CompleteAsync(state, "code", null, failure == "mcp-mismatch", default)).IsFailure.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Start_DisabledOrCapacityExhausted_DoesNotIssueUrl(bool exhausted)
    {
        var flow = AuthorizationFlow(refuseStorage: exhausted);
        if (!exhausted) _apple.IsConfigured.Returns(false);
        flow.Start("state", "en", null).Should().BeNull();
        var handoff = flow.IssueHandoff(new(new(Guid.NewGuid(), "access", "name", "email"), new("state", "nonce", "en", null, "uri")));
        if (exhausted) handoff.Should().BeNull();
        else handoff.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task McpCallback_RequiresConfigurationAndPreservesContext()
    {
        AuthorizationFlow().Start("client", "en", null, "context").Should().BeNull();
        var flow = AuthorizationFlow(mcpConfigured: true);
        _apple.AuthenticateWebAsync("code", Arg.Any<string>(), "https://api.example.com/oauth/apple/callback", Arg.Any<CancellationToken>()).Returns(Identity());
        var result = await flow.CompleteAsync(StateFrom(flow.Start("client", "pt", "referral", "context")!), "code", null, true, default);
        result.Value.Request.McpContext.Should().Be("context");
        result.Value.Request.ClientState.Should().Be("client");
    }

    [Fact]
    public void Handoff_Disabled_DoesNotReturnSession()
    {
        var flow = AuthorizationFlow();
        var handoff = flow.IssueHandoff(new(new(Guid.NewGuid(), "access", "name", "email"), new("state", "nonce", "en", null, "uri")))!;
        _apple.IsConfigured.Returns(false);
        flow.TakeHandoff(handoff, "state").IsFailure.Should().BeTrue();
    }

    private static string StateFrom(string url) => System.Web.HttpUtility.ParseQueryString(new Uri(url).Query)["state"]!;
}
