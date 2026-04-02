using MediatR;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.ChecklistTemplates.Queries;

public record ChecklistTemplateResponse(
    Guid Id,
    string Name,
    IReadOnlyList<string> Items);

public record GetChecklistTemplatesQuery(Guid UserId) : IRequest<Result<IReadOnlyList<ChecklistTemplateResponse>>>;

public class GetChecklistTemplatesQueryHandler(
    IGenericRepository<ChecklistTemplate> repository) : IRequestHandler<GetChecklistTemplatesQuery, Result<IReadOnlyList<ChecklistTemplateResponse>>>
{
    public async Task<Result<IReadOnlyList<ChecklistTemplateResponse>>> Handle(GetChecklistTemplatesQuery request, CancellationToken cancellationToken)
    {
        var templates = await repository.FindAsync(
            t => t.UserId == request.UserId,
            cancellationToken);

        var result = templates
            .OrderBy(t => t.CreatedAtUtc)
            .Select(t => new ChecklistTemplateResponse(t.Id, t.Name, t.Items))
            .ToList();

        return Result.Success<IReadOnlyList<ChecklistTemplateResponse>>(result);
    }
}
