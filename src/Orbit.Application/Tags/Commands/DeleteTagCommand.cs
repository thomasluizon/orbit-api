using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tags.Commands;

public record DeleteTagCommand(
    Guid UserId,
    Guid TagId) : IRequest<Result>;

public class DeleteTagCommandHandler(
    IGenericRepository<Tag> tagRepository,
    IUnitOfWork unitOfWork,
    IMemoryCache cache) : IRequestHandler<DeleteTagCommand, Result>
{
    public async Task<Result> Handle(DeleteTagCommand request, CancellationToken cancellationToken)
    {
        var tag = await tagRepository.FindOneTrackedAsync(
            t => t.Id == request.TagId && t.UserId == request.UserId,
            cancellationToken: cancellationToken);

        if (tag is null)
            return Result.Failure(ErrorMessages.TagNotFound);

        tag.SoftDelete();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        cache.Remove(ReferenceCacheKeys.Tags(request.UserId));

        return Result.Success();
    }
}
