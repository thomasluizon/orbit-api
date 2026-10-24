using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Models;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Persistence;

public class DesktopHandoffStoreTests : IDisposable
{
    private const string Redirect = "org.useorbit.desktop://auth/callback";
    private const string Verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    private const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";
    private readonly SqliteOrbitDbContextFactory _database = new();
    private readonly TestClock _clock = new();
    private readonly User _user = User.Create("Desktop User", "desktop@example.com").Value;

    public DesktopHandoffStoreTests()
    {
        _database.Context.Add(_user);
        _database.Context.SaveChanges();
    }

    [Fact]
    public async Task Handoff_CrossContextExchange_CreatesExactlyOneDurableSession()
    {
        var code = await Create();
        _database.Context.DesktopHandoffs.Single().CodeHash.Should().NotBe(code).And.HaveLength(64);
        await using var other = _database.CreateContext();
        var exchange = Exchange(other);
        var command = new ExchangeDesktopHandoffCommand(code, "state", Verifier, Redirect);

        var result = await exchange.Handle(command, default);
        var replay = await Exchange(_database.Context).Handle(command, default);

        result.IsSuccess.Should().BeTrue();
        result.Value.UserId.Should().Be(_user.Id);
        result.Value.Name.Should().Be(_user.Name);
        result.Value.Email.Should().Be(_user.Email);
        result.Value.Token.Should().Be("desktop-access");
        result.Value.RefreshToken.Should().NotBeNullOrWhiteSpace();
        result.Value.WasReactivated.Should().BeFalse();
        replay.Error.Should().Be(DesktopHandoff.InvalidHandoff);
        (await other.UserSessions.AsNoTracking().ToListAsync()).Should().ContainSingle().Which.UserId.Should().Be(_user.Id);
        (await other.DesktopHandoffs.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("state")]
    [InlineData("verifier")]
    [InlineData("redirect")]
    [InlineData("code")]
    public async Task Exchange_MismatchedBinding_DoesNotConsumeOrCreateSession(string invalid)
    {
        var code = await Create();
        await using var db = _database.CreateContext();
        var command = new ExchangeDesktopHandoffCommand(invalid == "code" ? new string('0', 64) : code,
            invalid == "state" ? "State" : "state", invalid == "verifier" ? new string('a', 43) : Verifier,
            invalid == "redirect" ? "org.useorbit.desktop.staging://auth/callback" : Redirect);

        var result = await Exchange(db).Handle(command, default);

        result.Error.Should().Be(DesktopHandoff.InvalidHandoff);
        (await db.UserSessions.CountAsync()).Should().Be(0);
        (await db.DesktopHandoffs.CountAsync()).Should().Be(1);
        (await Exchange(db).Handle(new(code, "state", Verifier, Redirect), default)).IsSuccess.Should().BeTrue();
        (await db.UserSessions.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData(59, true)]
    [InlineData(60, false)]
    [InlineData(61, false)]
    public async Task Exchange_ExpiresAtSixtySecondBoundary(int seconds, bool valid)
    {
        var code = await Create();
        _clock.Advance(TimeSpan.FromSeconds(seconds));
        await using var db = _database.CreateContext();

        var result = await Exchange(db).Handle(new(code, "state", Verifier, Redirect), default);

        result.IsSuccess.Should().Be(valid);
        (await db.UserSessions.CountAsync()).Should().Be(valid ? 1 : 0);
        if (!valid) result.Error.Should().Be(DesktopHandoff.InvalidHandoff);
    }

    [Fact]
    public async Task Store_ValidatesRedirectBindingEvenWithoutHandlerAllowlist()
    {
        var code = await Create();
        var hash = _database.Context.DesktopHandoffs.Single().CodeHash;
        var store = new DesktopHandoffStore(_database.Context);

        (await store.ConsumeAsync(hash, "state", Challenge, Redirect + "?extra=value", _clock.GetUtcNow().UtcDateTime, default))
            .Should().BeNull();
        (await Exchange(_database.Context).Handle(new(code, "state", Verifier, Redirect), default)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task AccountReset_RemovesOnlyTheTargetAccountsHandoffs()
    {
        await Create();
        var db = _database.Context;
        var other = User.Create("Other", "other@example.com").Value;
        db.Users.Add(other);
        db.DesktopHandoffs.Add(DesktopHandoff.Create(other.Id, new string('B', 64), "other-state", Challenge,
            "S256", Redirect, Redirect, _clock.GetUtcNow().UtcDateTime).Value);
        await db.SaveChangesAsync();

        await new AccountResetRepository(db, Substitute.For<Orbit.Domain.Events.IAccountEventCollector>())
            .DeleteAllUserDataAsync(_user.Id);

        (await db.DesktopHandoffs.AsNoTracking().ToListAsync()).Should().ContainSingle().Which.UserId.Should().Be(other.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_EnvironmentAllowsOnlyItsOwnCallback(bool staging)
    {
        var allowed = staging ? "org.useorbit.desktop.staging://auth/callback" : Redirect;
        var rejected = staging ? Redirect : "org.useorbit.desktop.staging://auth/callback";
        var db = _database.Context;
        var handler = new CreateDesktopHandoffCommandHandler(new DesktopHandoffStore(db), new GenericRepository<User>(db),
            new UnitOfWork(db, new DatabaseConnectionSettings()), Options.Create(new DesktopAuthOptions { RedirectUri = allowed }), _clock);

        (await handler.Handle(new(_user.Id, "state", Challenge, "S256", rejected), default)).Error.Should().Be(DesktopHandoff.InvalidHandoff);
        (await db.DesktopHandoffs.CountAsync()).Should().Be(0);
        (await handler.Handle(new(_user.Id, "state", Challenge, "S256", allowed), default)).IsSuccess.Should().BeTrue();
        (await db.DesktopHandoffs.SingleAsync()).RedirectUri.Should().Be(allowed);
    }

    private async Task<string> Create()
    {
        var db = _database.Context;
        var handler = new CreateDesktopHandoffCommandHandler(new DesktopHandoffStore(db), new GenericRepository<User>(db),
            new UnitOfWork(db, new DatabaseConnectionSettings()), Options.Create(new DesktopAuthOptions()), _clock);
        return (await handler.Handle(new(_user.Id, "state", Challenge, "S256", Redirect), default)).Value.Code;
    }

    private ExchangeDesktopHandoffCommandHandler Exchange(OrbitDbContext db)
    {
        var work = new UnitOfWork(db, new DatabaseConnectionSettings());
        var users = new GenericRepository<User>(db);
        var tokens = Substitute.For<ITokenService>();
        tokens.GenerateToken(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Guid>()).Returns("desktop-access");
        var sessions = new AuthSessionService(new GenericRepository<UserSession>(db), new GenericRepository<UserSessionRefreshToken>(db),
            users, tokens, work, Options.Create(new JwtSettings { SecretKey = "desktop-test-secret", Issuer = "test", Audience = "test" }));
        return new(new DesktopHandoffStore(db), users, sessions, work, Options.Create(new DesktopAuthOptions()), _clock);
    }

    public void Dispose() => _database.Dispose();

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
