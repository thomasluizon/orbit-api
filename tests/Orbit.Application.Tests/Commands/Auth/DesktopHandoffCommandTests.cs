using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Models;
using Orbit.Application.Auth.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Auth;

public class DesktopHandoffCommandTests
{
    private const string Redirect = "org.useorbit.desktop://auth/callback";
    private const string Verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    private const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";
    private readonly IDesktopHandoffStore _handoffs = Substitute.For<IDesktopHandoffStore>();
    private readonly IGenericRepository<User> _users = Substitute.For<IGenericRepository<User>>();
    private readonly IAuthSessionService _sessions = Substitute.For<IAuthSessionService>();
    private readonly IUnitOfWork _work = Substitute.For<IUnitOfWork>();
    private readonly User _user = User.Create("Desktop User", "desktop@example.com").Value;
    private readonly TestClock _clock = new();
    private readonly CreateDesktopHandoffCommandHandler _create;
    private readonly ExchangeDesktopHandoffCommandHandler _exchange;
    private DesktopHandoff? _stored;

    public DesktopHandoffCommandTests()
    {
        var options = Options.Create(new DesktopAuthOptions());
        _create = new(_handoffs, _users, _work, options, _clock);
        _exchange = new(_handoffs, _users, _sessions, _work, options, _clock);
        _users.GetByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_user);
        _handoffs.AddAsync(Arg.Do<DesktopHandoff>(value => _stored = value), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _handoffs.ConsumeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var stored = _stored;
                if (stored is null || stored.CodeHash != call.ArgAt<string>(0) || stored.State != call.ArgAt<string>(1)
                    || stored.CodeChallenge != call.ArgAt<string>(2) || stored.RedirectUri != call.ArgAt<string>(3)
                    || stored.ExpiresAtUtc <= call.Arg<DateTime>())
                    return (Guid?)null;
                _stored = null;
                return (Guid?)stored.UserId;
            });
        _sessions.CreateSessionAsync(_user.Id, _user.Email, Arg.Any<CancellationToken>())
            .Returns(Result.Success(new SessionTokens("access-token", "refresh-token")));
        _work.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result<LoginResponse>>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task<Result<LoginResponse>>>>()(call.Arg<CancellationToken>()));
    }

    [Fact]
    public async Task Create_PersistsHashAndBindings_WithoutIssuingSession()
    {
        var first = await Create();
        var firstStored = _stored!;
        var second = await Create();

        first.Value.ExpiresIn.Should().Be(60);
        first.Value.Code.Should().HaveLength(64).And.NotBe(second.Value.Code);
        firstStored.CodeHash.Should().Be(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(first.Value.Code))))
            .And.NotBe(first.Value.Code);
        firstStored.UserId.Should().Be(_user.Id);
        firstStored.State.Should().Be("state");
        firstStored.CodeChallenge.Should().Be(Challenge);
        firstStored.RedirectUri.Should().Be(Redirect);
        firstStored.ExpiresAtUtc.Should().Be(_clock.GetUtcNow().UtcDateTime.AddSeconds(60));
        await _work.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _sessions.DidNotReceiveWithAnyArgs().CreateSessionAsync(default, "", default);
    }

    [Fact]
    public async Task Exchange_ValidRfc7636Verifier_IssuesExistingLoginPayloadOnce()
    {
        var handoff = await Create();
        var command = new ExchangeDesktopHandoffCommand(handoff.Value.Code, "state", Verifier, Redirect);

        var result = await _exchange.Handle(command, default);
        var replay = await _exchange.Handle(command, default);

        result.Value.Should().Be(new LoginResponse(_user.Id, "access-token", _user.Name, _user.Email, false, "refresh-token"));
        replay.IsFailure.Should().BeTrue();
        replay.Error.Should().Be(DesktopHandoff.InvalidHandoff);
        await _sessions.Received(1).CreateSessionAsync(_user.Id, _user.Email, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("state")]
    [InlineData("state-case")]
    [InlineData("verifier")]
    [InlineData("redirect")]
    [InlineData("code")]
    [InlineData("expired")]
    public async Task Exchange_InvalidBinding_ReturnsGenericFailureWithoutSession(string invalid)
    {
        var handoff = await Create();
        if (invalid == "expired") _clock.Advance(TimeSpan.FromSeconds(60));
        var command = new ExchangeDesktopHandoffCommand(
            invalid == "code" ? new string('0', 64) : handoff.Value.Code,
            invalid == "state" ? "wrong" : invalid == "state-case" ? "State" : "state",
            invalid == "verifier" ? new string('a', 43) : Verifier,
            invalid == "redirect" ? "org.useorbit.desktop.staging://auth/callback" : Redirect);

        var result = await _exchange.Handle(command, default);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(DesktopHandoff.InvalidHandoff);
        await _sessions.DidNotReceiveWithAnyArgs().CreateSessionAsync(default, "", default);
        if (invalid != "expired")
            (await _exchange.Handle(new(handoff.Value.Code, "state", Verifier, Redirect), default)).IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exchange_MissingOrDeactivatedAccount_DoesNotIssueSession(bool deactivate)
    {
        var handoff = await Create();
        if (deactivate) _user.Deactivate(DateTime.UtcNow.AddDays(7));
        else _users.GetByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns((User?)null);

        var result = await _exchange.Handle(new(handoff.Value.Code, "state", Verifier, Redirect), default);

        result.Error.Should().Be(DesktopHandoff.InvalidHandoff);
        await _sessions.DidNotReceiveWithAnyArgs().CreateSessionAsync(default, "", default);
    }

    [Theory]
    [InlineData("redirect")]
    [InlineData("method")]
    [InlineData("user")]
    [InlineData("deactivated")]
    public async Task Create_InvalidRequestOrAccount_DoesNotPersist(string invalid)
    {
        if (invalid == "deactivated") _user.Deactivate(DateTime.UtcNow.AddDays(7));
        if (invalid == "user") _users.GetByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns((User?)null);

        var result = await _create.Handle(new(_user.Id, "state", Challenge, invalid == "method" ? "plain" : "S256",
            invalid == "redirect" ? "https://attacker.example/callback" : Redirect), default);

        result.Error.Should().Be(DesktopHandoff.InvalidHandoff);
        await _handoffs.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await _work.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Fact]
    public async Task Exchange_SessionFailure_ReturnsGenericFailure()
    {
        var handoff = await Create();
        _sessions.CreateSessionAsync(_user.Id, _user.Email, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<SessionTokens>("internal session failure"));

        var result = await _exchange.Handle(new(handoff.Value.Code, "state", Verifier, Redirect), default);

        result.Error.Should().Be(DesktopHandoff.InvalidHandoff);
    }

    private Task<Result<DesktopHandoffResponse>> Create() =>
        _create.Handle(new(_user.Id, "state", Challenge, "S256", Redirect), default);

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
