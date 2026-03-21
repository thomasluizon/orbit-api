using Orbit.Application.Common.Attributes;

namespace Orbit.Infrastructure.Services.Prompts;

public record DiscoveredField(string Name, AiFieldAttribute Metadata);

public record DiscoveredAction(
    AiActionAttribute Metadata,
    IReadOnlyList<string> Rules,
    IReadOnlyList<AiExampleAttribute> Examples,
    IReadOnlyList<DiscoveredField> Fields)
{
    public bool RequiresHabitId => Fields.Any(f =>
        f.Metadata.Required && f.Name.Equals("habitId", StringComparison.OrdinalIgnoreCase));
}
