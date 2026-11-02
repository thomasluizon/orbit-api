using Orbit.Domain.Interfaces;

namespace Orbit.Application.Waitlist.Commands;

public sealed class SendWaitlistConfirmationJob(IEmailService email)
{
    public Task ExecuteAsync(string address, string confirmUrl, string language, CancellationToken cancellationToken) =>
        email.SendWaitlistConfirmationAsync(address, confirmUrl, language, cancellationToken);
}
