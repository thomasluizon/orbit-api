namespace Orbit.Infrastructure.AI;

/// <summary>
/// Selects which configured model a completion runs on. <see cref="Primary"/> is the agentic
/// chat brain; <see cref="SubTask"/> is the cheaper sub-task model for mechanical, no-tool jobs.
/// </summary>
public enum AiModelTier
{
    Primary,
    SubTask
}
