using MediatR;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Support.Commands;

public record SendSupportCommand(Guid UserId, string Name, string Email, string Subject, string Message)
    : IRequest<Result>;

public class SendSupportCommandHandler(
    IEmailService emailService) : IRequestHandler<SendSupportCommand, Result>
{
    public async Task<Result> Handle(SendSupportCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Subject))
            return Result.Failure("Subject is required");

        if (string.IsNullOrWhiteSpace(request.Message))
            return Result.Failure("Message is required");

        await emailService.SendSupportEmailAsync(
            request.Name,
            request.Email,
            request.Subject,
            request.Message,
            cancellationToken);

        return Result.Success();
    }
}
