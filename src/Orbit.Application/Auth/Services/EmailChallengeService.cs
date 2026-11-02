using Orbit.Domain.Common;

namespace Orbit.Application.Auth.Services;

public enum EmailChallengeOperation
{
    AccountDeletion,
    ApiKeyManagement,
    SignIn,
}

public readonly record struct EmailChallengeConfirmation(TimeSpan RemainingLifetime);

public sealed class EmailChallengeService(IEmailChallengeStore store)
{
    public Task<Result<string>> IssueAsync(EmailChallengeOperation operation, string email,
        CancellationToken cancellationToken = default) => store.IssueAsync(operation, email, cancellationToken: cancellationToken);

    public Task<Result<EmailChallengeConfirmation>> ConfirmAsync(EmailChallengeOperation operation, string email,
        string code, CancellationToken cancellationToken = default) =>
        store.ConfirmAsync(operation, email, code, cancellationToken: cancellationToken);

    public async Task<Result> CheckConfirmationAsync(EmailChallengeOperation operation, string email, string code,
        CancellationToken cancellationToken = default)
    {
        var result = await store.ConfirmAsync(operation, email, code, consume: false, cancellationToken);
        return result.IsSuccess ? Result.Success() : Result.Failure(result.Error, result.ErrorCode);
    }

    public Task AuthorizeOnceAsync(EmailChallengeOperation operation, Guid userId, TimeSpan lifetime) =>
        store.AuthorizeOnceAsync(operation, userId, lifetime);
    public Task<bool> TryConsumeAuthorizationAsync(EmailChallengeOperation operation, Guid userId) =>
        store.TryConsumeAuthorizationAsync(operation, userId);
    public Task<bool> HasAuthorizationAsync(EmailChallengeOperation operation, Guid userId) =>
        store.HasAuthorizationAsync(operation, userId);
}
