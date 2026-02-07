using MediatR;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Commands;

public record RegisterCommand(string Name, string Email, string Password)
    : IRequest<Result<Guid>>;

public class RegisterCommandHandler(
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork,
    IPasswordHasher passwordHasher) : IRequestHandler<RegisterCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(RegisterCommand request, CancellationToken cancellationToken)
    {
        // Check if email already exists
        var existingUsers = await userRepository.GetAllAsync(cancellationToken);
        if (existingUsers.Any(u => u.Email.Equals(request.Email, StringComparison.OrdinalIgnoreCase)))
            return Result.Failure<Guid>("Email already registered");

        // Validate and create user (domain validation)
        var userResult = User.Create(request.Name, request.Email, request.Password);
        if (userResult.IsFailure)
            return Result.Failure<Guid>(userResult.Error);

        var user = userResult.Value;

        // Hash password (infrastructure concern)
        var hashedPassword = passwordHasher.HashPassword(request.Password);
        user.SetPasswordHash(hashedPassword);

        await userRepository.AddAsync(user, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(user.Id);
    }
}
