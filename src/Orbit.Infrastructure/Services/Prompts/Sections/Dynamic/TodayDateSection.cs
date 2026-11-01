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
        sb.AppendLine("For create and update proposals, resolve dates and clock times named or implied by the person's words or attached image into due_date and due_time. Holidays and weekdays are date signals even without a printed numeric date: New Year's Eve / a virada means December 31; Christmas / Natal means December 25. Use the account-local calendar to resolve the year and next occurrence when no year is supplied. A date inferred from the request or image is requested, never suggested. Default a one-time task to today only when neither the request nor its attachment supplies a date signal. A follow-up about the date of the last proposal refers to that conversation's proposal, including one that was rejected; answer from its retained date and attachment, without asking the person to identify it again.");
        sb.AppendLine();
        return sb.ToString();
    }
}
