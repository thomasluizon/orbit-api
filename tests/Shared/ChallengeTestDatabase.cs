using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Auth.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Tests.Shared;

internal sealed class ChallengeTestDatabase
{
    private const string Secret = "unit-test-challenge-secret-at-least-32-bytes";
    private static readonly ConditionalWeakTable<IMemoryCache, ChallengeTestDatabase> Databases = new();
    private readonly DbContextOptions<OrbitDbContext> _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    internal static ChallengeTestDatabase For(IMemoryCache cache) => Databases.GetValue(cache, _ => new());

    internal ChallengeTestDatabase()
    {
        _options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString(), new InMemoryDatabaseRoot()).Options;
    }

    internal IEmailChallengeStore Store(TimeProvider? clock = null) => new PerCallStore(this, clock ?? TimeProvider.System);

    internal static IHostEnvironment Environment(string name = "Development")
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(name);
        return environment;
    }

    internal void Seed(string key, string code, DateTime? issuedAtUtc = null, bool expired = false)
    {
        var (operation, subject) = Parse(key);
        using var context = new OrbitDbContext(_options);
        var row = context.EmailChallenges.SingleOrDefault(row => row.Operation == operation && row.Subject == subject);
        var nowAtUtc = issuedAtUtc ?? DateTime.UtcNow;
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes($"email-challenge:{operation}:{subject}:{code}"));
        var lifetime = TimeSpan.FromMinutes(expired ? 0.001 : 5);
        var issueAtUtc = expired ? nowAtUtc.AddMinutes(-10) : nowAtUtc;
        if (row is null)
            context.EmailChallenges.Add(EmailChallenge.Create(operation, subject, hash, issueAtUtc, lifetime));
        else
            row.Issue(hash, issueAtUtc, lifetime);
        context.SaveChanges();
    }

    internal EmailChallenge? Row(string key)
    {
        var (operation, subject) = Parse(key);
        using var context = new OrbitDbContext(_options);
        return context.EmailChallenges.AsNoTracking().SingleOrDefault(row => row.Operation == operation && row.Subject == subject);
    }

    internal int Attempts(string key) => Row(key)?.Attempts ?? 0;
    internal bool HasCode(string key) => Row(key) is { Consumed: false };
    internal void SetAttempts(string key, int attempts)
    {
        using var context = new OrbitDbContext(_options);
        var (operation, subject) = Parse(key);
        var row = context.EmailChallenges.Single(row => row.Operation == operation && row.Subject == subject);
        for (var i = 0; i < attempts; i++) row.RecordFailedAttempt(row.IssuedAtUtc);
        context.SaveChanges();
    }
    internal void Clear()
    {
        using var context = new OrbitDbContext(_options);
        context.EmailChallenges.RemoveRange(context.EmailChallenges);
        context.SaveChanges();
    }

    private static (string Operation, string Subject) Parse(string key)
    {
        var parts = key.Split(':', 2);
        return (parts[0] switch
        {
            "verify" or "verify-attempts" => "SignIn",
            "delete" or "delete-attempts" => "AccountDeletion",
            "api-key-create" or "api-key-create-attempts" => "ApiKeyManagement",
            _ => parts[0],
        }, parts[1]);
    }

    private sealed class PerCallStore(ChallengeTestDatabase database, TimeProvider clock) : IEmailChallengeStore
    {
        private async Task<T> RunAsync<T>(Func<PostgresEmailChallengeStore, Task<T>> operation)
        {
            await database._gate.WaitAsync();
            try
            {
                using var context = new OrbitDbContext(database._options);
                using var unitOfWork = new UnitOfWork(context, new DatabaseConnectionSettings());
                return await operation(new PostgresEmailChallengeStore(context, unitOfWork,
                    Options.Create(new JwtSettings { SecretKey = Secret, Issuer = "test", Audience = "test" }), clock));
            }
            finally { database._gate.Release(); }
        }
        public Task<Orbit.Domain.Common.Result<string>> IssueAsync(EmailChallengeOperation operation, string email, string? fixedCode = null,
            TimeSpan? lifetime = null, CancellationToken cancellationToken = default) =>
            RunAsync(store => store.IssueAsync(operation, email, fixedCode, lifetime, cancellationToken));
        public Task<Orbit.Domain.Common.Result<EmailChallengeConfirmation>> ConfirmAsync(EmailChallengeOperation operation, string email,
            string code, bool consume = true, CancellationToken cancellationToken = default) =>
            RunAsync(store => store.ConfirmAsync(operation, email, code, consume, cancellationToken));
        public async Task AuthorizeOnceAsync(EmailChallengeOperation operation, Guid userId, TimeSpan lifetime) =>
            await RunAsync(async store => { await store.AuthorizeOnceAsync(operation, userId, lifetime); return true; });
        public Task<bool> HasAuthorizationAsync(EmailChallengeOperation operation, Guid userId) => RunAsync(store => store.HasAuthorizationAsync(operation, userId));
        public Task<bool> TryConsumeAuthorizationAsync(EmailChallengeOperation operation, Guid userId) => RunAsync(store => store.TryConsumeAuthorizationAsync(operation, userId));
        public Task<bool> TryThrottleAsync(string operation, string subject, TimeSpan lifetime, CancellationToken cancellationToken) =>
            RunAsync(store => store.TryThrottleAsync(operation, subject, lifetime, cancellationToken));
    }
}
