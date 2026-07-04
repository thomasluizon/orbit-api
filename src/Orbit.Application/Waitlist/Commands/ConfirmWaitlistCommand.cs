using MediatR;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Waitlist.Commands;

public record ConfirmWaitlistCommand(string Token) : IRequest<Result>;

public class ConfirmWaitlistCommandHandler(
    IWaitlistConfirmationTokenService tokenService,
    IMarketingContactsService contactsService) : IRequestHandler<ConfirmWaitlistCommand, Result>
{
    public async Task<Result> Handle(ConfirmWaitlistCommand request, CancellationToken cancellationToken)
    {
        if (!tokenService.TryValidateToken(request.Token, out var email, out _))
            return Result.Failure("Invalid or expired confirmation link.");

        await contactsService.AddContactAsync(email, cancellationToken);

        return Result.Success();
    }
}
