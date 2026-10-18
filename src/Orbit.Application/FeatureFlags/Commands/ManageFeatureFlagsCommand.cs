using MediatR;
using Orbit.Domain.Common;

namespace Orbit.Application.FeatureFlags.Commands;

public record ManageFeatureFlagsCommand(
    Guid AdminId, string Action, string Key, IReadOnlyList<string>? Emails = null, string? Description = null)
    : IRequest<Result<AdminFeatureFlag>>;

public class ManageFeatureFlagsCommandHandler(IFeatureFlagManagementService service)
    : IRequestHandler<ManageFeatureFlagsCommand, Result<AdminFeatureFlag>>
{
    public Task<Result<AdminFeatureFlag>> Handle(ManageFeatureFlagsCommand request, CancellationToken cancellationToken) =>
        service.ManageAsync(request, cancellationToken);
}
