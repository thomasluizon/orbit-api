using MediatR;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Uploads.Queries;

public record UploadOwnerExistsQuery(Guid UserId) : IRequest<bool>;

public sealed class UploadOwnerExistsQueryHandler(IGenericRepository<User> users)
    : IRequestHandler<UploadOwnerExistsQuery, bool>
{
    public Task<bool> Handle(UploadOwnerExistsQuery request, CancellationToken cancellationToken) =>
        users.AnyIgnoringFiltersAsync(user => user.Id == request.UserId, cancellationToken);
}
