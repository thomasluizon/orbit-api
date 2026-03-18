using MediatR;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Profile.Commands;

public record SetLanguageCommand(Guid UserId, string Language) : IRequest<Result>;

public class SetLanguageCommandHandler(
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<SetLanguageCommand, Result>
{
    public async Task<Result> Handle(SetLanguageCommand request, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindOneTrackedAsync(
            u => u.Id == request.UserId,
            cancellationToken: cancellationToken);

        if (user is null)
            return Result.Failure("User not found.");

        user.SetLanguage(request.Language);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
