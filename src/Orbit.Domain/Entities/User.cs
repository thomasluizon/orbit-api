using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public class User : Entity
{
    public string Name { get; private set; } = null!;
    public string Email { get; private set; } = null!;
    public DateTime CreatedAtUtc { get; private set; }

    private User() { }

    public static Result<User> Create(string name, string email)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure<User>("Name is required.");

        if (string.IsNullOrWhiteSpace(email))
            return Result.Failure<User>("Email is required.");

        return Result.Success(new User
        {
            Name = name.Trim(),
            Email = email.Trim().ToLowerInvariant(),
            CreatedAtUtc = DateTime.UtcNow
        });
    }

    public void UpdateProfile(string name, string email)
    {
        Name = name.Trim();
        Email = email.Trim().ToLowerInvariant();
    }
}
