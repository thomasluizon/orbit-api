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
    public string? TimeZone { get; private set; }
    public bool AiMemoryEnabled { get; private set; } = true;
    public bool AiSummaryEnabled { get; private set; } = true;
    public bool HasCompletedOnboarding { get; private set; } = false;
    public string? Language { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    private User() { }

    public static Result<User> Create(string name, string email)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure<User>("Name is required");

        if (string.IsNullOrWhiteSpace(email))
            return Result.Failure<User>("Email is required");

        var trimmedEmail = email.Trim();
        if (!EmailRegex.IsMatch(trimmedEmail))
            return Result.Failure<User>("Invalid email format");

        return Result.Success(new User
        {
            Name = name.Trim(),
            Email = trimmedEmail.ToLowerInvariant(),
            CreatedAtUtc = DateTime.UtcNow
        });
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

    public void SetAiMemory(bool enabled) => AiMemoryEnabled = enabled;

    public void SetAiSummary(bool enabled) => AiSummaryEnabled = enabled;

    public void SetLanguage(string? language) => Language = language;

    public void CompleteOnboarding() => HasCompletedOnboarding = true;
}
