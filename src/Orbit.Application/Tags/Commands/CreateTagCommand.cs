using MediatR;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tags.Commands;

public record CreateTagCommand(
    Guid UserId,
    string Name,
    string Color) : IRequest<Result<Guid>>;

public class CreateTagCommandHandler(
    IGenericRepository<Tag> tagRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<CreateTagCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateTagCommand request, CancellationToken cancellationToken)
    {
        // Check for duplicate name
        var existing = await tagRepository.FindAsync(
            t => t.UserId == request.UserId && t.Name == request.Name.Trim(),
            cancellationToken);

        if (existing.Count > 0)
            return Result.Failure<Guid>("A tag with this name already exists.");

        var result = Tag.Create(request.UserId, request.Name, request.Color);
        if (result.IsFailure)
            return Result.Failure<Guid>(result.Error);

        await tagRepository.AddAsync(result.Value, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(result.Value.Id);
    }
}
