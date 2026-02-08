using MediatR;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Profile.Queries;

public record ProfileResponse(string Name, string Email, string? TimeZone);

public record GetProfileQuery(Guid UserId) : IRequest<ProfileResponse>;

public class GetProfileQueryHandler(
    IGenericRepository<User> userRepository) : IRequestHandler<GetProfileQuery, ProfileResponse>
{
    public async Task<ProfileResponse> Handle(GetProfileQuery request, CancellationToken cancellationToken)
    {
        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);

        if (user is null)
            throw new InvalidOperationException("User not found.");

        return new ProfileResponse(user.Name, user.Email, user.TimeZone);
    }
}
