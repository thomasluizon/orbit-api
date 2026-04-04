namespace Orbit.Domain.Models;

public record UserStreakState(int CurrentStreak, int LongestStreak, DateOnly? LastActiveDate);
