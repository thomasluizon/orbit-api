namespace Orbit.Domain.Interfaces;

public interface IEmailService
{
    Task SendWelcomeEmailAsync(string toEmail, string userName, string language = "en", CancellationToken cancellationToken = default);
}
