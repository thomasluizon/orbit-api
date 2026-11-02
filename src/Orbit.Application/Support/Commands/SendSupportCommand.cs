using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Support.Commands;

public record SendSupportCommand(Guid UserId, string Name, string Email, string Subject, string Message)
    : IRequest<Result>;

public class SendSupportCommandHandler(
    IEmailService emailService, IGenericRepository<User> users) : IRequestHandler<SendSupportCommand, Result>
{
    public async Task<Result> Handle(SendSupportCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Subject))
            return Result.Failure(ErrorMessages.SubjectRequired);

        if (string.IsNullOrWhiteSpace(request.Message))
            return Result.Failure(ErrorMessages.MessageRequired);

        var user = await users.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure(ErrorMessages.UserNotFound);

        await emailService.SendSupportEmailAsync(
            user.Name,
            user.Email,
            request.Subject,
            $"User id: {request.UserId}\n\n{request.Message}",
            cancellationToken);

        return Result.Success();
    }
}
