using MediatR;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.ChecklistTemplates.Commands;

public record DeleteChecklistTemplateCommand(
    Guid UserId,
    Guid TemplateId) : IRequest<Result>;

public class DeleteChecklistTemplateCommandHandler(
    IGenericRepository<ChecklistTemplate> repository,
    IUnitOfWork unitOfWork) : IRequestHandler<DeleteChecklistTemplateCommand, Result>
{
    public async Task<Result> Handle(DeleteChecklistTemplateCommand request, CancellationToken cancellationToken)
    {
        var template = await repository.FindOneTrackedAsync(
            t => t.Id == request.TemplateId && t.UserId == request.UserId,
            cancellationToken: cancellationToken);

        if (template is null)
            return Result.Failure("Template not found.");

        repository.Remove(template);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
