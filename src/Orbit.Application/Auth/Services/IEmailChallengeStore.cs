using Orbit.Domain.Common;

namespace Orbit.Application.Auth.Services;

public interface IEmailChallengeStore
{
    Task<Result<string>> IssueAsync(EmailChallengeOperation operation, string email, string? fixedCode = null,
        TimeSpan? lifetime = null, CancellationToken cancellationToken = default);
    Task<Result<EmailChallengeConfirmation>> ConfirmAsync(EmailChallengeOperation operation, string email, string code,
        bool consume = true, CancellationToken cancellationToken = default);
    Task AuthorizeOnceAsync(EmailChallengeOperation operation, Guid userId, TimeSpan lifetime);
    Task<bool> TryConsumeAuthorizationAsync(EmailChallengeOperation operation, Guid userId);
    Task<bool> HasAuthorizationAsync(EmailChallengeOperation operation, Guid userId);
    Task<bool> TryThrottleAsync(string operation, string subject, TimeSpan lifetime, CancellationToken cancellationToken);
}
