using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using Hangfire;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Jobs;
using Orbit.Application.Auth.Queries;
using Orbit.Application.Auth.Services;
using Orbit.Application.Auth.Validators;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Auth;

public class AppleSignInTests
{
    private readonly IGenericRepository<User> _users = Substitute.For<IGenericRepository<User>>();
    private readonly IAuthUserStore _userStore = Substitute.For<IAuthUserStore>();
    private readonly IUnitOfWork _work = Substitute.For<IUnitOfWork>();
    private readonly IAuthSessionService _sessions = Substitute.For<IAuthSessionService>();
    private readonly IAppleIdentityService _apple = Substitute.For<IAppleIdentityService>();
    private readonly AppleSignInFlow _signIn;
    private readonly Clock _clock = new(DateTimeOffset.UtcNow);

    public AppleSignInTests()
    {
        _work.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result<LoginResponse>>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task<Result<LoginResponse>>>>()(default));
        _work.ExecuteOnceInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(default));
        _sessions.CreateSessionAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new SessionTokens("access", "refresh")));
        _userStore.UpsertAsync(Arg.Any<User>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<User>());
        _signIn = new(_users, _userStore, _work, _sessions, Substitute.For<ISignupJobDispatcher>(), Substitute.For<IBackgroundJobClient>(),
            new AccountReactivation(Substitute.For<IGenericRepository<OwedSubscriptionCancellation>>()));
        _apple.IsConfigured.Returns(true);
    }

    private static AppleIdentity Identity(string email = "apple@example.com") => new("subject", email, true, "apple-refresh", "org.useorbit.app");

    [Fact]
    public async Task VerifiedEmail_MatchesExistingUserId()
    {
        var existing = User.Create("Existing", "apple@example.com").Value;
        _userStore.UpsertAsync(Arg.Is<User>(candidate => candidate.Email == existing.Email), Arg.Any<CancellationToken>()).Returns(existing);
        var result = await _signIn.CompleteAsync(Identity(), "Apple", "en", null, default);
        result.Value.UserId.Should().Be(existing.Id);
        existing.AppleSubject.Should().Be("subject");
        existing.AppleRefreshToken.Should().Be("apple-refresh");
    }

    [Fact]
    public async Task SubjectMatch_PrecedesEmailAndSupportsMissingEmail()
    {
        var existing = User.Create("Existing", "old@example.com").Value;
        existing.SetAppleIdentity("subject", "old-refresh", "client");
        _users.FindOneTrackedIgnoringFiltersAsync(Arg.Any<Expression<Func<User, bool>>>(), Arg.Any<CancellationToken>()).Returns(existing);
        var result = await _signIn.CompleteAsync(Identity() with { Email = null, EmailVerified = false }, null, "en", null, default);
        result.Value.UserId.Should().Be(existing.Id);
        await _userStore.DidNotReceive().UpsertAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RelayEmail_CreatesSeparateAccount()
    {
        var result = await _signIn.CompleteAsync(Identity("relay@privaterelay.appleid.com"), null, "en", null, default);
        result.Value.Email.Should().Be("relay@privaterelay.appleid.com");
        await _userStore.Received().UpsertAsync(Arg.Is<User>(user => user.Email == result.Value.Email), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnverifiedEmail_CannotProvisionOrMatch()
    {
        var result = await _signIn.CompleteAsync(Identity() with { EmailVerified = false }, null, "en", null, default);
        result.IsFailure.Should().BeTrue();
        await _userStore.DidNotReceive().UpsertAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("replay")]
    [InlineData("wrong-state")]
    [InlineData("expired")]
    public void Handoff_IsSingleUseBoundToStateAndExpires(string failure)
    {
        var flow = AuthorizationFlow();
        var handoff = flow.IssueHandoff(new(new(Guid.NewGuid(), "access", "Apple", "apple@example.com"),
            new("client-state", "nonce", "en", null, "https://api.example.com/callback")))!;
        if (failure == "expired") _clock.Now += TimeSpan.FromSeconds(61);
        var first = flow.TakeHandoff(handoff, failure == "wrong-state" ? "attacker-state" : "client-state");
        first.IsSuccess.Should().Be(failure == "replay");
        flow.TakeHandoff(handoff, "client-state").IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task WebCallback_UsesServerStateAndProducesHandoffWithoutCookies()
    {
        _apple.AuthenticateWebAsync("code", Arg.Any<string>(), "https://api.example.com/callback", Arg.Any<CancellationToken>()).Returns(Identity());
        var flow = AuthorizationFlow();
        var start = new Uri(flow.Start("client-state", "en", null)!);
        var query = start.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2)).ToDictionary(part => part[0], part => Uri.UnescapeDataString(part[1]));
        var result = await flow.CompleteWebCallbackAsync(query["state"], "code", null, default);
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().StartWith("https://app.example.com/auth-callback?provider=apple&handoff=").And.EndWith("&state=client-state");
        result.Value.Should().NotContain("access").And.NotContain("refresh");
        (await flow.CompleteWebCallbackAsync(query["state"], "code", null, default)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Deletion_RevokeOnceAndSkipCompletedJob()
    {
        var user = User.Create("Apple", "apple@example.com").Value;
        user.SetAppleIdentity("subject", "apple-refresh", "org.useorbit.app");
        user.Deactivate(DateTime.UtcNow.AddDays(7));
        _users.FindOneTrackedIgnoringFiltersAsync(Arg.Any<Expression<Func<User, bool>>>(), Arg.Any<CancellationToken>()).Returns(user);
        var job = new RevokeAppleTokenOnDeletionJob(_apple, _users, _work);
        await job.ExecuteAsync(user.Id, default);
        await job.ExecuteAsync(user.Id, default);
        await _apple.Received(1).RevokeAsync("apple-refresh", "org.useorbit.app", Arg.Any<CancellationToken>());
        user.AppleRefreshToken.Should().BeNull();
    }

    [Fact]
    public void Validators_RejectMissingAndOversizedCredentials()
    {
        new AppleNativeSignInCommandValidator().Validate(new AppleNativeSignInCommand("", "", "", null, null)).IsValid.Should().BeFalse();
        new StartAppleSignInCommandValidator().Validate(new StartAppleSignInCommand("short")).IsValid.Should().BeFalse();
        new CompleteAppleCallbackCommandValidator().Validate(new CompleteAppleCallbackCommand("", "")).IsValid.Should().BeFalse();
        new TakeAppleHandoffCommandValidator().Validate(new TakeAppleHandoffCommand("", "")).IsValid.Should().BeFalse();
    }

    private AppleAuthorizationFlow AuthorizationFlow()
    {
        var states = Substitute.For<IOAuthStateStore>();
        var entries = new Dictionary<(string, string), (string Payload, DateTime Expiry)>();
        states.Put(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<int?>()).Returns(call =>
        { entries[(call.ArgAt<string>(0), call.ArgAt<string>(1))] = (call.ArgAt<string>(2), call.ArgAt<DateTime>(3)); return true; });
        states.Take(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>()).Returns(call =>
        {
            var key = (call.ArgAt<string>(0), call.ArgAt<string>(1));
            if (!entries.Remove(key, out var entry) || entry.Expiry <= call.ArgAt<DateTime>(2)) return null;
            return entry.Payload;
        });
        return new(states, _apple, _signIn, Options.Create(new AppleSignInSettings
        { WebClientId = "web", RedirectUri = "https://api.example.com/callback", WebOrigin = "https://app.example.com" }), _clock);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
