using System.Text;

namespace Orbit.Infrastructure.Services.Prompts.Sections.Dynamic;

public class TodayDateSection : IPromptSection
{
    public int Order => 650;
    public bool ShouldInclude(PromptContext context) => true;

    public string Build(PromptContext context)
    {
        var today = context.UserToday ?? throw new InvalidOperationException(
            "PromptContext.UserToday must be set before building the today-date section.");
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"## Today's Date: {today:yyyy-MM-dd}");
        sb.AppendLine();
        return sb.ToString();
    }
}
