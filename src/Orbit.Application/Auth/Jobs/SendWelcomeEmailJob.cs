using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Jobs;

/// <summary>
/// Retries welcome-email delivery independently of the account's other signup side effects.
/// </summary>
public sealed class SendWelcomeEmailJob(IGenericRepository<User> userRepository, IEmailService emailService,
    IUnitOfWork unitOfWork)
{
    public Task ExecuteAsync(Guid userId, string language) =>
        SignupWork.RunAsync(userId, userRepository, unitOfWork, async user =>
        {
            if (user.SignupWelcomeCompletedAtUtc is not null)
                return;

            await emailService.SendWelcomeEmailAsync(user.Email, user.Name,
                user.SignupLanguage ?? language, CancellationToken.None);
            if (user.SignupLanguage is not null)
                user.CompleteSignupWelcome();
        });
}
