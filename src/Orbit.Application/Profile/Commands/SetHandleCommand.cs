using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Profile.Commands;

public record SetHandleCommand(Guid UserId, string Handle) : IRequest<Result>;

public class SetHandleCommandHandler(
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<SetHandleCommand, Result>
{
    public async Task<Result> Handle(SetHandleCommand request, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindOneTrackedAsync(
            u => u.Id == request.UserId,
            cancellationToken: cancellationToken);

        if (user is null)
            return Result.Failure(ErrorMessages.UserNotFound);

        var normalized = request.Handle.Trim();
        var lowered = normalized.ToLowerInvariant();

        var taken = await userRepository.AnyAsync(
            u => u.Id != request.UserId && u.Handle != null && u.Handle.ToLower() == lowered,
            cancellationToken);
        if (taken)
            return Result.Failure(ErrorMessages.HandleTaken);

        var setResult = user.SetHandle(normalized);
        if (setResult.IsFailure)
            return setResult;

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (DbUniqueViolation.IsUniqueViolation(exception))
        {
            return Result.Failure(ErrorMessages.HandleTaken);
        }

        return Result.Success();
    }
}
