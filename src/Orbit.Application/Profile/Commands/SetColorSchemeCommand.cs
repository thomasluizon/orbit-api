using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Profile.Commands;

public record SetColorSchemeCommand(Guid UserId, string? ColorScheme) : IRequest<Result>;

public class SetColorSchemeCommandHandler(
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<SetColorSchemeCommand, Result>
{
    public async Task<Result> Handle(SetColorSchemeCommand request, CancellationToken cancellationToken)
    {
        var result = await ConcurrencyRetry.ExecuteAsync(
            userRepository,
            unitOfWork,
            ct => userRepository.FindOneTrackedAsync(u => u.Id == request.UserId, cancellationToken: ct),
            user => Task.FromResult(user.SetColorScheme(request.ColorScheme)),
            ErrorMessages.UserNotFound,
            cancellationToken);

        return result.IsSuccess ? Result.Success() : result.PropagateError();
    }
}
