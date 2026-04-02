using MediatR;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.ChecklistTemplates.Commands;

public record CreateChecklistTemplateCommand(
    Guid UserId,
    string Name,
    IReadOnlyList<string> Items) : IRequest<Result<Guid>>;

public class CreateChecklistTemplateCommandHandler(
    IGenericRepository<ChecklistTemplate> repository,
    IUnitOfWork unitOfWork) : IRequestHandler<CreateChecklistTemplateCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateChecklistTemplateCommand request, CancellationToken cancellationToken)
    {
        var result = ChecklistTemplate.Create(request.UserId, request.Name, request.Items);
        if (result.IsFailure)
            return Result.Failure<Guid>(result.Error);

        await repository.AddAsync(result.Value, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(result.Value.Id);
    }
}
