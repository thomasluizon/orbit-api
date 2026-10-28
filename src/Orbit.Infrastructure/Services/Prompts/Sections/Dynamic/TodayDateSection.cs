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
        sb.AppendLine("Use this turn's account-local calendar for today, yesterday and tomorrow, including hoje, ontem and amanhã. Earlier messages and pending previews do not define today's date. Preserve explicit calendar dates; calculate preparation offsets from the named event date. A correction preserves existing dates unless the person requests changing them.");
        sb.AppendLine();
        return sb.ToString();
    }
}
