using System.Text;

namespace Orbit.Infrastructure.Services.Prompts.Sections.Dynamic;

public class HabitCountSection : IPromptSection
{
    public int Order => 800;
    public bool ShouldInclude(PromptContext context) => true;

    public string Build(PromptContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Habit Count: {context.ActiveHabits.Count} active habits");
        sb.AppendLine();
        return sb.ToString();
    }
}
