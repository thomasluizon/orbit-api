using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orbit.Application.Auth.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public sealed class IndependentEmailChallengeStore(IServiceScopeFactory scopes) : IEmailChallengeStore
{
    private async Task<T> RunAsync<T>(Func<PostgresEmailChallengeStore, Task<T>> action)
    {
        using var scope = scopes.CreateScope();
        var services = scope.ServiceProvider;
        var store = new PostgresEmailChallengeStore(services.GetRequiredService<OrbitDbContext>(),
            services.GetRequiredService<IUnitOfWork>(), services.GetRequiredService<IOptions<JwtSettings>>(),
            services.GetRequiredService<TimeProvider>());
        return await action(store);
    }

    public Task<Result<string>> IssueAsync(EmailChallengeOperation operation, string email, string? fixedCode = null,
        TimeSpan? lifetime = null, CancellationToken cancellationToken = default) =>
        RunAsync(store => store.IssueAsync(operation, email, fixedCode, lifetime, cancellationToken));
    public Task<Result<EmailChallengeConfirmation>> ConfirmAsync(EmailChallengeOperation operation, string email, string code,
        bool consume = true, CancellationToken cancellationToken = default) =>
        RunAsync(store => store.ConfirmAsync(operation, email, code, consume, cancellationToken));
    public async Task AuthorizeOnceAsync(EmailChallengeOperation operation, Guid userId, TimeSpan lifetime) =>
        await RunAsync(async store => { await store.AuthorizeOnceAsync(operation, userId, lifetime); return true; });
    public Task<bool> HasAuthorizationAsync(EmailChallengeOperation operation, Guid userId) =>
        RunAsync(store => store.HasAuthorizationAsync(operation, userId));
    public Task<bool> TryConsumeAuthorizationAsync(EmailChallengeOperation operation, Guid userId) =>
        RunAsync(store => store.TryConsumeAuthorizationAsync(operation, userId));
    public Task<bool> TryThrottleAsync(string operation, string subject, TimeSpan lifetime, CancellationToken cancellationToken) =>
        RunAsync(store => store.TryThrottleAsync(operation, subject, lifetime, cancellationToken));
}
