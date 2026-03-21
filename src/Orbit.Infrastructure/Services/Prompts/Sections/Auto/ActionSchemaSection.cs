using System.Text;

namespace Orbit.Infrastructure.Services.Prompts.Sections.Auto;

public class ActionSchemaSection(ActionDiscoveryService discovery) : IPromptSection
{
    public int Order => 900;
    public bool ShouldInclude(PromptContext context) => true;

    public string Build(PromptContext context)
    {
        var today = (context.UserToday ?? DateOnly.FromDateTime(DateTime.UtcNow));
        var todayStr = today.ToString("yyyy-MM-dd");
        var tomorrowStr = today.AddDays(1).ToString("yyyy-MM-dd");
        var actions = discovery.GetActions();

        var sb = new StringBuilder();
        sb.AppendLine("## Response JSON Schema & Examples");
        sb.AppendLine();

        // Action-specific rules (auto-numbered continuing from GlobalRulesSection)
        var ruleNumber = 29;
        var hasRules = false;
        foreach (var action in actions)
        {
            foreach (var rule in action.Rules)
            {
                if (!hasRules)
                {
                    sb.AppendLine("### Action-Specific Rules");
                    sb.AppendLine();
                    hasRules = true;
                }
                sb.AppendLine($"{ruleNumber}. {rule}");
                ruleNumber++;
            }
        }
        if (hasRules)
            sb.AppendLine();

        // Examples
        sb.AppendLine("### Key Examples (one per action type):");
        sb.AppendLine();
        foreach (var action in actions)
        {
            foreach (var example in action.Examples)
            {
                var response = example.JsonResponse
                    .Replace("{TODAY}", todayStr)
                    .Replace("{TOMORROW}", tomorrowStr);

                var label = example.Note is not null
                    ? $"{action.Metadata.ActionType} ({example.Note}) -- \"{example.UserMessage}\""
                    : $"{action.Metadata.ActionType} -- \"{example.UserMessage}\"";
                sb.AppendLine(label);
                sb.AppendLine(response);
                sb.AppendLine();
            }
        }

        // CRITICAL ID warning
        var idActions = actions.Where(a => a.RequiresHabitId).Select(a => a.Metadata.ActionType).ToList();
        if (idActions.Count > 0)
        {
            sb.AppendLine($"CRITICAL: For {string.Join("/", idActions)}, use EXACT IDs from Active Habits list. NEVER fabricate IDs.");
            sb.AppendLine();
        }

        // Field docs
        sb.AppendLine("### Action Types & Required Fields:");
        sb.AppendLine();
        foreach (var action in actions)
        {
            var parts = new List<string> { "type" };
            foreach (var f in action.Fields.Where(f => f.Metadata.Required))
                parts.Add($"{f.Name} ({f.Metadata.Description})");
            foreach (var f in action.Fields.Where(f => !f.Metadata.Required))
                parts.Add($"{f.Name} ({f.Metadata.Type} - {f.Metadata.Description})");
            sb.AppendLine($"{action.Metadata.ActionType}: {string.Join(", ", parts)}");
        }
        sb.AppendLine();

        // Decision logic
        foreach (var action in actions)
        {
            if (string.IsNullOrWhiteSpace(action.Metadata.WhenToUse)) continue;
            sb.AppendLine($"**When to use {action.Metadata.ActionType}:**");
            sb.AppendLine(action.Metadata.WhenToUse.TrimEnd());
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
