namespace Orbit.Application.Chat.Models;

public sealed record ChatTurnTiming(double TotalMilliseconds, IReadOnlyList<ChatPhaseTiming> Phases);

public sealed record ChatPhaseTiming(string Name, double Milliseconds, double ElapsedMilliseconds);
