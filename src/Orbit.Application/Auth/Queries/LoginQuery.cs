using MediatR;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Queries;

public record LoginQuery(string Email, string Password)
    : IRequest<Result<LoginResponse>>;

public record LoginResponse(Guid UserId, string Token, string Name, string Email);

public class LoginQueryHandler(
    IGenericRepository<User> userRepository,
    IPasswordHasher passwordHasher,
    ITokenService tokenService) : IRequestHandler<LoginQuery, Result<LoginResponse>>
{
    public async Task<Result<LoginResponse>> Handle(LoginQuery request, CancellationToken cancellationToken)
    {
        // Find user by email (case-insensitive)
        var users = await userRepository.GetAllAsync(cancellationToken);
        var user = users.FirstOrDefault(u =>
            u.Email.Equals(request.Email, StringComparison.OrdinalIgnoreCase));

        if (user == null)
            return Result.Failure<LoginResponse>("Invalid email or password");

        // Verify password
        if (!passwordHasher.VerifyPassword(request.Password, user.PasswordHash))
            return Result.Failure<LoginResponse>("Invalid email or password");

        // Generate JWT token
        var token = tokenService.GenerateToken(user.Id, user.Email);

        return Result.Success(new LoginResponse(user.Id, token, user.Name, user.Email));
    }
}
