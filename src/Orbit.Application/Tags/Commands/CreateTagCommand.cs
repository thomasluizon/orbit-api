using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tags.Commands;

public record CreateTagCommand(
    Guid UserId,
    string Name,
    string Color) : IRequest<Result<Guid>>, IIdempotentCommand;

public class CreateTagCommandHandler(
    IGenericRepository<Tag> tagRepository,
    IUnitOfWork unitOfWork,
    IMemoryCache cache) : IRequestHandler<CreateTagCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateTagCommand request, CancellationToken cancellationToken)
    {
        var trimmedName = request.Name.Trim();
        var nameAlreadyExists = await tagRepository.AnyAsync(
            t => t.UserId == request.UserId && t.Name == trimmedName,
            cancellationToken);

        if (nameAlreadyExists)
            return Result.Failure<Guid>(ErrorMessages.DuplicateTagName);

        var result = Tag.Create(request.UserId, request.Name, request.Color);
        if (result.IsFailure)
            return result.PropagateError<Guid>();

        await tagRepository.AddAsync(result.Value, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        cache.Remove(ReferenceCacheKeys.Tags(request.UserId));

        return Result.Success(result.Value.Id);
    }
}
