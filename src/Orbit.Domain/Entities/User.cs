using System.Text.RegularExpressions;
using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public class User : Entity
{
    private static readonly Regex EmailRegex = new(
        @"^[^@\s]+@[^@\s]+\.[^@\s]+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string Name { get; private set; } = null!;
    public string Email { get; private set; } = null!;
    public string PasswordHash { get; private set; } = string.Empty;
    public string? TimeZone { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    private User() { }

    public static Result<User> Create(string name, string email, string password)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure<User>("Name is required");

        if (string.IsNullOrWhiteSpace(email))
            return Result.Failure<User>("Email is required");

        var trimmedEmail = email.Trim();
        if (!EmailRegex.IsMatch(trimmedEmail))
            return Result.Failure<User>("Invalid email format");

        if (string.IsNullOrWhiteSpace(password))
            return Result.Failure<User>("Password is required");

        var passwordValidation = ValidatePassword(password);
        if (passwordValidation.IsFailure)
            return Result.Failure<User>(passwordValidation.Error);

        return Result.Success(new User
        {
            Name = name.Trim(),
            Email = trimmedEmail.ToLowerInvariant(),
            CreatedAtUtc = DateTime.UtcNow
        });
    }

    private static Result ValidatePassword(string password)
    {
        if (password.Length < 8)
            return Result.Failure("Password must be at least 8 characters");

        if (!password.Any(char.IsUpper))
            return Result.Failure("Password must contain at least one uppercase letter");

        if (!password.Any(char.IsLower))
            return Result.Failure("Password must contain at least one lowercase letter");

        if (!password.Any(char.IsDigit))
            return Result.Failure("Password must contain at least one digit");

        return Result.Success();
    }

    public void SetPasswordHash(string passwordHash)
    {
        if (string.IsNullOrWhiteSpace(passwordHash))
            throw new ArgumentException("Password hash cannot be empty", nameof(passwordHash));

        PasswordHash = passwordHash;
    }

    public void UpdateProfile(string name, string email)
    {
        Name = name.Trim();
        Email = email.Trim().ToLowerInvariant();
    }

    public Result SetTimeZone(string ianaTimeZoneId)
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(ianaTimeZoneId);
            TimeZone = ianaTimeZoneId;
            return Result.Success();
        }
        catch (TimeZoneNotFoundException)
        {
            return Result.Failure($"Invalid timezone: {ianaTimeZoneId}");
        }
    }

    public void ClearTimeZone() => TimeZone = null;
}
