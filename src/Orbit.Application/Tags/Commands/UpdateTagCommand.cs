using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tags.Commands;

public record UpdateTagCommand(
    Guid UserId,
    Guid TagId,
    string Name,
    string Color) : IRequest<Result>;

public class UpdateTagCommandHandler(
    IGenericRepository<Tag> tagRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<UpdateTagCommand, Result>
{
    public async Task<Result> Handle(UpdateTagCommand request, CancellationToken cancellationToken)
    {
        var tag = await tagRepository.FindOneTrackedAsync(
            t => t.Id == request.TagId && t.UserId == request.UserId,
            cancellationToken: cancellationToken);

        if (tag is null)
            return Result.Failure(ErrorMessages.TagNotFound, ErrorCodes.TagNotFound);

        // Check for duplicate name (excluding self)
        var existing = await tagRepository.FindAsync(
            t => t.UserId == request.UserId && t.Name == request.Name.Trim() && t.Id != request.TagId,
            cancellationToken);

        if (existing.Count > 0)
            return Result.Failure("A tag with this name already exists.");

        var result = tag.Update(request.Name, request.Color);
        if (result.IsFailure)
            return result;

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
