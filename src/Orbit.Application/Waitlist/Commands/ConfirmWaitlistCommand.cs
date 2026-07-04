using MediatR;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Waitlist.Commands;

public record ConfirmWaitlistCommand(string Token) : IRequest<Result>;

public class ConfirmWaitlistCommandHandler(
    IWaitlistConfirmationTokenService tokenService,
    IMarketingAudienceService audienceService) : IRequestHandler<ConfirmWaitlistCommand, Result>
{
    public async Task<Result> Handle(ConfirmWaitlistCommand request, CancellationToken cancellationToken)
    {
        if (!tokenService.TryValidateToken(request.Token, out var email, out _))
            return Result.Failure("Invalid or expired confirmation link.");

        await audienceService.AddContactAsync(email, cancellationToken);

        return Result.Success();
    }
}
