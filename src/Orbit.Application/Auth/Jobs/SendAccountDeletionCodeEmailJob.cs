using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Jobs;

/// <summary>
/// Durable Hangfire job that dispatches an account-deletion confirmation-code email. Enqueued by
/// <see cref="Commands.RequestAccountDeletionCommandHandler"/> so the request path returns as soon as the
/// code is cached: the actual send runs out of band, survives a process restart, and is retried by Hangfire
/// on failure. Arguments are the ones Hangfire persists, so they stay primitive and serializable.
/// </summary>
public sealed class SendAccountDeletionCodeEmailJob(IEmailService emailService)
{
    public Task ExecuteAsync(string email, string code, string language) =>
        emailService.SendAccountDeletionCodeAsync(email, code, language);
}
