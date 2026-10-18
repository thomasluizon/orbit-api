using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Jobs;

/// <summary>
/// Retries welcome-email delivery independently of the account's other signup side effects.
/// </summary>
public sealed class SendWelcomeEmailJob(IGenericRepository<User> userRepository, IEmailService emailService)
{
    public async Task ExecuteAsync(Guid userId, string language)
    {
        var user = await userRepository.GetByIdAsync(userId, CancellationToken.None);
        if (user is not null)
            await emailService.SendWelcomeEmailAsync(user.Email, user.Name, language, CancellationToken.None);
    }
}
