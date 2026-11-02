using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbit.Application.Auth.Services;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public class PostgresEmailChallengeStoreTests
{
    [Fact]
    public async Task SeparateContexts_KeepHashesCooldownAttemptsAndConsumption()
    {
        using var database = new SqliteOrbitDbContextFactory();
        const string email = "user@example.com";
        const string code = "123456";
        using (var issuer = database.CreateContext())
        {
            (await Store(issuer).IssueAsync(EmailChallengeOperation.SignIn, email, code)).IsSuccess.Should().BeTrue();
        }
        using (var verifier = database.CreateContext())
        {
            var store = Store(verifier);
            (await store.IssueAsync(EmailChallengeOperation.SignIn, email)).ErrorCode.Should().Be("CODE_REQUEST_COOLDOWN");
            var row = await verifier.EmailChallenges.AsNoTracking().SingleAsync();
            row.CodeHash.Should().HaveCount(32);
            row.CodeHash.Should().NotEqual(System.Text.Encoding.UTF8.GetBytes(code));
            (await store.ConfirmAsync(EmailChallengeOperation.SignIn, email, "000000")).IsFailure.Should().BeTrue();
        }
        using (var verifier = database.CreateContext())
        {
            (await verifier.EmailChallenges.SingleAsync()).Attempts.Should().Be(1);
            (await Store(verifier).ConfirmAsync(EmailChallengeOperation.SignIn, email, code)).IsSuccess.Should().BeTrue();
        }
        using (var verifier = database.CreateContext())
            (await Store(verifier).ConfirmAsync(EmailChallengeOperation.SignIn, email, code)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task AttemptCap_AppliesAcrossFreshContexts()
    {
        using var database = new SqliteOrbitDbContextFactory();
        var clock = new TestClock();
        using (var issuer = database.CreateContext())
            await Store(issuer, clock).IssueAsync(EmailChallengeOperation.SignIn, "user@example.com", "123456");

        clock.Advance(TimeSpan.FromMinutes(14));
        using (var issuer = database.CreateContext())
            await Store(issuer, clock).IssueAsync(EmailChallengeOperation.SignIn, "user@example.com", "123456");

        for (var attempt = 0; attempt < 6; attempt++)
        {
            using var verifier = database.CreateContext();
            var result = await Store(verifier, clock).ConfirmAsync(EmailChallengeOperation.SignIn, "user@example.com", "000000");
            result.IsFailure.Should().BeTrue();
            if (attempt >= 3)
                result.ErrorCode.Should().Be("TOO_MANY_ATTEMPTS");
        }
        using var final = database.CreateContext();
        (await final.EmailChallenges.SingleAsync()).Attempts.Should().Be(3);
        (await Store(final, clock).ConfirmAsync(EmailChallengeOperation.SignIn, "user@example.com", "123456"))
            .IsFailure.Should().BeTrue();

        clock.Advance(TimeSpan.FromMinutes(2));
        using (var issuer = database.CreateContext())
            (await Store(issuer, clock).IssueAsync(EmailChallengeOperation.SignIn, "user@example.com", "654321"))
                .IsSuccess.Should().BeTrue();
        using var afterResend = database.CreateContext();
        (await Store(afterResend, clock).ConfirmAsync(EmailChallengeOperation.SignIn, "user@example.com", "654321"))
            .ErrorCode.Should().Be("TOO_MANY_ATTEMPTS", "a resend must preserve recent failed attempts across the issuance window boundary");

        clock.Advance(TimeSpan.FromMinutes(15));
        using (var issuer = database.CreateContext())
            (await Store(issuer, clock).IssueAsync(EmailChallengeOperation.SignIn, "user@example.com", "654321"))
                .IsSuccess.Should().BeTrue();
        using var released = database.CreateContext();
        (await Store(released, clock).ConfirmAsync(EmailChallengeOperation.SignIn, "user@example.com", "654321"))
            .IsSuccess.Should().BeTrue("the budget must reopen after the last failure window expires");
    }

    [Fact]
    public async Task Grant_ConditionalDeleteSpendsItOnceAcrossContexts()
    {
        using var database = new SqliteOrbitDbContextFactory();
        var userId = Guid.NewGuid();
        using (var issuer = database.CreateContext())
            await Store(issuer).AuthorizeOnceAsync(EmailChallengeOperation.ApiKeyManagement, userId, TimeSpan.FromMinutes(5));
        using (var reader = database.CreateContext())
        {
            var store = Store(reader);
            (await store.HasAuthorizationAsync(EmailChallengeOperation.ApiKeyManagement, userId)).Should().BeTrue();
            (await store.TryConsumeAuthorizationAsync(EmailChallengeOperation.ApiKeyManagement, userId)).Should().BeTrue();
        }
        using var next = database.CreateContext();
        (await Store(next).TryConsumeAuthorizationAsync(EmailChallengeOperation.ApiKeyManagement, userId)).Should().BeFalse();
    }

    private static Orbit.Infrastructure.Services.PostgresEmailChallengeStore Store(OrbitDbContext db, TimeProvider? clock = null) => new(
        db, new UnitOfWork(db, new DatabaseConnectionSettings()),
        Options.Create(new JwtSettings { SecretKey = "unit-test-challenge-secret-at-least-32-bytes", Issuer = "test", Audience = "test" }), clock ?? TimeProvider.System);
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _nowAtUtc = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _nowAtUtc;
        public void Advance(TimeSpan duration) => _nowAtUtc = _nowAtUtc.Add(duration);
    }
}
