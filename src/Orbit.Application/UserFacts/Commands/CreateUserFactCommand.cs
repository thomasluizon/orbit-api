using MediatR;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.UserFacts.Commands;

public record CreateUserFactCommand(Guid UserId, string FactText, string? Category) : IRequest<Result<Guid>>;

public class CreateUserFactCommandHandler(
    IGenericRepository<UserFact> userFactRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<CreateUserFactCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateUserFactCommand request, CancellationToken cancellationToken)
    {
        // Check for duplicate/similar facts
        var existingFacts = await userFactRepository.FindAsync(
            f => f.UserId == request.UserId && !f.IsDeleted,
            cancellationToken);

        var normalizedNew = request.FactText.Trim().ToLowerInvariant();
        var isDuplicate = existingFacts.Any(f =>
            f.FactText.ToLowerInvariant() == normalizedNew);

        if (isDuplicate)
            return Result.Failure<Guid>("A similar fact already exists.");

        var factResult = UserFact.Create(request.UserId, request.FactText, request.Category);

        if (factResult.IsFailure)
            return Result.Failure<Guid>(factResult.Error);

        await userFactRepository.AddAsync(factResult.Value, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(factResult.Value.Id);
    }
}
