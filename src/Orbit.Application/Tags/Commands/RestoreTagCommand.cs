using MediatR;
using Orbit.Application.Behaviors;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tags.Commands;

public record RestoreTagCommand(
    Guid UserId,
    Guid TagId) : IRequest<Result>, IConcurrencyRetryable;

public class RestoreTagCommandHandler(
    IGenericRepository<Tag> tagRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<RestoreTagCommand, Result>
{
    public async Task<Result> Handle(RestoreTagCommand request, CancellationToken cancellationToken)
    {
        var tags = await tagRepository.FindTrackedIgnoringFiltersAsync(
            t => t.Id == request.TagId && t.UserId == request.UserId,
            cancellationToken);

        var tag = tags.FirstOrDefault();
        if (tag is null || !tag.IsDeleted)
            return Result.Failure(ErrorMessages.TagNotFound);

        tag.Restore();
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
