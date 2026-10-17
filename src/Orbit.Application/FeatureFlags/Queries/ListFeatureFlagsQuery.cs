using MediatR;
using Orbit.Domain.Common;

namespace Orbit.Application.FeatureFlags.Queries;

public record ListFeatureFlagsQuery(Guid AdminId) : IRequest<Result<IReadOnlyList<AdminFeatureFlag>>>;

public class ListFeatureFlagsQueryHandler(IFeatureFlagManagementService service)
    : IRequestHandler<ListFeatureFlagsQuery, Result<IReadOnlyList<AdminFeatureFlag>>>
{
    public Task<Result<IReadOnlyList<AdminFeatureFlag>>> Handle(ListFeatureFlagsQuery request, CancellationToken cancellationToken) =>
        service.ListAsync(request.AdminId, cancellationToken);
}
