using System.Text;

namespace Orbit.Infrastructure.Services.Prompts.Sections.Dynamic;

public class TodayDateSection : IPromptSection
{
    public int Order => 650;
    public bool ShouldInclude(PromptContext context) => true;

    public string Build(PromptContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"## Today's Date: {(context.UserToday ?? DateOnly.FromDateTime(DateTime.UtcNow)):yyyy-MM-dd}");
        sb.AppendLine();
        return sb.ToString();
    }
}
