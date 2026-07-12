using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Jobs;

/// <summary>
/// Durable Hangfire job that dispatches an account verification-code email. Enqueued by
/// <see cref="Commands.SendCodeCommandHandler"/> so the request path returns as soon as the code is
/// cached: the actual send runs out of band, survives a process restart, and is retried by Hangfire
/// on failure. Arguments are the ones Hangfire persists, so they stay primitive and serializable.
/// </summary>
public sealed class SendVerificationCodeEmailJob(IEmailService emailService)
{
    public Task ExecuteAsync(string email, string code, string language) =>
        emailService.SendVerificationCodeAsync(email, code, language);
}
