using MediatR;
using Orbit.Application.Common;
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

        return Result.Success(result.Value.Id);
    }
}
