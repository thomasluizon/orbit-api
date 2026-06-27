using MediatR;
using Orbit.Application.Common;
using Orbit.Application.Social.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Social.Commands;

public record ReportUserCommand(
    Guid UserId,
    Guid ReportedUserId,
    ReportReason Reason,
    string? Details,
    Guid? CheerId) : IRequest<Result<Guid>>;

public class ReportUserCommandHandler(
    SocialAccessGuard socialAccessGuard,
    IGenericRepository<User> userRepository,
    IGenericRepository<Cheer> cheerRepository,
    IGenericRepository<Report> reportRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<ReportUserCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(ReportUserCommand request, CancellationToken cancellationToken)
    {
        var access = await socialAccessGuard.EnsureEnabledAsync(request.UserId, cancellationToken);
        if (access.IsFailure)
            return access.PropagateError<Guid>();

        var targetExists = await userRepository.AnyAsync(u => u.Id == request.ReportedUserId, cancellationToken);
        if (!targetExists)
            return Result.Failure<Guid>(ErrorMessages.UserNotFound);

        if (request.CheerId.HasValue)
        {
            var cheer = await cheerRepository.FindOneTrackedAsync(
                c => c.Id == request.CheerId.Value, cancellationToken: cancellationToken);
            if (cheer is null || (cheer.SenderId != request.ReportedUserId && cheer.RecipientId != request.ReportedUserId))
                return Result.Failure<Guid>(ErrorMessages.CheerNotFound);
        }

        var createResult = Report.Create(
            request.UserId,
            request.ReportedUserId,
            request.Reason,
            request.Details,
            request.CheerId);
        if (createResult.IsFailure)
            return createResult.PropagateError<Guid>();

        await reportRepository.AddAsync(createResult.Value, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(createResult.Value.Id);
    }
}
