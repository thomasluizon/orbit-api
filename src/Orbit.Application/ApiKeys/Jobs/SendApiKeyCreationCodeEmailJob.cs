using Orbit.Domain.Interfaces;

namespace Orbit.Application.ApiKeys.Jobs;

public sealed class SendApiKeyCreationCodeEmailJob(IEmailService emailService)
{
    public Task ExecuteAsync(string email, string code, string language) =>
        emailService.SendApiKeyCreationCodeAsync(email, code, language);
}
