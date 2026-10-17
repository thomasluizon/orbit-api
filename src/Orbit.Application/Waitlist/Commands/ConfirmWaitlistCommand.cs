using MediatR;
using Orbit.Application.Common;
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
        if (!tokenService.TryValidateToken(request.Token, out var email, out var language))
            return Result.Failure(ErrorMessages.InvalidWaitlistConfirmation);

        if (!WaitlistLanguage.TryCanonicalize(language, out var canonicalLanguage))
            return Result.Failure(ErrorMessages.InvalidWaitlistConfirmation);

        await contactsService.AddContactAsync(email, canonicalLanguage, cancellationToken);

        return Result.Success();
    }
}
