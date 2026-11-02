using MediatR;
using Orbit.Application.Auth.Services;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Waitlist.Commands;

public record JoinWaitlistCommand(string Email, string Language = "en") : IRequest<Result>;

public class JoinWaitlistCommandHandler(
    IEmailChallengeStore challenges,
    IWaitlistConfirmationTokenService tokenService,
    IEmailService emailService,
    IOptions<WaitlistSettings> waitlistOptions) : IRequestHandler<JoinWaitlistCommand, Result>
{
    private readonly WaitlistSettings _settings = waitlistOptions.Value;

    public async Task<Result> Handle(JoinWaitlistCommand request, CancellationToken cancellationToken)
    {
        if (!WaitlistLanguage.TryCanonicalize(request.Language, out var language))
            return Result.Failure("Unsupported waitlist language.");

        var email = request.Email.Trim().ToLowerInvariant();
        if (!await challenges.TryThrottleAsync("waitlist", email, TimeSpan.FromSeconds(60), cancellationToken))
            return Result.Success();

        var token = tokenService.CreateToken(email, language);
        var confirmUrl = $"{_settings.ApiBaseUrl.TrimEnd('/')}/api/waitlist/confirm?token={Uri.EscapeDataString(token)}";

        await emailService.SendWaitlistConfirmationAsync(email, confirmUrl, language, cancellationToken);

        return Result.Success();
    }
}
