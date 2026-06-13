using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.UserFacts.Commands;

public record CreateUserFactCommand(Guid UserId, string FactText, string? Category) : IRequest<Result<Guid>>;

public class CreateUserFactCommandHandler(
    IGenericRepository<UserFact> userFactRepository,
    IAppConfigService appConfigService,
    IUnitOfWork unitOfWork) : IRequestHandler<CreateUserFactCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateUserFactCommand request, CancellationToken cancellationToken)
    {
        var maxFacts = await appConfigService.GetAsync(AppConfigKeys.MaxUserFacts, AppConstants.MaxUserFacts, cancellationToken);

        var existingFacts = await userFactRepository.FindAsync(
            f => f.UserId == request.UserId && !f.IsDeleted,
            cancellationToken);

        if (existingFacts.Count >= maxFacts)
            return Result.Failure<Guid>(ErrorMessages.UserFactsLimitReached.Format(maxFacts));

        var normalizedNew = request.FactText.Trim();
        var isDuplicate = existingFacts.Any(f =>
            string.Equals(f.FactText, normalizedNew, StringComparison.OrdinalIgnoreCase));

        if (isDuplicate)
            return Result.Failure<Guid>(ErrorMessages.DuplicateFact);

        var factResult = UserFact.Create(request.UserId, request.FactText, request.Category);

        if (factResult.IsFailure)
            return factResult.PropagateError<Guid>();

        await userFactRepository.AddAsync(factResult.Value, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(factResult.Value.Id);
    }
}
