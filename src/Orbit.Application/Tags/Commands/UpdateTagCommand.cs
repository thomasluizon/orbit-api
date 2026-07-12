using MediatR;
using Microsoft.Extensions.Caching.Memory;
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
    IUnitOfWork unitOfWork,
    IMemoryCache cache) : IRequestHandler<UpdateTagCommand, Result>
{
    public async Task<Result> Handle(UpdateTagCommand request, CancellationToken cancellationToken)
    {
        var tag = await tagRepository.FindOneTrackedAsync(
            t => t.Id == request.TagId && t.UserId == request.UserId,
            cancellationToken: cancellationToken);

        if (tag is null)
            return Result.Failure(ErrorMessages.TagNotFound);

        var existing = await tagRepository.FindAsync(
            t => t.UserId == request.UserId && t.Name == request.Name.Trim() && t.Id != request.TagId,
            cancellationToken);

        if (existing.Count > 0)
            return Result.Failure(ErrorMessages.DuplicateTagName);

        var result = tag.Update(request.Name, request.Color);
        if (result.IsFailure)
            return result;

        await unitOfWork.SaveChangesAsync(cancellationToken);

        cache.Remove(ReferenceCacheKeys.Tags(request.UserId));

        return Result.Success();
    }
}
