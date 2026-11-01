using System.Globalization;
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
        sb.AppendLine(CultureInfo.InvariantCulture, $"## Today's Date: {today:yyyy-MM-dd}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Account-local calendar dates. Yesterday: {today.AddDays(-1):yyyy-MM-dd}. Tomorrow: {today.AddDays(1):yyyy-MM-dd}.");
        sb.AppendLine("Earlier messages and pending previews do not define today's date. Preserve explicit calendar dates.");
        sb.AppendLine();
        return sb.ToString();
    }
}
