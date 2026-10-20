using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.ChecklistTemplates.Commands;

public record UpdateChecklistTemplateCommand(
    Guid UserId,
    Guid TemplateId,
    string? Name = null,
    IReadOnlyList<string>? Items = null) : IRequest<Result>;

public class UpdateChecklistTemplateCommandHandler(
    IGenericRepository<ChecklistTemplate> repository,
    IUnitOfWork unitOfWork,
    IMemoryCache cache) : IRequestHandler<UpdateChecklistTemplateCommand, Result>
{
    public async Task<Result> Handle(UpdateChecklistTemplateCommand request, CancellationToken cancellationToken)
    {
        if (request.Name is null && request.Items is null)
            return Result.Failure("Provide a template name, items, or both.");

        var template = await repository.FindOneTrackedAsync(
            t => t.Id == request.TemplateId && t.UserId == request.UserId,
            cancellationToken: cancellationToken);
        if (template is null)
            return Result.Failure(ErrorMessages.TemplateNotFound);

        var result = template.Update(request.Name ?? template.Name, request.Items ?? template.Items);
        if (result.IsFailure)
            return result;

        await unitOfWork.SaveChangesAsync(cancellationToken);
        cache.Remove(ReferenceCacheKeys.ChecklistTemplates(request.UserId));
        return Result.Success();
    }
}
